using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        string bridgeLocal = Path.GetFullPath(Path.Combine(configDir, "bridge", fallbackBridgeFileName));
        if (File.Exists(bridgeLocal))
        {
            return bridgeLocal;
        }

        string bridgeRoot = Path.GetFullPath(Path.Combine(configDir, "..", "..", "bridge", fallbackBridgeFileName));
        if (File.Exists(bridgeRoot))
        {
            return bridgeRoot;
        }

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
            Path.GetFullPath(Path.Combine(configDir, "..", "..", "runtime", "emulator", "emulator.exe")),
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
            RunProcessCaptureOutput(adbExe, new[] { "shell", "wm", "size", $"{win.Width}x{win.Height}" }, 5000);
            RunProcessCaptureOutput(adbExe, new[] { "shell", "wm", "density", "160" }, 5000);
            RunProcessCaptureOutput(adbExe, new[] { "shell", "wm", "dismiss-keyguard" }, 5000);
            RunProcessCaptureOutput(adbExe, new[] { "shell", "cmd", "statusbar", "collapse" }, 5000);
            RunProcessCaptureOutput(
                adbExe,
                new[]
                {
                    "shell", "cmd", "statusbar", "send-disable-flag",
                    "statusbar-expansion", "home", "recents", "notification-peek",
                    "system-icons", "clock", "notification-icons", "search"
                },
                5000);
            RunProcessCaptureOutput(
                adbExe,
                new[]
                {
                    "shell", "settings", "put", "secure", "icon_blacklist",
                    "wifi,mobile,battery,airplane,bluetooth,alarm_clock,volume,zen,managed_profile,cast,hotspot,location,rotate,headset"
                },
                5000);
            RunProcessCaptureOutput(adbExe, new[] { "emu", "avd", "hostmicon" }, 5000);
        }
        catch
        {
            // Non-fatal if device doesn't allow wm/statusbar overrides
        }
    }

    public static void SideloadAndGrantPermissions(string adbExe, string apkPath, string packageName)
    {
        RunCheckedProcess(adbExe, new[] { "install", "-r", "-g", apkPath });
        try
        {
            RunProcessCaptureOutput(adbExe, new[] { "shell", "appops", "set", packageName, "MANAGE_EXTERNAL_STORAGE", "allow" }, 5000);
        }
        catch
        {
            // Ignore if app does not declare MANAGE_EXTERNAL_STORAGE
        }
    }

    public static string LaunchApkWithRetry(string adbExe, AppConfig app, int maxWaitSec)
    {
        var apkArgs = BuildApkLaunchArgs(app);
        DateTime deadline = DateTime.UtcNow.AddSeconds(maxWaitSec);
        string lastOut = "";

        while (DateTime.UtcNow < deadline)
        {
            RunProcessCaptureOutput(adbExe, new[] { "shell", "wm", "dismiss-keyguard" }, 3000);
            RunProcessCaptureOutput(adbExe, new[] { "shell", "cmd", "statusbar", "collapse" }, 3000);
            RunProcessCaptureOutput(
                adbExe,
                new[]
                {
                    "shell", "cmd", "statusbar", "send-disable-flag",
                    "statusbar-expansion", "home", "recents", "notification-peek",
                    "system-icons", "clock", "notification-icons", "search"
                },
                3000);

            lastOut = RunProcessCaptureOutput(adbExe, apkArgs, 10000);
            if (lastOut.Contains("Status: ok", StringComparison.OrdinalIgnoreCase) &&
                !lastOut.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            {
                return "Status: ok";
            }
            Thread.Sleep(1500);
        }

        return string.IsNullOrWhiteSpace(lastOut) ? "Completed" : lastOut.Trim();
    }

    public static Process EnsureSingleScrcpyInstance(string scrcpyExe, IEnumerable<string> scrcpyArgs, string? iconPath = null)
    {
        try
        {
            foreach (var existing in Process.GetProcessesByName("scrcpy"))
            {
                try
                {
                    existing.Kill();
                    existing.WaitForExit(2000);
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
        list.AddRange(new[] { "-no-boot-anim", "-no-audio" });
        if (rt.Whpx)
        {
            list.AddRange(new[] { "-feature", "WindowsHypervisorPlatform" });
        }
        return list;
    }

    public static List<string> BuildScrcpyArgs(LaForgeConfig cfg, bool forExecution = false)
    {
        var disp = cfg.Display;
        var win = disp.Window;
        string titleArg = forExecution
            ? $"--window-title={cfg.App.Title}"
            : $"--window-title=\"{cfg.App.Title}\"";

        var list = new List<string>
        {
            titleArg,
            $"--window-x={win.X}",
            $"--window-y={win.Y}",
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
        return list;
    }

    public static List<string> BuildApkLaunchArgs(AppConfig app)
    {
        string component = $"{app.PackageName}/{app.ActivityName}";
        return new List<string> { "shell", "am", "start", "-W", "-n", component };
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
        string component = $"{cfg.App.PackageName}/{cfg.App.ActivityName}";

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
