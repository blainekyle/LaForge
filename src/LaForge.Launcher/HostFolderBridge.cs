using System.Reflection;

namespace LaForge.Launcher;

/// <summary>
/// Manages the dedicated host shared directory (%LOCALAPPDATA%\LaForge\HOST),
/// generates the adjacent Windows Shell Link (HOST.lnk), and runs a bidirectional
/// synchronization bridge between the host folder and /sdcard/HOST in the Android container.
/// </summary>
public sealed class HostFolderBridge : IDisposable
{
    private readonly StorageConfig _storage;
    private readonly string _adbExe;
    private readonly string _resolvedHostDir;
    private readonly string _guestDir;
    private readonly Action<string>? _logger;

    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _guestPollTimer;
    private readonly Dictionary<string, (long Mtime, long Size)> _knownGuestFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inFlightFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncLock = new();
    private bool _disposed;

    public string ResolvedHostDir => _resolvedHostDir;
    public string GuestMountDir => _guestDir;

    public HostFolderBridge(StorageConfig storage, string adbExe, Action<string>? logger = null)
    {
        _storage = storage;
        _adbExe = adbExe;
        _resolvedHostDir = ResolveHostDirectory(storage.HostFolderPath);
        _guestDir = string.IsNullOrWhiteSpace(storage.GuestMountPath)
            ? "/sdcard/HOST"
            : storage.GuestMountPath.TrimEnd('/');
        _logger = logger;
    }

    public static string ResolveHostDirectory(string configuredPath)
    {
        string raw = string.IsNullOrWhiteSpace(configuredPath)
            ? @"%LOCALAPPDATA%\LaForge\HOST"
            : configuredPath;
        string expanded = Environment.ExpandEnvironmentVariables(raw);
        return Path.GetFullPath(expanded);
    }

    public static string EnsureHostFolderAndShortcut(StorageConfig storage, string bundleOrExeDir)
    {
        string hostDir = ResolveHostDirectory(storage.HostFolderPath);
        Directory.CreateDirectory(hostDir);

        string readmePath = Path.Combine(hostDir, "README_HOST_SHARE.txt");
        if (!File.Exists(readmePath))
        {
            File.WriteAllText(
                readmePath,
                "LaForge Shared Host Folder\r\n" +
                "==========================\r\n" +
                $"Host Path : {hostDir}\r\n" +
                $"Guest Path: {storage.GuestMountPath}\r\n\r\n" +
                "Any files placed in this folder are automatically bridged to /sdcard/HOST inside\r\n" +
                "LaForge Android containers (and vice versa) when Host Mount is enabled.\r\n");
        }

        if (storage.CreateShortcut && Directory.Exists(bundleOrExeDir))
        {
            string lnkPath = Path.Combine(bundleOrExeDir, "HOST.lnk");
            if (!File.Exists(lnkPath))
            {
                CreateWindowsShortcut(lnkPath, hostDir, $"LaForge Shared Host Folder ({storage.GuestMountPath})");
            }
        }

        return hostDir;
    }

