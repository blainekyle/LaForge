using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace LaForge.Launcher;

public sealed class LaForgeConfig
{
    [JsonPropertyName("app")]
    public AppConfig App { get; set; } = new();

    [JsonPropertyName("runtime")]
    public RuntimeConfig Runtime { get; set; } = new();

    [JsonPropertyName("display")]
    public DisplayConfig Display { get; set; } = new();

    [JsonPropertyName("storage")]
    public StorageConfig Storage { get; set; } = new();

    [JsonPropertyName("relays")]
    public RelaysConfig Relays { get; set; } = new();
}

public sealed class AppConfig
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "LaForge Standalone App";

    [JsonPropertyName("executable_name")]
    public string ExecutableName { get; set; } = "launcher.exe";

    [JsonPropertyName("icon_path")]
    public string IconPath { get; set; } = "./app.ico";

    [JsonPropertyName("apk_path")]
    public string ApkPath { get; set; } = "./payload.apk";

    [JsonPropertyName("package_name")]
    public string PackageName { get; set; } = "";

    [JsonPropertyName("activity_name")]
    public string ActivityName { get; set; } = "";
}

public sealed class RuntimeConfig
{
    [JsonPropertyName("emulator_path")]
    public string EmulatorPath { get; set; } = "emulator";

    [JsonPropertyName("adb_path")]
    public string AdbPath { get; set; } = "adb";

    [JsonPropertyName("avd_name")]
    public string AvdName { get; set; } = "LaForge_x86_64_API34";

    [JsonPropertyName("snapshot")]
    public string Snapshot { get; set; } = "default_boot";

    [JsonPropertyName("gpu_mode")]
    public string GpuMode { get; set; } = "host";

    [JsonPropertyName("whpx")]
    public bool Whpx { get; set; } = true;

    [JsonPropertyName("headless")]
    public bool Headless { get; set; } = true;

    [JsonPropertyName("boot_timeout_sec")]
    public int BootTimeoutSec { get; set; } = 60;
}

public sealed class DisplayConfig
{
    [JsonPropertyName("scrcpy_path")]
    public string ScrcpyPath { get; set; } = "bridge/scrcpy.exe";

    [JsonPropertyName("render_driver")]
    public string RenderDriver { get; set; } = "direct3d";

    [JsonPropertyName("window")]
    public WindowConfig Window { get; set; } = new();

    [JsonPropertyName("vsync")]
    public bool Vsync { get; set; } = true;

    [JsonPropertyName("max_fps")]
    public int MaxFps { get; set; } = 60;

    [JsonPropertyName("stay_awake")]
    public bool StayAwake { get; set; } = true;

    [JsonPropertyName("auto_hide_console")]
    public bool AutoHideConsole { get; set; } = true;
}

public sealed class WindowConfig
{
    [JsonPropertyName("x")]
    public int X { get; set; } = 100;

    [JsonPropertyName("y")]
    public int Y { get; set; } = 100;

    [JsonPropertyName("width")]
    public int Width { get; set; } = 1280;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 720;

    [JsonPropertyName("borderless")]
    public bool Borderless { get; set; } = true;

    [JsonPropertyName("always_on_top")]
    public bool AlwaysOnTop { get; set; }

    [JsonPropertyName("fullscreen")]
    public bool Fullscreen { get; set; }
}

public sealed class StorageConfig
{
    [JsonPropertyName("enable_host_mount")]
    public bool EnableHostMount { get; set; } = true;

    [JsonPropertyName("host_folder_path")]
    public string HostFolderPath { get; set; } = @"%LOCALAPPDATA%\LaForge\HOST";

    [JsonPropertyName("guest_mount_path")]
    public string GuestMountPath { get; set; } = "/sdcard/HOST";

    [JsonPropertyName("create_shortcut")]
    public bool CreateShortcut { get; set; } = true;
}

public sealed class RelaysConfig
{
    [JsonPropertyName("remote_staging_dir")]
    public string RemoteStagingDir { get; set; } = "/sdcard/Download";

    [JsonPropertyName("associations")]
    public Dictionary<string, RelayRule> Associations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RelayRule
{
    [JsonPropertyName("mime_type")]
    public string MimeType { get; set; } = "application/octet-stream";

    [JsonPropertyName("action")]
    public string Action { get; set; } = "android.intent.action.VIEW";
}

public static class Program
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string configPath = Path.Combine(baseDir, "config.json");
            if (!File.Exists(configPath))
            {
                configPath = Path.Combine(Directory.GetCurrentDirectory(), "config.json");
            }

