using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using LaForge.Shared;
using Microsoft.Win32;

namespace LaForge.Launcher;

/// <summary>
/// LaForge Console uninstaller (<c>LaForgeConsole.exe --uninstall</c>, wired to Windows "Installed apps").
/// It copies itself to %TEMP% first so the install folder is not locked by the running uninstaller, then:
/// shuts down the Android VM / adb server / running apps cleanly, optionally removes all LaForge apps and the Android VM,
/// removes Start Menu + Desktop shortcuts and the Installed-apps registration, deletes the install folder and reports
/// anything it could not remove. Flags: <c>--silent</c> (no prompts), <c>--remove-apps</c> (with --silent).
/// </summary>
public static class Uninstaller
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LaForgeConsole";
    private const string Caption = "LaForge Console — Uninstall";

    public static int Run(string[] args)
    {
        bool silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        bool removeApps = args.Any(a => a.Equals("--remove-apps", StringComparison.OrdinalIgnoreCase));
        string? runDir = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--uninstall-run", StringComparison.OrdinalIgnoreCase)) runDir = args[i + 1];
        }

        ApplicationConfiguration.Initialize();
        return runDir == null
            ? Relaunch(silent, removeApps)
            : Execute(Path.GetFullPath(runDir), silent, removeApps);
    }

    // ---------------------------------------------------------------------------------------------

    private static string? ValidateInstallDir(string dir)
    {
        if (!File.Exists(Path.Combine(dir, "LaForgeConsole.exe")) || !Directory.Exists(Path.Combine(dir, "img")))
        {
            return "This is not a LaForge Console installation folder, so nothing was changed.";
        }
        if (Directory.Exists(Path.Combine(dir, ".git")) || Directory.Exists(Path.Combine(dir, "src")))
        {
            return "This looks like a development checkout, not an installed copy, so nothing was changed.";
        }
        return null;
    }

    private static int Relaunch(bool silent, bool removeApps)
    {
        string installDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\', '/');
        string? problem = ValidateInstallDir(installDir);
        if (problem != null)
        {
            if (!silent) MessageBox.Show(problem + $"\n\n{installDir}", Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 2;
        }

        string self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the uninstaller path.");
        string temp = Path.Combine(Path.GetTempPath(), $"LaForgeUninstall_{Guid.NewGuid():N}.exe");
        File.Copy(self, temp, overwrite: true);

        var psi = new ProcessStartInfo
        {
            FileName = temp,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false
        };
        psi.ArgumentList.Add("--uninstall-run");
        psi.ArgumentList.Add(installDir);
        if (silent) psi.ArgumentList.Add("--silent");
        if (removeApps) psi.ArgumentList.Add("--remove-apps");
        Process.Start(psi);
        return 0;
    }

    private static int Execute(string installDir, bool silent, bool removeApps)
    {
        string self = Environment.ProcessPath ?? "";
        try { Directory.SetCurrentDirectory(Path.GetTempPath()); } catch { /* ignore */ }

        string? problem = ValidateInstallDir(installDir);
        if (problem != null)
        {
            if (!silent) MessageBox.Show(problem, Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
            SelfDelete(self);
            return 2;
        }

        if (!silent)
        {
            if (MessageBox.Show(
                    $"Uninstall LaForge Console from:\n{installDir}\n\nRunning LaForge apps, the Android VM and the adb server will be closed.",
                    Caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                SelfDelete(self);
                return 0;
            }

            string avdDir = RuntimeProvisioner.GetAvdDir();
            double gb = Directory.Exists(avdDir) ? SizeOf(avdDir) / (1024.0 * 1024 * 1024) : 0;
            DialogResult answer = MessageBox.Show(
                "Do you also want to remove all LaForge apps?\n\n" +
                "YES — removes every app you forged with LaForge, its data and settings, and the LaForge Android VM" +
                (gb > 0.05 ? $" (frees about {gb:F1} GB)" : "") + ". Files in your HOST folder are kept.\n\n" +
                "NO — keeps the LaForge Android VM and your apps' data on disk. Note: the LaForge apps installed in it may not work on a new installation of LaForge.",
                Caption, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Cancel)
            {
                SelfDelete(self);
                return 0;
            }
            removeApps = answer == DialogResult.Yes;
        }

        var problems = new List<string>();
        var form = new ProgressForm();
        if (!silent) form.Show();
        Task work = Task.Run(() => DoUninstall(installDir, removeApps, form.SetStatus, problems));
        while (!work.IsCompleted)
        {
            Application.DoEvents();
            Thread.Sleep(30);
        }
        form.Close();
        if (work.IsFaulted) problems.Add(work.Exception?.GetBaseException().Message ?? "unexpected error");

        if (!silent)
        {
            string text = "LaForge Console was uninstalled.";
            if (removeApps)
            {
                text += "\n\nAll LaForge apps, their data and the LaForge Android VM were removed. Your HOST folder (%LOCALAPPDATA%\\LaForge\\HOST) was kept.";
            }
            else
            {
                text += "\n\nYou chose to keep your LaForge apps: the Android VM and your apps' data are still on disk, but their launchers were removed with this uninstall. " +
                        "Those apps may not work on a new installation of LaForge. To clean them up later, delete %LOCALAPPDATA%\\LaForge\\config and the " +
                        "\"LaForge_x86_64_API34.avd\" folder in %USERPROFILE%\\.android\\avd (or uninstall each app from LaForge Console before reinstalling).";
            }
            if (problems.Count > 0)
            {
                text += "\n\nSome items could not be removed (close anything using them, or restart Windows, then delete them manually):\n • " +
                        string.Join("\n • ", problems.Take(8)) + (problems.Count > 8 ? $"\n • …and {problems.Count - 8} more" : "");
            }
            MessageBox.Show(text, Caption, MessageBoxButtons.OK, problems.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        SelfDelete(self);
        return problems.Count > 0 ? 3 : 0;
    }

    // ---------------------------------------------------------------------------------------------

    private static void DoUninstall(string installDir, bool removeApps, Action<string> status, List<string> problems)
    {
        string localLaForge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaForge");
        List<(string Pkg, string BundleDir)> apps = removeApps ? ReadRememberedApps(localLaForge) : new();
        var bundleDirs = apps
            .Select(a => a.BundleDir)
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d) && IsForgedBundle(d) && !IsSameOrParent(d, installDir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 1. Close everything running out of the install folder (and, if requested, out of the forged app folders)
        status("Closing LaForge, the Android VM and adb...");
        string adb = Path.Combine(installDir, "bridge", "adb.exe");
        problems.AddRange(InstallProcessCleanup.StopProcessesUnder(new[] { installDir }.Concat(bundleDirs), adb, status));

        // 2. Apps + Android VM + settings
        if (removeApps)
        {
            status("Removing LaForge apps...");
            foreach (string dir in bundleDirs)
            {
                InstallProcessCleanup.SafeDeleteDirectory(dir, problems);
            }

            status("Removing the LaForge Android VM...");
            string avdDir = RuntimeProvisioner.GetAvdDir();
            string avdIni = Path.Combine(RuntimeProvisioner.GetAvdHome(), $"{RuntimeProvisioner.AvdName}.ini");
            InstallProcessCleanup.SafeDeleteDirectory(avdDir, problems);
            TryDeleteFile(avdIni, problems);

            status("Removing LaForge settings...");
            InstallProcessCleanup.SafeDeleteDirectory(Path.Combine(localLaForge, "config"), problems);
            InstallProcessCleanup.SafeDeleteDirectory(Path.Combine(localLaForge, "logs"), problems);
        }

        // 3. Shortcuts
        status("Removing shortcuts...");
        RemoveShortcuts(installDir, bundleDirs, problems);

        // 4. Installed-apps registration
        status("Removing the Windows Installed Apps entry...");
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false); }
        catch (Exception ex) { problems.Add($"Installed Apps entry: {ex.Message}"); }

        // 5. The install folder itself
        status("Deleting program files...");
        InstallProcessCleanup.SafeDeleteDirectory(installDir, problems);
    }

    private static void RemoveShortcuts(string installDir, List<string> bundleDirs, List<string> problems)
    {
        string startPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        string commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        // Our own named shortcuts
        TryDeleteFile(Path.Combine(startPrograms, "LaForge Console.lnk"), problems);
        TryDeleteFile(Path.Combine(desktop, "LaForge Console.lnk"), problems);
        TryDeleteFile(Path.Combine(commonPrograms, "LaForge Console.lnk"), problems, quiet: true);
        TryDeleteFile(Path.Combine(commonDesktop, "LaForge Console.lnk"), problems, quiet: true);
        foreach (string folder in new[] { Path.Combine(startPrograms, "LaForge"), Path.Combine(commonPrograms, "LaForge") })
        {
            if (Directory.Exists(folder))
            {
                var local = new List<string>();
                InstallProcessCleanup.SafeDeleteDirectory(folder, local);
                if (folder.StartsWith(startPrograms, StringComparison.OrdinalIgnoreCase)) problems.AddRange(local);
            }
        }

        // Any other shortcut anywhere on the desktop / Start Menu that points into the install or a removed app folder
        var roots = new List<string> { installDir };
        roots.AddRange(bundleDirs);
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;
            foreach (string dir in new[] { desktop, startPrograms })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string target = shell.CreateShortcut(lnk).TargetPath as string ?? "";
                        if (roots.Any(r => target.StartsWith(InstallProcessCleanup.NormalizeDir(r), StringComparison.OrdinalIgnoreCase)))
                        {
                            File.Delete(lnk);
                        }
                    }
                    catch { /* unreadable shortcut: leave it */ }
                }
            }
        }
        catch
        {
            // COM unavailable: the named shortcuts above are already handled
        }
    }

    // ---------------------------------------------------------------------------------------------

    private static List<(string Pkg, string BundleDir)> ReadRememberedApps(string localLaForge)
    {
        var result = new List<(string, string)>();
        string cfg = Path.Combine(localLaForge, "config");
        if (!Directory.Exists(cfg)) return result;
        foreach (string conf in Directory.EnumerateFiles(cfg, "*.conf"))
        {
            string name = Path.GetFileName(conf);
            if (name.Equals("gms_interop.conf", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("icon_sources.conf", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("master_settings.conf", StringComparison.OrdinalIgnoreCase)) continue;
            string pkg = "", dir = "";
            try
            {
                foreach (string raw in File.ReadAllLines(conf))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("package_name=", StringComparison.OrdinalIgnoreCase)) pkg = line[13..].Trim();
                    else if (line.StartsWith("bundle_dir=", StringComparison.OrdinalIgnoreCase)) dir = line[11..].Trim();
                }
            }
            catch { /* unreadable conf */ }
            if (!string.IsNullOrWhiteSpace(dir)) result.Add((pkg, dir));
        }
        return result;
    }

    /// <summary>Only ever delete folders that are unmistakably forged LaForge bundles.</summary>
    private static bool IsForgedBundle(string dir) =>
        File.Exists(Path.Combine(dir, "payload.apk")) && File.Exists(Path.Combine(dir, "config.json"));

    private static bool IsSameOrParent(string candidate, string installDir)
    {
        string c = InstallProcessCleanup.NormalizeDir(candidate);
        string i = InstallProcessCleanup.NormalizeDir(installDir);
        return i.StartsWith(c, StringComparison.OrdinalIgnoreCase); // candidate is the install dir or one of its parents
    }

    private static long SizeOf(string dir)
    {
        long total = 0;
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
        return total;
    }

    private static void TryDeleteFile(string path, List<string> problems, bool quiet = false)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            if (!quiet) problems.Add($"{path}: {ex.Message}");
        }
    }

    private static void SelfDelete(string self)
    {
        if (string.IsNullOrWhiteSpace(self) || !self.Contains("LaForgeUninstall_", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c ping 127.0.0.1 -n 4 >nul & del /f /q \"{self}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { /* the temp copy is harmless if it stays */ }
    }

    private sealed class ProgressForm : Form
    {
        private readonly Label _label;

        public ProgressForm()
        {
            Text = Caption;
            Size = new Size(460, 130);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;
            BackColor = Color.FromArgb(16, 20, 28);
            ForeColor = Color.FromArgb(235, 240, 248);
            Font = new Font("Segoe UI", 9.5F);
            Icon = LaForgeSplashOverlay.LoadBrandIcon();

            _label = new Label { Text = "Preparing...", Location = new Point(20, 18), Size = new Size(410, 24), AutoEllipsis = true };
            var bar = new ProgressBar
            {
                Location = new Point(20, 52),
                Size = new Size(410, 18),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 25
            };
            Controls.Add(_label);
            Controls.Add(bar);
        }

        public void SetStatus(string text)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(() => _label.Text = text);
                else _label.Text = text;
            }
            catch { /* form closing */ }
        }
    }
}
