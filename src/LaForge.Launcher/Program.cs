using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaForge.Launcher;

public sealed class LaForgeConfig
{
    [JsonPropertyName("app")]
    public AppConfig App { get; set; } = new();

    [JsonPropertyName("runtime")]
    public RuntimeConfig Runtime { get; set; } = new();

    [JsonPropertyName("display")]
    public DisplayConfig Display { get; set; } = new();

    [JsonPropertyName("relays")]
    public RelaysConfig Relays { get; set; } = new();
}

public sealed class AppConfig
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "LaForge Standalone App";

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
            bool relayOnly = false;
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
                }
                else if (arg.Equals("--relay-only", StringComparison.OrdinalIgnoreCase))
                {
                    relayOnly = true;
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
                }
                else if (arg.Equals("--forge", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    forgeApk = args[++i];
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
                string bundleDir = ApkPackager.ForgeBundle(forgeApk, forgeOutDir);
                Console.WriteLine($"[LaForge APK Packager] Forged standalone bundle:");
                Console.WriteLine($"  Source APK        : {meta.ApkPath}");
                Console.WriteLine($"  Package Name      : {meta.PackageName} (v{meta.VersionName})");
                Console.WriteLine($"  Launcher Activity : {meta.LauncherActivity}");
                Console.WriteLine($"  Output Bundle Dir : {bundleDir}");
                return 0;
            }

            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"[LaForge Error] Configuration file not found: {configPath}");
                return 1;
            }

            string configDir = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? baseDir;
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
            var config = JsonSerializer.Deserialize<LaForgeConfig>(File.ReadAllText(configPath), jsonOptions)
                         ?? throw new InvalidOperationException("Failed to deserialize config.json.");

            ValidateWindowBoundaries(config.Display.Window);

            if (!relayOnly)
            {
                // Step 1: Launch headless Android runtime via subprocess (-snapshot for fast boot)
                var emuArgs = BuildEmulatorArgs(config.Runtime);
                Console.WriteLine($"[LaForge] Step 1/4: Booting Headless Android Runtime...");
                Console.WriteLine($"  Command: {config.Runtime.EmulatorPath} {string.Join(' ', emuArgs)}");
                if (!dryRun)
                {
                    StartBackgroundProcess(config.Runtime.EmulatorPath, emuArgs, hidden: true);
                    WaitForDeviceBoot(config.Runtime.AdbPath, config.Runtime.BootTimeoutSec);
                }

                // Step 2: Initialize display bridge (scrcpy + SDL2/Direct3D)
                var scrcpyArgs = BuildScrcpyArgs(config);
                var win = config.Display.Window;
                Console.WriteLine($"[LaForge] Step 2/4: Initializing Display Bridge (scrcpy + SDL2/Direct3D)...");
                Console.WriteLine($"  Window Boundaries: X={win.X}, Y={win.Y}, W={win.Width}, H={win.Height}, Borderless={win.Borderless}");
                Console.WriteLine($"  Command: {config.Display.ScrcpyPath} {string.Join(' ', scrcpyArgs)}");
                if (!dryRun)
                {
                    StartBackgroundProcess(config.Display.ScrcpyPath, scrcpyArgs, hidden: false);
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
                        RunCheckedProcess(config.Runtime.AdbPath, new[] { "install", "-r", resolvedApkPath });
                    }
                }

                var apkArgs = BuildApkLaunchArgs(config.App);
                Console.WriteLine($"[LaForge] Step 3/4: Launching Target APK...");
                Console.WriteLine($"  Command: {config.Runtime.AdbPath} {string.Join(' ', apkArgs)}");
                if (!dryRun)
                {
                    RunCheckedProcess(config.Runtime.AdbPath, apkArgs);
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
                    RunCheckedProcess(config.Runtime.AdbPath, pushArgs);
                    RunCheckedProcess(config.Runtime.AdbPath, intentArgs);
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

    public static List<string> BuildScrcpyArgs(LaForgeConfig cfg)
    {
        var disp = cfg.Display;
        var win = disp.Window;
        var list = new List<string>
        {
            $"--window-title=\"{cfg.App.Title}\"",
            $"--window-x={win.X}",
            $"--window-y={win.Y}",
            $"--window-width={win.Width}",
            $"--window-height={win.Height}",
            $"--render-driver={disp.RenderDriver}",
            $"--max-fps={disp.MaxFps}"
        };

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

    private static Process StartBackgroundProcess(string fileName, IEnumerable<string> args, bool hidden)
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
        return Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
    }

    private static void RunCheckedProcess(string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            string err = proc.StandardError.ReadToEnd();
            throw new InvalidOperationException($"{fileName} exited with code {proc.ExitCode}: {err}");
        }
    }

    private static void WaitForDeviceBoot(string adbPath, int timeoutSec)
    {
        RunCheckedProcess(adbPath, new[] { "wait-for-device" });
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            var psi = new ProcessStartInfo
            {
                FileName = adbPath,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            psi.ArgumentList.Add("shell");
            psi.ArgumentList.Add("getprop");
            psi.ArgumentList.Add("sys.boot_completed");

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (output == "1")
                {
                    return;
                }
            }
            Thread.Sleep(1000);
        }
        throw new TimeoutException($"Android runtime failed to boot within {timeoutSec}s.");
    }
}