            bool dryRun = false;
            bool cliMode = false;
            bool relayOnly = false;
            bool consoleMode = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "")
                .Equals("LaForgeConsole", StringComparison.OrdinalIgnoreCase);
            bool forgeHostMount = true;
            string? relayFile = null;
            string? inspectApk = null;
            string? forgeApk = null;
            string forgeOutDir = Path.Combine(Directory.GetCurrentDirectory(), "dist");

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("--dry-run", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--verify", StringComparison.OrdinalIgnoreCase))
                {
                    dryRun = true;
                    cliMode = true;
                }
                else if (arg.Equals("--console", StringComparison.OrdinalIgnoreCase))
                {
                    consoleMode = true;
                }
                else if (arg.Equals("--cli", StringComparison.OrdinalIgnoreCase))
                {
                    cliMode = true;
                }
                else if (arg.Equals("--relay-only", StringComparison.OrdinalIgnoreCase))
                {
                    relayOnly = true;
                    cliMode = true;
                }
                else if (arg.Equals("--no-host-mount", StringComparison.OrdinalIgnoreCase))
                {
                    forgeHostMount = false;
                }
                else if (arg.Equals("--host-mount", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    forgeHostMount = !args[++i].Equals("false", StringComparison.OrdinalIgnoreCase);
                }
                else if (arg.Equals("--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    configPath = args[++i];
                }
                else if (arg.Equals("--relay", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    relayFile = args[++i];
                }
                else if (arg.Equals("--inspect", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inspectApk = args[++i];
                    cliMode = true;
                }
                else if (arg.Equals("--forge", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    forgeApk = args[++i];
                    cliMode = true;
                }
                else if (arg.Equals("--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    forgeOutDir = args[++i];
                }
                else if (!arg.StartsWith('-'))
                {
                    relayFile = arg;
                }
            }

            if (consoleMode)
            {
                IntPtr consoleWnd = GetConsoleWindow();
                if (consoleWnd != IntPtr.Zero)
                {
                    ShowWindow(consoleWnd, SW_HIDE);
                }

                ApplicationConfiguration.Initialize();
                Application.Run(new LaForgeConsoleWindow(baseDir));
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(inspectApk))
            {
                ApkMetadata meta = ApkManifestParser.ParseApk(inspectApk);
                Console.WriteLine($"[LaForge APK Inspector]");
                Console.WriteLine($"  APK File          : {meta.ApkPath}");
                Console.WriteLine($"  Package Name      : {meta.PackageName}");
                Console.WriteLine($"  Version           : {meta.VersionName} ({meta.VersionCode})");
                Console.WriteLine($"  Launcher Activity : {meta.LauncherActivity}");
                Console.WriteLine($"  Total Activities  : {meta.AllActivities.Count}");
                Console.WriteLine($"  VIEW MIME Types   : {(meta.MimeTypes.Count > 0 ? string.Join(", ", meta.MimeTypes) : "(none declared)")}");
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(forgeApk))
            {
                ApkMetadata meta = ApkManifestParser.ParseApk(forgeApk);
                string bundleDir = ApkPackager.ForgeBundle(forgeApk, forgeOutDir, forgeHostMount);
                Console.WriteLine($"[LaForge APK Packager] Forged standalone bundle:");
                Console.WriteLine($"  Source APK        : {meta.ApkPath}");
                Console.WriteLine($"  Package Name      : {meta.PackageName} (v{meta.VersionName})");
                Console.WriteLine($"  Launcher Activity : {meta.LauncherActivity}");
                Console.WriteLine($"  Host Share Mount  : {(forgeHostMount ? "Enabled (/sdcard/HOST)" : "Disabled")}");
                Console.WriteLine($"  Output Bundle Dir : {bundleDir}");
                return 0;
            }

            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"[LaForge Error] Configuration file not found: {configPath}");
                return 1;
            }

            string fullConfigPath = Path.GetFullPath(configPath);
            string configDir = Path.GetDirectoryName(fullConfigPath) ?? baseDir;
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
            var config = JsonSerializer.Deserialize<LaForgeConfig>(File.ReadAllText(fullConfigPath), jsonOptions)
                         ?? throw new InvalidOperationException("Failed to deserialize config.json.");

            ValidateWindowBoundaries(config.Display.Window);

            // Ensure dedicated %LOCALAPPDATA%\LaForge\HOST directory and adjacent HOST.lnk shortcut exist
            string resolvedHostShare = HostFolderBridge.EnsureHostFolderAndShortcut(config.Storage, configDir);

            // Interactive Desktop Mode (when launched from Explorer without --dry-run / --cli)
            if (!dryRun && !cliMode)
            {
                IntPtr consoleWnd = GetConsoleWindow();
                if (consoleWnd != IntPtr.Zero)
                {
                    ShowWindow(consoleWnd, SW_HIDE);
                }

                string checkApkPath = Path.IsPathRooted(config.App.ApkPath)
                    ? config.App.ApkPath
                    : Path.GetFullPath(Path.Combine(configDir, config.App.ApkPath));

                // If launched from the repo root where config.json is the CLI test harness (no payload.apk), open LaForge Console
                if (!File.Exists(checkApkPath))
                {
                    ApplicationConfiguration.Initialize();
                    Application.Run(new LaForgeConsoleWindow(baseDir));
                    return 0;
                }

                // Fast-path: if this app's scrcpy window is already running or backgrounded (SW_HIDE), restore & focus it in 0ms
                if (string.IsNullOrWhiteSpace(relayFile))
                {
                    var existingPolicy = LaForgeAppRegistry.LoadOrCreate(config, configDir);
                    if (LaForgeAppRegistry.TryRestoreOrFocusAppWindow(existingPolicy))
                    {
                        return 0;
                    }
                }

                ApplicationConfiguration.Initialize();
                Application.Run(new LaForgeWindow(config, fullConfigPath, configDir, relayFile));
                return 0;
            }

            string adbExe = dryRun ? config.Runtime.AdbPath : ResolveToolPath(configDir, config.Runtime.AdbPath, "adb.exe");
            string scrcpyExe = dryRun ? config.Display.ScrcpyPath : ResolveToolPath(configDir, config.Display.ScrcpyPath, "scrcpy.exe");
            string emulatorExe = dryRun ? config.Runtime.EmulatorPath : ResolveEmulatorPath(configDir, config.Runtime.EmulatorPath);

            if (!relayOnly)
            {
                // Step 1: Launch headless Android runtime via subprocess (-snapshot for fast boot)
                var emuArgs = BuildEmulatorArgs(config.Runtime);
                Console.WriteLine($"[LaForge] Step 1/4: Booting Headless Android Runtime...");
                Console.WriteLine($"  Command: {config.Runtime.EmulatorPath} {string.Join(' ', emuArgs)}");
                if (!dryRun)
                {
                    if (string.IsNullOrEmpty(GetConnectedAdbDevice(adbExe)))
                    {
                        StartBackgroundProcess(emulatorExe, emuArgs, hidden: true);
                    }
                    WaitForDeviceBoot(adbExe, config.Runtime.BootTimeoutSec);
                    ConfigureGuestDisplay(adbExe, config.Display.Window);
                }

                // Host Folder Bridge (%LOCALAPPDATA%\LaForge\HOST <-> /sdcard/HOST)
                if (config.Storage.EnableHostMount)
                {
                    Console.WriteLine($"[LaForge] Host Share Mount: {resolvedHostShare} <-> {config.Storage.GuestMountPath}");
                    if (!dryRun)
                    {
                        using var bridge = new HostFolderBridge(config.Storage, adbExe, Console.WriteLine);
                        bridge.Start();
                    }
                }

                // Step 2: Initialize display bridge (scrcpy + SDL2/Direct3D)
                var scrcpyArgsDisplay = BuildScrcpyArgs(config, forExecution: false);
                var scrcpyArgsExec = BuildScrcpyArgs(config, forExecution: true);
                var win = config.Display.Window;
                Console.WriteLine($"[LaForge] Step 2/4: Initializing Display Bridge (scrcpy + SDL2/Direct3D)...");
                Console.WriteLine($"  Window Boundaries: X={win.X}, Y={win.Y}, W={win.Width}, H={win.Height}, Borderless={win.Borderless}");
                Console.WriteLine($"  Command: {config.Display.ScrcpyPath} {string.Join(' ', scrcpyArgsDisplay)}");
                if (!dryRun)
                {
                    string scrcpyIconPath = Path.Combine(configDir, "bridge", "scrcpy.png");
                    EnsureSingleScrcpyInstance(scrcpyExe, scrcpyArgsExec, scrcpyIconPath);
                }

                // Step 3: Sideload payload.apk (if present) and execute target APK via 'adb shell am start'
                string resolvedApkPath = Path.IsPathRooted(config.App.ApkPath)
                    ? config.App.ApkPath
                    : Path.GetFullPath(Path.Combine(configDir, config.App.ApkPath));

                if (File.Exists(resolvedApkPath))
                {
                    Console.WriteLine($"[LaForge] Sideloading Payload APK: {resolvedApkPath}");
                    Console.WriteLine($"  Install Cmd: {config.Runtime.AdbPath} install -r \"{resolvedApkPath}\"");
                    if (!dryRun)
                    {
                        SideloadAndGrantPermissions(adbExe, resolvedApkPath, config.App.PackageName);
                    }
                }

                var apkArgs = BuildApkLaunchArgs(config.App);
                Console.WriteLine($"[LaForge] Step 3/4: Launching Target APK...");
                Console.WriteLine($"  Command: {config.Runtime.AdbPath} {string.Join(' ', apkArgs)}");
                if (!dryRun)
                {
                    LaunchApkWithRetry(adbExe, config.App, 15);
                }
            }

            // Step 4: Relay System (Windows file association -> adb push -> ACTION_VIEW intent)
            if (!string.IsNullOrWhiteSpace(relayFile))
            {
                Console.WriteLine($"[LaForge] Step 4/4: Executing File Association Relay...");
                var (pushArgs, intentArgs, remoteUri, mimeType) = BuildRelayArgs(config, relayFile);
                Console.WriteLine($"  Host File  : {Path.GetFullPath(relayFile)}");
                Console.WriteLine($"  Remote URI : {remoteUri}");
                Console.WriteLine($"  MIME Type  : {mimeType}");
                Console.WriteLine($"  Push Cmd   : {config.Runtime.AdbPath} {FormatArgsForDisplay(pushArgs)}");
                Console.WriteLine($"  Intent Cmd : {config.Runtime.AdbPath} {string.Join(' ', intentArgs)}");

                if (!dryRun)
                {
                    RunCheckedProcess(adbExe, pushArgs);
                    RunCheckedProcess(adbExe, intentArgs);
                }
            }
            else
            {
                Console.WriteLine($"[LaForge] Step 4/4: No initial relay file specified (standing by).");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LaForge Error] {ex.Message}");
            return 1;
        }
    }

    public static string ResolveToolPath(string configDir, string configuredPath, string fallbackBridgeFileName)
    {
        if (Path.IsPathRooted(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        string relToConfig = Path.GetFullPath(Path.Combine(configDir, configuredPath));
        if (File.Exists(relToConfig))
        {
            return relToConfig;
        }

        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(configDir, "bridge", fallbackBridgeFileName)),
            Path.GetFullPath(Path.Combine(configDir, "..", "bridge", fallbackBridgeFileName)),
            Path.GetFullPath(Path.Combine(configDir, "..", "..", "bridge", fallbackBridgeFileName)),
            Path.GetFullPath(Path.Combine(configDir, "..", "..", "..", "bridge", fallbackBridgeFileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "bridge", fallbackBridgeFileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "bridge", fallbackBridgeFileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "bridge", fallbackBridgeFileName))
        };

        foreach (string c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        try
        {
            if (Directory.Exists(configDir))
            {
                foreach (string subDir in Directory.GetDirectories(configDir))
                {
                    string subBridge = Path.Combine(subDir, "bridge", fallbackBridgeFileName);
                    if (File.Exists(subBridge))
                    {
                        return Path.GetFullPath(subBridge);
                    }
                }
            }
        }
        catch { }

        return configuredPath;
    }

    public static string ResolveEmulatorPath(string configDir, string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        string[] candidates =
        {
            Path.GetFullPath(Path.Combine(configDir, "runtime", "emulator", "emulator.exe")),
            Path.GetFullPath(Path.Combine(configDir, "..", "runtime", "emulator", "emulator.exe")),
            Path.GetFullPath(Path.Combine(configDir, "..", "..", "runtime", "emulator", "emulator.exe")),
            Path.GetFullPath(Path.Combine(configDir, "..", "..", "..", "runtime", "emulator", "emulator.exe")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "emulator", "emulator.exe")
        };

        foreach (string c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        return configuredPath;
    }

    public static string GetConnectedAdbDevice(string adbExe)
    {
        try
        {
            if (!File.Exists(adbExe)) return "";
            string output = RunProcessCaptureOutput(adbExe, new[] { "devices" }, timeoutMs: 8000);

            foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith('*'))
                {
                    continue;
                }
                string[] parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1].Trim().Equals("device", StringComparison.OrdinalIgnoreCase))
                {
                    return parts[0].Trim();
                }
            }
        }
        catch
        {
            // Ignore when adb is unavailable
        }
        return "";
    }

    public static void ConfigureGuestDisplay(string adbExe, WindowConfig win)
    {
        try
        {
            string alreadyConfigured = RunProcessCaptureOutput(
                adbExe,
                new[] { "shell", "getprop", "debug.laforge.display_configured" },
                3000).Trim();

            if (alreadyConfigured != "1")
            {
                string oneTimeCmd =
                    $"wm size {win.Width}x{win.Height}; " +
                    "wm density 160; " +
                    "pm disable-user --user 0 com.android.inputmethod.latin >/dev/null 2>&1; " +
                    "settings put global window_animation_scale 0; " +
                    "settings put global transition_animation_scale 0; " +
                    "settings put global animator_duration_scale 0; " +
                    "settings put system accelerometer_rotation 0; " +
                    "settings put secure icon_blacklist wifi,mobile,battery,airplane,bluetooth,alarm_clock,volume,zen,managed_profile,cast,hotspot,location,rotate,headset; " +
                    "setprop debug.laforge.display_configured 1";
                RunProcessCaptureOutput(adbExe, new[] { "shell", oneTimeCmd }, 6000);
                RunProcessCaptureOutput(adbExe, new[] { "emu", "avd", "hostmicon" }, 3000);
            }

            string kioskLockCmd =
                "wm dismiss-keyguard; " +
                "cmd statusbar collapse; " +
                "cmd statusbar send-disable-flag statusbar-expansion home recents notification-peek system-icons clock notification-icons search";
            RunProcessCaptureOutput(adbExe, new[] { "shell", kioskLockCmd }, 4000);
        }
        catch
        {
            // Non-fatal if device doesn't allow wm/statusbar overrides
        }
    }

    public static void SideloadAndGrantPermissions(string adbExe, string apkPath, string packageName)
    {
        bool alreadyInstalled = false;
        if (!string.IsNullOrWhiteSpace(packageName))
        {
            try
            {
                string pmOut = RunProcessCaptureOutput(
                    adbExe,
                    new[] { "shell", $"pm path {packageName}; appops set {packageName} MANAGE_EXTERNAL_STORAGE allow 2>/dev/null" },
                    10000).Trim();
                alreadyInstalled = pmOut.Contains("package:", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Ignore
            }
        }

        if (!alreadyInstalled)
        {
            RunCheckedProcess(adbExe, new[] { "install", "-r", "-g", apkPath });
            try
            {
                RunProcessCaptureOutput(adbExe, new[] { "shell", "appops", "set", packageName, "MANAGE_EXTERNAL_STORAGE", "allow" }, 4000);
            }
            catch
            {
                // Ignore if app does not declare MANAGE_EXTERNAL_STORAGE
            }
        }
    }

    public static string LaunchApkWithRetry(string adbExe, AppConfig app, int maxWaitSec)
    {
        var apkArgs = BuildApkLaunchArgs(app);
        DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSec);
        string lastOut = "";

        while (DateTime.UtcNow < deadline)
        {
            lastOut = RunProcessCaptureOutput(adbExe, apkArgs, 10000);
            if (lastOut.Contains("Status: ok", StringComparison.OrdinalIgnoreCase) &&
                !lastOut.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            {
                return "Status: ok";
            }
            Thread.Sleep(1000);
        }

        return string.IsNullOrWhiteSpace(lastOut) ? "Completed" : lastOut.Trim();
    }

    public static Process EnsureSingleScrcpyInstance(
        string scrcpyExe,
        IEnumerable<string> scrcpyArgs,
        string? iconPath = null,
        string? appTitle = null,
        int previousPid = 0)
    {
        try
        {
            var matchingWinPids = new HashSet<int>();
            if (!string.IsNullOrWhiteSpace(appTitle))
            {
                foreach (var w in LaForgeAppRegistry.FindAllScrcpyWindows())
                {
                    if (w.Title.Equals(appTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingWinPids.Add(w.ProcessId);
                    }
                }
            }

            foreach (var existing in Process.GetProcessesByName("scrcpy"))
            {
                try
                {
                    bool sameApp = (previousPid > 0 && existing.Id == previousPid) ||
                                   matchingWinPids.Contains(existing.Id) ||
                                   (!string.IsNullOrWhiteSpace(appTitle) &&
                                    existing.MainWindowTitle.Equals(appTitle, StringComparison.OrdinalIgnoreCase));
                    if (sameApp)
                    {
                        existing.Kill();
                        existing.WaitForExit(1500);
                    }
                }
                catch
                {
                    // Ignore
                }
            }
        }
        catch
        {
            // Ignore
        }

        var envVars = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
        {
            envVars["SCRCPY_ICON_PATH"] = Path.GetFullPath(iconPath);
        }

        return StartBackgroundProcess(scrcpyExe, scrcpyArgs, hidden: false, envVars);
    }

    public static void ValidateWindowBoundaries(WindowConfig win)
    {
        if (win.Width < 200 || win.Width > 7680)
        {
            throw new ArgumentOutOfRangeException(nameof(win.Width), $"Invalid window width ({win.Width}). Must be between 200 and 7680 pixels.");
        }
        if (win.Height < 200 || win.Height > 4320)
        {
            throw new ArgumentOutOfRangeException(nameof(win.Height), $"Invalid window height ({win.Height}). Must be between 200 and 4320 pixels.");
        }
        if (win.X < -3840 || win.X > 7680 || win.Y < -2160 || win.Y > 4320)
        {
            throw new ArgumentOutOfRangeException(nameof(win.X), $"Window coordinates ({win.X}, {win.Y}) are out of valid desktop bounds.");
        }
    }

    public static List<string> BuildEmulatorArgs(RuntimeConfig rt)
    {
        var list = new List<string> { "-avd", rt.AvdName };
        if (rt.Headless)
        {
            list.Add("-no-window");
        }
        list.AddRange(new[] { "-gpu", rt.GpuMode });
        if (!string.IsNullOrWhiteSpace(rt.Snapshot))
        {
            list.AddRange(new[] { "-snapshot", rt.Snapshot });
        }
        list.AddRange(new[] { "-no-snapshot-save", "-no-boot-anim", "-no-audio" });
        if (rt.Whpx)
        {
            list.AddRange(new[] { "-feature", "WindowsHypervisorPlatform" });
        }
        return list;
    }

    public static void StopHeadlessEmulator(string? adbExe = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(adbExe) && File.Exists(adbExe))
            {
                RunProcessCaptureOutput(adbExe, new[] { "emu", "kill" }, 2000);
            }
        }
        catch
        {
            // Ignore
        }

        try
        {
            foreach (var proc in Process.GetProcessesByName("qemu-system-x86_64-headless"))
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore
                }
            }
        }
        catch
        {
            // Ignore
        }
    }

    public static void StopEmulatorIfNoActiveSessions(string? adbExe = null, int ignoreScrcpyPid = 0)
    {
        try
        {
            if (LaForgeAppRegistry.IsConsoleServiceRunning())
            {
                return;
            }

            bool anyOtherScrcpy = Process.GetProcessesByName("scrcpy")
                .Any(p =>
                {
                    try
                    {
                        return !p.HasExited && p.Id != ignoreScrcpyPid;
                    }
                    catch
                    {
                        return false;
                    }
                });

            if (!anyOtherScrcpy)
            {
                StopHeadlessEmulator(adbExe);
            }
        }
        catch
        {
            // Ignore
        }
    }

    public static List<string> BuildScrcpyArgs(LaForgeConfig cfg, bool forExecution = false, AppPolicyConfig? policy = null)
    {
        var disp = cfg.Display;
        var win = disp.Window;
        string titleArg = forExecution
            ? $"--window-title={cfg.App.Title}"
            : $"--window-title=\"{cfg.App.Title}\"";

        int winX = win.X;
        int winY = win.Y;
        int otherScrcpyCount = 0;

        if (forExecution)
        {
            try
            {
                int prevPid = policy?.LastScrcpyPid ?? 0;
                otherScrcpyCount = Process.GetProcessesByName("scrcpy")
                    .Count(p => p.Id != prevPid &&
                                !p.MainWindowTitle.Equals(cfg.App.Title, StringComparison.OrdinalIgnoreCase));
                if (otherScrcpyCount > 0)
                {
                    int offset = (otherScrcpyCount % 6) * 38;
                    winX += offset;
                    winY += offset;
                }
            }
            catch
            {
                // Ignore
            }
        }

        var list = new List<string>
        {
            titleArg,
            $"--window-x={winX}",
            $"--window-y={winY}",
            $"--window-width={win.Width}",
            $"--window-height={win.Height}"
        };

        if (!forExecution || !disp.RenderDriver.Equals("direct3d", StringComparison.OrdinalIgnoreCase))
        {
            list.Add($"--render-driver={disp.RenderDriver}");
        }

        list.Add($"--max-fps={disp.MaxFps}");

        if (win.Borderless)
        {
            list.Add("--window-borderless");
        }
        if (win.AlwaysOnTop)
        {
            list.Add("--always-on-top");
        }
        if (win.Fullscreen)
        {
            list.Add("--fullscreen");
        }
        if (disp.StayAwake)
        {
            list.Add("--stay-awake");
        }

        if (forExecution)
        {
            bool useVirtualDisplay = policy?.VirtualDisplay ?? true;
            if (useVirtualDisplay && !string.IsNullOrWhiteSpace(cfg.App.PackageName))
            {
                list.Add($"--new-display={win.Width}x{win.Height}/160");
                list.Add("--no-vd-system-decorations");
            }

            if ((policy?.MuteAudio ?? false) || otherScrcpyCount > 0)
            {
                list.Add("--no-audio");
            }
            else
            {
                list.Add("--audio-codec=raw");
            }

            if (policy?.BlockHid ?? false)
            {
                list.Add("--no-control");
            }
        }

        return list;
    }

    public static List<string> BuildApkLaunchArgs(AppConfig app, int displayId = -1)
    {
        string directActivity = ApkManifestParser.ResolveDirectActivity(app.PackageName, app.ActivityName);
        string component = $"{app.PackageName}/{directActivity}";
        if (displayId > 0)
        {
            return new List<string> { "shell", "am", "start", "--display", displayId.ToString(), "-n", component };
        }
        return new List<string> { "shell", "cmd", "activity", "start", "-W", "-n", component };
    }

    public static HashSet<int> GetActiveScrcpyDisplayIds(string adbExe)
    {
        var ids = new HashSet<int>();
        try
        {
            if (!File.Exists(adbExe)) return ids;
            string dump = RunProcessCaptureOutput(
                adbExe,
                new[] { "shell", "dumpsys display | grep scrcpy" },
                3000);
            foreach (Match m in Regex.Matches(dump, @"displayId\s+(\d+)", RegexOptions.IgnoreCase))
            {
                if (int.TryParse(m.Groups[1].Value, out int id) && id > 0)
                {
                    ids.Add(id);
                }
            }
        }
        catch
        {
            // Ignore
        }
        return ids;
    }

    public static int WaitForNewScrcpyDisplayId(string adbExe, HashSet<int> previousDisplayIds)
    {
        if (!File.Exists(adbExe))
        {
            return -1;
        }

        // Allow scrcpy.exe 650ms of uncontended ADB & DisplayManagerService time to push scrcpy-server and create its VirtualDisplay
        Thread.Sleep(650);

        int targetDisplayId = -1;
        DateTime deadline = DateTime.UtcNow.AddSeconds(12);

        while (DateTime.UtcNow < deadline)
        {
            var currentIds = GetActiveScrcpyDisplayIds(adbExe);
            var newlyCreated = currentIds.Where(id => !previousDisplayIds.Contains(id)).OrderByDescending(id => id).ToList();
            if (newlyCreated.Count > 0)
            {
                targetDisplayId = newlyCreated[0];
                break;
            }
            Thread.Sleep(250);
        }

        if (targetDisplayId <= 0)
        {
            var fallbackIds = GetActiveScrcpyDisplayIds(adbExe).OrderByDescending(id => id).ToList();
            if (fallbackIds.Count > 0)
            {
                targetDisplayId = fallbackIds[0];
            }
        }

        return targetDisplayId;
    }

    private static List<(int DisplayId, int TaskId)> GetPackageActiveTasks(string adbExe, string packageName)
    {
        var tasks = new List<(int DisplayId, int TaskId)>();
        string actDump = RunProcessCaptureOutput(adbExe, new[] { "shell", "dumpsys", "activity", "activities" }, 4000);
        int currentDisplay = -1;
        var seenTasks = new HashSet<int>();

        foreach (string rawLine in actDump.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("ActivityTaskSupervisor", StringComparison.Ordinal) ||
                line.StartsWith("Recent tasks:", StringComparison.Ordinal))
            {
                break;
            }

            var dispMatch = Regex.Match(line, @"^Display\s+#(\d+)\b");
            if (dispMatch.Success && int.TryParse(dispMatch.Groups[1].Value, out int dId))
            {
                currentDisplay = dId;
                continue;
            }

            if (currentDisplay >= 0 && line.Contains("ActivityRecord{", StringComparison.Ordinal) &&
                !line.Contains(" f}}", StringComparison.Ordinal) &&
                line.Contains(packageName + "/", StringComparison.OrdinalIgnoreCase))
            {
                var taskMatch = Regex.Match(line, $@"\b{Regex.Escape(packageName)}/[^\s]+\s+t(\d+)\b", RegexOptions.IgnoreCase);
                if (taskMatch.Success && int.TryParse(taskMatch.Groups[1].Value, out int tId) && tId > 0 && seenTasks.Add(tId))
                {
                    tasks.Add((currentDisplay, tId));
                }
            }
        }

        return tasks;
    }

    public static int BindAppToVirtualDisplay(
        string adbExe,
        AppConfig app,
        int targetDisplayId,
        string? fallbackApkPath = null,
        Action<string>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(app.PackageName) || !File.Exists(adbExe))
        {
            return -1;
        }

        if (targetDisplayId <= 0)
        {
            logger?.Invoke($"[VirtualDisplay] No newly allocated scrcpy display detected; falling back to primary display.");
            LaunchApkWithRetry(adbExe, app, 10);
            return -1;
        }

        // 1. If the app already has an active non-finishing task (e.g., FirstRunActivity or MainActivity), move it directly to targetDisplayId
        var initialTasks = GetPackageActiveTasks(adbExe, app.PackageName);
        if (initialTasks.Count > 0)
        {
            foreach (var (dId, tId) in initialTasks)
            {
                if (dId != targetDisplayId)
                {
                    logger?.Invoke($"[VirtualDisplay] Moving existing Task #{tId} ({app.PackageName}) from Display #{dId} -> Display #{targetDisplayId}...");
                    RunProcessCaptureOutput(
                        adbExe,
                        new[] { "shell", "cmd", "activity", "display", "move-stack", tId.ToString(), targetDisplayId.ToString() },
                        4000);
                }
            }
            logger?.Invoke($"[VirtualDisplay] Confirmed {app.PackageName} active on isolated Display #{targetDisplayId} (Task #{initialTasks[0].TaskId}).");
            return targetDisplayId;
        }

        string directActivity = ApkManifestParser.ResolveDirectActivity(app.PackageName, app.ActivityName);
        var startArgs = BuildApkLaunchArgs(app, targetDisplayId);
        logger?.Invoke($"[VirtualDisplay] Launching {app.PackageName}/{directActivity} on Display #{targetDisplayId}...");
        string launchOut = RunProcessCaptureOutput(adbExe, startArgs, 8000);

        if ((launchOut.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
             launchOut.Contains("Unable to resolve", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(fallbackApkPath) && File.Exists(fallbackApkPath))
        {
            logger?.Invoke($"[VirtualDisplay] Package {app.PackageName} not installed yet; sideloading {Path.GetFileName(fallbackApkPath)}...");
            SideloadAndGrantPermissions(adbExe, fallbackApkPath, app.PackageName);
            launchOut = RunProcessCaptureOutput(adbExe, startArgs, 8000);
        }

        // Verify activity placement; if a trampoline or FirstRun redirect routed a task to Display #0, move all tasks to targetDisplayId
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Thread.Sleep(350);
            var activeTasks = GetPackageActiveTasks(adbExe, app.PackageName);
            bool anyMoved = false;
            int confirmedTaskId = -1;

            foreach (var (dId, tId) in activeTasks)
            {
                if (dId == targetDisplayId)
                {
                    confirmedTaskId = tId;
                }
                else
                {
                    logger?.Invoke($"[VirtualDisplay] Moving Task #{tId} ({app.PackageName}) from Display #{dId} -> Display #{targetDisplayId}...");
                    RunProcessCaptureOutput(
                        adbExe,
                        new[] { "shell", "cmd", "activity", "display", "move-stack", tId.ToString(), targetDisplayId.ToString() },
                        4000);
                    confirmedTaskId = tId;
                    anyMoved = true;
                }
            }

            if (confirmedTaskId > 0 && !anyMoved)
            {
                logger?.Invoke($"[VirtualDisplay] Confirmed {app.PackageName} active on isolated Display #{targetDisplayId} (Task #{confirmedTaskId}).");
                return targetDisplayId;
            }

            if (activeTasks.Count == 0 && attempt == 2)
            {
                RunProcessCaptureOutput(adbExe, startArgs, 6000);
            }
        }

        return targetDisplayId;
    }

    public static (List<string> PushArgs, List<string> IntentArgs, string RemoteUri, string MimeType) BuildRelayArgs(
        LaForgeConfig cfg,
        string hostFilePath)
    {
        string fullPath = Path.GetFullPath(hostFilePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Relay host file does not exist: {fullPath}");
        }

        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!cfg.Relays.Associations.TryGetValue(ext, out RelayRule? rule) || rule is null)
        {
            throw new InvalidOperationException($"Unsupported file extension '{ext}' for relay.");
        }

        string fileName = Path.GetFileName(fullPath);
        string remoteDir = cfg.Relays.RemoteStagingDir.TrimEnd('/');
        string remotePath = $"{remoteDir}/{fileName}";
        string remoteUri = $"file://{remotePath}";
        string directActivity = ApkManifestParser.ResolveDirectActivity(cfg.App.PackageName, cfg.App.ActivityName);
        string component = $"{cfg.App.PackageName}/{directActivity}";

        var pushArgs = new List<string> { "push", fullPath, remotePath };
        var intentArgs = new List<string>
        {
            "shell", "am", "start",
            "-a", rule.Action,
            "-d", remoteUri,
            "-t", rule.MimeType,
            "-n", component
        };

        return (pushArgs, intentArgs, remoteUri, rule.MimeType);
    }

    private static string FormatArgsForDisplay(IEnumerable<string> args)
    {
        return string.Join(' ', args.Select(a => a.Contains(' ') || a.Contains('\\') || a.Contains('/') && !a.StartsWith('-') ? $"\"{a}\"" : a));
    }

    public static Process StartBackgroundProcess(
        string fileName,
        IEnumerable<string> args,
        bool hidden,
        IDictionary<string, string>? environmentVariables = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = hidden,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        if (environmentVariables != null)
        {
            foreach (var kv in environmentVariables)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }
        return Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
    }

    public static string RunProcessCaptureOutput(string fileName, IEnumerable<string> args, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit(timeoutMs);
        string stdout = outTask.GetAwaiter().GetResult();
        string stderr = errTask.GetAwaiter().GetResult();
        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }

    public static void RunCheckedProcess(string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit(120000);
        if (proc.ExitCode != 0)
        {
            string err = errTask.GetAwaiter().GetResult();
            throw new InvalidOperationException($"{fileName} exited with code {proc.ExitCode}: {err}");
        }
    }

    public static void WaitForDeviceBoot(string adbPath, int timeoutSec)
    {
        try
        {
            string fastCheck = RunProcessCaptureOutput(
                adbPath,
                new[] { "shell", "getprop sys.boot_completed; getprop debug.laforge.display_configured" },
                3000).Trim();
            if (fastCheck.StartsWith("1\n1", StringComparison.Ordinal) ||
                fastCheck.StartsWith("1\r\n1", StringComparison.Ordinal))
            {
                return;
            }
        }
        catch
        {
            // Fall through to full boot wait
        }

        RunCheckedProcess(adbPath, new[] { "wait-for-device" });
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            string bootOut = RunProcessCaptureOutput(adbPath, new[] { "shell", "getprop", "sys.boot_completed" }, 5000).Trim();
            if (bootOut == "1")
            {
                string dumpsys = RunProcessCaptureOutput(adbPath, new[] { "shell", "dumpsys", "activity", "activities" }, 5000);
                if (!dumpsys.Contains("FallbackHome", StringComparison.OrdinalIgnoreCase) &&
                    !dumpsys.Contains("com.android.sdksetup", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            Thread.Sleep(1000);
        }
        throw new TimeoutException($"Android runtime failed to boot within {timeoutSec}s.");
    }
}
