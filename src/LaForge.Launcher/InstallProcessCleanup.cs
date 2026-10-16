using System.Diagnostics;

namespace LaForge.Shared;

/// <summary>
/// Shared by the launcher (uninstaller) and the installer (upgrade/reinstall): stops everything that is running out of
/// an install folder gracefully before its files are replaced or deleted, and deletes folders without ever following
/// a junction/symlink into somewhere else (e.g. a developer's workspace).
/// </summary>
public static class InstallProcessCleanup
{
    public static string NormalizeDir(string dir) =>
        Path.GetFullPath(dir).TrimEnd('\\', '/') + "\\";

    private static bool IsUnder(string? path, string normalizedDir) =>
        !string.IsNullOrEmpty(path) && path.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase);

    private static string? ImagePath(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch { return null; } // access denied / process already exiting / bitness mismatch
    }

    private static void RunQuiet(string exe, string args, int timeoutMs)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc == null) return;
            proc.StandardOutput.ReadToEndAsync();
            proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
        }
        catch
        {
            // Best effort
        }
    }

    /// <summary>
    /// Stops the Android emulator and the adb server through the bundled adb (so they shut down cleanly), then closes and,
    /// if needed, kills every process whose executable lives under one of <paramref name="dirs"/>.
    /// Returns a description of every process that could not be stopped.
    /// </summary>
    public static List<string> StopProcessesUnder(IEnumerable<string> dirs, string? adbExe, Action<string>? log = null)
    {
        var failures = new List<string>();
        List<string> roots = dirs
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(NormalizeDir)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        int selfId = Environment.ProcessId;

        static bool IsBackend(string image)
        {
            string n = Path.GetFileName(image);
            return n.StartsWith("adb", StringComparison.OrdinalIgnoreCase) ||
                   n.StartsWith("emulator", StringComparison.OrdinalIgnoreCase) ||
                   n.StartsWith("qemu", StringComparison.OrdinalIgnoreCase) ||
                   n.StartsWith("crashpad", StringComparison.OrdinalIgnoreCase);
        }

        List<(Process Proc, string Image)> Find(bool backend)
        {
            var found = new List<(Process, string)>();
            foreach (Process p in Process.GetProcesses())
            {
                string? image = p.Id == selfId ? null : ImagePath(p);
                if (image != null && roots.Any(r => IsUnder(image, r)) && IsBackend(image) == backend)
                {
                    found.Add((p, image));
                }
                else
                {
                    p.Dispose();
                }
            }
            return found;
        }

        void Stop(List<(Process Proc, string Image)> victims, bool graceful)
        {
            if (graceful)
            {
                foreach (var v in victims)
                {
                    try { if (v.Proc.MainWindowHandle != IntPtr.Zero) v.Proc.CloseMainWindow(); } catch { /* ignore */ }
                }
                DateTime graceDeadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < graceDeadline && victims.Any(v => SafeRunning(v.Proc)))
                {
                    Thread.Sleep(200);
                }
            }
            foreach (var v in victims)
            {
                string name = Path.GetFileName(v.Image);
                try
                {
                    if (SafeRunning(v.Proc))
                    {
                        log?.Invoke($"Stopping {name}...");
                        v.Proc.Kill(entireProcessTree: true);
                        if (!v.Proc.WaitForExit(4000)) failures.Add($"{name} did not stop");
                    }
                }
                catch (Exception ex)
                {
                    if (SafeRunning(v.Proc)) failures.Add($"{name}: {ex.Message}");
                }
                finally
                {
                    v.Proc.Dispose();
                }
            }
        }

        // 1. The Console, forged-app launchers and scrcpy windows first, so nothing is left to restart the backend.
        Stop(Find(backend: false), graceful: true);

        // 2. Then shut the Android VM and the adb server down cleanly through the bundled adb.
        if (!string.IsNullOrWhiteSpace(adbExe) && File.Exists(adbExe))
        {
            log?.Invoke("Shutting down the Android VM and adb server...");
            RunQuiet(adbExe, "emu kill", 6000);
            Thread.Sleep(1500);
            RunQuiet(adbExe, "kill-server", 6000);
        }

        // 3. Sweep whatever is still running (emulator/qemu/adb that ignored the polite request, or anything respawned).
        for (int pass = 0; pass < 4; pass++)
        {
            var leftovers = Find(backend: true);
            leftovers.AddRange(Find(backend: false));
            if (leftovers.Count == 0)
            {
                failures.Clear();
                break;
            }
            failures.Clear(); // only the final state matters
            Stop(leftovers, graceful: false);
            Thread.Sleep(700);
        }
        return failures;
    }

    private static bool SafeRunning(Process p)
    {
        try { return !p.HasExited; } catch { return false; }
    }

    /// <summary>
    /// Deletes a directory tree. Junctions and symlinks are removed as links and never traversed, files are made
    /// writable first, and every failure is recorded instead of being swallowed.
    /// </summary>
    public static void SafeDeleteDirectory(string path, List<string> failures)
    {
        var di = new DirectoryInfo(path);
        if (!di.Exists) return;

        if (di.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            try { di.Delete(recursive: false); }
            catch (Exception ex) { failures.Add($"{path}: {ex.Message}"); }
            return;
        }

        foreach (FileInfo f in di.EnumerateFiles())
        {
            DeleteWithRetry(() =>
            {
                f.Attributes = FileAttributes.Normal;
                f.Delete();
            }, f.FullName, failures);
        }

        foreach (DirectoryInfo d in di.EnumerateDirectories())
        {
            SafeDeleteDirectory(d.FullName, failures);
        }

        DeleteWithRetry(() =>
        {
            di.Attributes = FileAttributes.Normal;
            di.Delete(recursive: false);
        }, path, failures);
    }

    private static void DeleteWithRetry(Action action, string label, List<string> failures)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(400);
            }
        }
        failures.Add($"{label}: {last?.Message}");
    }
}