    public static void CreateWindowsShortcut(string shortcutFilePath, string targetFolderPath, string description)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            object? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            object? shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { Path.GetFullPath(shortcutFilePath) });

            if (shortcut == null) return;

            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetFullPath(targetFolderPath) });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetFullPath(targetFolderPath) });
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        catch
        {
            // Non-fatal if WScript.Shell COM is unavailable
        }
    }

    public void Start()
    {
        if (!_storage.EnableHostMount)
        {
            _logger?.Invoke($"[HostBridge] Host folder mount is disabled in config.json ({_guestDir}).");
            return;
        }

        Directory.CreateDirectory(_resolvedHostDir);

        // 1. Create /sdcard/HOST in Android container
        Program.RunProcessCaptureOutput(_adbExe, new[] { "shell", "mkdir", "-p", _guestDir }, 5000);
        _logger?.Invoke($"[HostBridge] Mounted '{_resolvedHostDir}' <-> '{_guestDir}'");

        // 2. Perform initial two-way synchronization
        PerformInitialSync();

        // 3. Start real-time Host -> Guest FileSystemWatcher
        _watcher = new FileSystemWatcher(_resolvedHostDir)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnHostFileChanged;
        _watcher.Changed += OnHostFileChanged;
        _watcher.Renamed += OnHostFileRenamed;
        _watcher.Deleted += OnHostFileDeleted;

        // 4. Start periodic Guest -> Host poll timer (every 3 seconds)
        _guestPollTimer = new System.Threading.Timer(_ => PollGuestChanges(), null, 3000, 3000);
    }

    private void PerformInitialSync()
    {
        lock (_syncLock)
        {
            var guestFiles = QueryGuestFiles();
            foreach (var kv in guestFiles)
            {
                _knownGuestFiles[kv.Key] = kv.Value;
            }

            // Push host files that don't exist on guest or have different size
            foreach (string hostFile in Directory.GetFiles(_resolvedHostDir))
            {
                string name = Path.GetFileName(hostFile);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;

                var fi = new FileInfo(hostFile);
                if (!guestFiles.TryGetValue(name, out var gInfo) || gInfo.Size != fi.Length)
                {
                    PushFileToGuest(hostFile, name);
                }
            }

            // Pull guest files that don't exist on host
            foreach (var kv in guestFiles)
            {
                string name = kv.Key;
                string localPath = Path.Combine(_resolvedHostDir, name);
                if (!File.Exists(localPath))
                {
                    PullFileFromGuest(name, localPath);
                }
            }
        }
    }

    private Dictionary<string, (long Mtime, long Size)> QueryGuestFiles()
    {
        var result = new Dictionary<string, (long Mtime, long Size)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string cmd = $"stat -c '%Y|%s|%n' {_guestDir}/* 2>/dev/null || true";
            string output = Program.RunProcessCaptureOutput(_adbExe, new[] { "shell", cmd }, 5000);
            foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                string[] parts = line.Split('|', 3);
                if (parts.Length == 3 &&
                    long.TryParse(parts[0], out long mtime) &&
                    long.TryParse(parts[1], out long size))
                {
                    string fullGuestPath = parts[2].Trim();
                    string fileName = fullGuestPath.Split('/').LastOrDefault() ?? "";
                    if (!string.IsNullOrEmpty(fileName) && fileName != "*")
                    {
                        result[fileName] = (mtime, size);
                    }
                }
            }
        }
        catch
        {
            // Ignore transient ADB poll failures
        }
        return result;
    }

    private void PushFileToGuest(string hostFilePath, string fileName)
    {
        try
        {
            if (!File.Exists(hostFilePath)) return;
            string remotePath = $"{_guestDir}/{fileName}";
            Program.RunProcessCaptureOutput(_adbExe, new[] { "push", hostFilePath, remotePath }, 15000);
            Program.RunProcessCaptureOutput(
                _adbExe,
                new[] { "shell", "am", "broadcast", "-a", "android.intent.action.MEDIA_SCANNER_SCAN_FILE", "-d", $"file://{remotePath}" },
                5000);

            var updated = QueryGuestFiles();
            if (updated.TryGetValue(fileName, out var info))
            {
                _knownGuestFiles[fileName] = info;
            }
            _logger?.Invoke($"[HostBridge -> Guest] Synced '{fileName}' to {_guestDir}/");
        }
        catch
        {
            // Ignore if file is still locked by host writer
        }
    }

    private void PullFileFromGuest(string fileName, string localDestPath)
    {
        try
        {
            lock (_inFlightFiles)
            {
                _inFlightFiles.Add(fileName);
            }
            string remotePath = $"{_guestDir}/{fileName}";
            Program.RunProcessCaptureOutput(_adbExe, new[] { "pull", remotePath, localDestPath }, 15000);
            _logger?.Invoke($"[HostBridge <- Guest] Synced '{fileName}' from {_guestDir}/");
        }
        catch
        {
            // Ignore transient pull errors
        }
        finally
        {
            lock (_inFlightFiles)
            {
                _inFlightFiles.Remove(fileName);
            }
        }
    }

    private void OnHostFileChanged(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Name)) return;
        lock (_inFlightFiles)
        {
            if (_inFlightFiles.Contains(e.Name)) return;
        }

        Task.Run(async () =>
        {
            await Task.Delay(250);
            lock (_syncLock)
            {
                if (File.Exists(e.FullPath))
                {
                    PushFileToGuest(e.FullPath, e.Name);
                }
            }
        });
    }

    private void OnHostFileRenamed(object sender, RenamedEventArgs e)
    {
        Task.Run(() =>
        {
            lock (_syncLock)
            {
                if (!string.IsNullOrEmpty(e.OldName))
                {
                    Program.RunProcessCaptureOutput(_adbExe, new[] { "shell", "rm", "-f", $"{_guestDir}/{e.OldName}" }, 5000);
                    _knownGuestFiles.Remove(e.OldName);
                }
                if (!string.IsNullOrEmpty(e.Name) && File.Exists(e.FullPath))
                {
                    PushFileToGuest(e.FullPath, e.Name);
                }
            }
        });
    }

    private void OnHostFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Name)) return;
        Task.Run(() =>
        {
            lock (_syncLock)
            {
                Program.RunProcessCaptureOutput(_adbExe, new[] { "shell", "rm", "-f", $"{_guestDir}/{e.Name}" }, 5000);
                _knownGuestFiles.Remove(e.Name);
                _logger?.Invoke($"[HostBridge -> Guest] Removed '{e.Name}' from {_guestDir}/");
            }
        });
    }

    private void PollGuestChanges()
    {
        if (_disposed) return;
        if (!Monitor.TryEnter(_syncLock)) return;
        try
        {
            var currentGuest = QueryGuestFiles();
            foreach (var kv in currentGuest)
            {
                string name = kv.Key;
                var info = kv.Value;

                if (!_knownGuestFiles.TryGetValue(name, out var prev) ||
                    prev.Mtime != info.Mtime ||
                    prev.Size != info.Size)
                {
                    _knownGuestFiles[name] = info;
                    string localPath = Path.Combine(_resolvedHostDir, name);
                    PullFileFromGuest(name, localPath);
                }
            }

            // Detect files deleted on the guest
            var removedOnGuest = _knownGuestFiles.Keys.Except(currentGuest.Keys, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string removed in removedOnGuest)
            {
                _knownGuestFiles.Remove(removed);
            }
        }
        finally
        {
            Monitor.Exit(_syncLock);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher?.Dispose();
        _guestPollTimer?.Dispose();
    }
}
