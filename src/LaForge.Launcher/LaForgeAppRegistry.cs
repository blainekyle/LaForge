using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace LaForge.Launcher;

public sealed class AppPolicyConfig
{
    public string PackageName { get; set; } = "";
    public string AppTitle { get; set; } = "";
    public string ActivityName { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string BundleDir { get; set; } = "";
    public string IconPath { get; set; } = "";
    public string LastLaunchedUtc { get; set; } = "";
    public int LastScrcpyPid { get; set; }

    public bool VirtualDisplay { get; set; } = true;
    public bool BackgroundOnClose { get; set; }
    public bool BlockNetwork { get; set; }
    public bool MuteAudio { get; set; }
    public bool MuteMic { get; set; }
    public bool BlockHid { get; set; }
    public bool HostShareEnabled { get; set; } = true;

    // Windowing, Orientation & Screen Size Customization (Phase 5)
    public string PreferredOrientation { get; set; } = "unspecified"; // "portrait", "landscape", or "unspecified"
    public bool IsWindowable { get; set; } = true; // true if resizeableActivity=true / supports arbitrary multi-window aspect ratios
    public string PreferredScreenSize { get; set; } = "1280x720";
    public List<string> SupportedScreenSizes { get; set; } = new();
    public string ScreenSizeOverride { get; set; } = ""; // Empty = use PreferredScreenSize; otherwise "WxH"
    public bool DynamicAspectResize { get; set; } = true; // Allow live aspect-ratio resizing while open for window-able apps

    public (int Width, int Height) GetEffectiveScreenSize(WindowConfig? fallback = null)
    {
        string raw = !string.IsNullOrWhiteSpace(ScreenSizeOverride)
            ? ScreenSizeOverride
            : (!string.IsNullOrWhiteSpace(PreferredScreenSize)
                ? PreferredScreenSize
                : (PreferredOrientation.Equals("portrait", StringComparison.OrdinalIgnoreCase) ? "540x960" : "1280x720"));

        if (TryParseDimensions(raw, out int w, out int h))
        {
            return (AlignTo8(Math.Clamp(w, 240, 7680)), AlignTo8(Math.Clamp(h, 240, 4320)));
        }

        if (fallback != null && fallback.Width >= 240 && fallback.Height >= 240)
        {
            if (PreferredOrientation.Equals("portrait", StringComparison.OrdinalIgnoreCase) && fallback.Width > fallback.Height)
            {
                return (540, 960);
            }
            return (AlignTo8(fallback.Width), AlignTo8(fallback.Height));
        }

        return PreferredOrientation.Equals("portrait", StringComparison.OrdinalIgnoreCase) ? (540, 960) : (1280, 720);
    }

    public static bool TryParseDimensions(string? input, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var m = Regex.Match(input.Trim(), @"(\d{3,4})\s*[xX×]\s*(\d{3,4})");
        if (!m.Success) return false;
        return int.TryParse(m.Groups[1].Value, out width) &&
               int.TryParse(m.Groups[2].Value, out height) &&
               width >= 200 && width <= 7680 &&
               height >= 200 && height <= 4320;
    }

    public static int AlignTo8(int val) => Math.Max(200, (val + 4) & ~7);
}

public sealed class AppNotificationItem
{
    public string PackageName { get; init; } = "";
    public string AppTitle { get; init; } = "";
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public string Channel { get; init; } = "";
}

public sealed class AppLiveTelemetry
{
    public string PackageName { get; set; } = "";
    public int AndroidUid { get; set; }
    public int AndroidPid { get; set; }
    public int VirtualDisplayId { get; set; } = -1;
    public bool HasActiveAndroidTask { get; set; }
    public int ScrcpyProcessId { get; set; }
    public IntPtr ScrcpyWindowHandle { get; set; }
    public bool IsScrcpyWindowVisible { get; set; }
    public bool IsPlayingAudio { get; set; }
    public bool IsRecordingAudio { get; set; }
    public List<AppNotificationItem> Notifications { get; set; } = new();

    public bool IsRunningOnDisplay =>
        (ScrcpyProcessId > 0 && IsScrcpyWindowVisible) ||
        (ScrcpyProcessId <= 0 && VirtualDisplayId > 0 && HasActiveAndroidTask);

    public bool IsBackgrounded =>
        (ScrcpyProcessId > 0 && !IsScrcpyWindowVisible) ||
        (!IsRunningOnDisplay && (HasActiveAndroidTask || IsPlayingAudio));

    public bool IsStopped => !IsRunningOnDisplay && !IsBackgrounded;
}

/// <summary>
/// Manages %LOCALAPPDATA%\LaForge\config\*.conf per-app configuration files,
/// app registration ("remembered" apps), live ADB policy enforcement (network, audio, mic, HID),
/// and real-time multi-display / audio / notification telemetry for LaForge Console.
/// </summary>
public static class LaForgeAppRegistry
{
    public static string GetFriendlyAppName(string? rawTitle, string? packageName = null)
    {
        string name = rawTitle ?? "";
        if (name.StartsWith("LaForge - ", StringComparison.OrdinalIgnoreCase))
        {
            name = name["LaForge - ".Length..].Trim();
        }
        int parenIdx = name.IndexOf(" (v", StringComparison.OrdinalIgnoreCase);
        if (parenIdx > 0)
        {
            name = name[..parenIdx].Trim();
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            name = packageName ?? "Application";
        }
        return name;
    }
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const int VK_LBUTTON = 0x01;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    public sealed record ScrcpyWindowInfo(IntPtr Hwnd, int ProcessId, string Title, bool IsVisible);

    public static bool IsLeftMouseButtonDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    public static bool TryGetWindowBoundsByPid(int scrcpyPid, out RECT rect)
    {
        rect = default;
        if (scrcpyPid <= 0) return false;
        var win = FindAllScrcpyWindows().FirstOrDefault(w => w.ProcessId == scrcpyPid);
        if (win == null || win.Hwnd == IntPtr.Zero) return false;
        return GetWindowRect(win.Hwnd, out rect) && rect.Width >= 100 && rect.Height >= 100;
    }

    public static bool TryGetClientBoundsByPid(int scrcpyPid, out RECT clientRect)
    {
        clientRect = default;
        if (scrcpyPid <= 0) return false;
        var win = FindAllScrcpyWindows().FirstOrDefault(w => w.ProcessId == scrcpyPid);
        if (win == null || win.Hwnd == IntPtr.Zero) return false;
        return GetClientRect(win.Hwnd, out clientRect) && clientRect.Width >= 100 && clientRect.Height >= 100;
    }

    public static bool TryRestoreWindowBoundsByPid(int scrcpyPid, RECT rect)
    {
        if (scrcpyPid <= 0 || rect.Width < 100 || rect.Height < 100) return false;
        var win = FindAllScrcpyWindows().FirstOrDefault(w => w.ProcessId == scrcpyPid);
        if (win == null || win.Hwnd == IntPtr.Zero) return false;
        return SetWindowPos(win.Hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static List<ScrcpyWindowInfo> FindAllScrcpyWindows()
    {
        var list = new List<ScrcpyWindowInfo>();
        var scrcpyPids = new HashSet<int>();
        try
        {
            foreach (var p in Process.GetProcessesByName("scrcpy"))
            {
                scrcpyPids.Add(p.Id);
            }
        }
        catch
        {
            // Ignore
        }

        if (scrcpyPids.Count == 0)
        {
            return list;
        }

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (!scrcpyPids.Contains((int)pid))
            {
                return true;
            }

            var clsSb = new StringBuilder(128);
            GetClassName(hWnd, clsSb, clsSb.Capacity);
            string cls = clsSb.ToString();

            var titleSb = new StringBuilder(256);
            GetWindowText(hWnd, titleSb, titleSb.Capacity);
            string title = titleSb.ToString();

            if (cls.Equals("SDL_app", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(title))
            {
                list.Add(new ScrcpyWindowInfo(hWnd, (int)pid, title, IsWindowVisible(hWnd)));
            }
            return true;
        }, IntPtr.Zero);

        return list;
    }

    public static ScrcpyWindowInfo? FindAppScrcpyWindow(AppPolicyConfig app, IReadOnlyList<ScrcpyWindowInfo>? allWindows = null)
    {
        var windows = allWindows ?? FindAllScrcpyWindows();
        if (app.LastScrcpyPid > 0)
        {
            var byPid = windows.FirstOrDefault(w => w.ProcessId == app.LastScrcpyPid);
            if (byPid != null) return byPid;
        }
        if (!string.IsNullOrWhiteSpace(app.AppTitle))
        {
            var byTitle = windows.FirstOrDefault(w => w.Title.Equals(app.AppTitle, StringComparison.OrdinalIgnoreCase));
            if (byTitle != null) return byTitle;
        }
        return null;
    }

    /// <summary>
    /// Hides the app's scrcpy SDL3 window in 0ms without killing scrcpy or destroying its Android virtual display.
    /// </summary>
    public static bool BackgroundAppWindow(AppPolicyConfig app)
    {
        var win = FindAppScrcpyWindow(app);
        if (win != null && win.Hwnd != IntPtr.Zero)
        {
            ShowWindow(win.Hwnd, SW_HIDE);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Restores and focuses an existing visible or hidden (backgrounded) scrcpy window in 0ms.
    /// Returns true if an existing window was found and restored/focused.
    /// </summary>
    public static bool TryRestoreOrFocusAppWindow(AppPolicyConfig app)
    {
        var win = FindAppScrcpyWindow(app);
        if (win != null && win.Hwnd != IntPtr.Zero)
        {
            ShowWindow(win.Hwnd, SW_SHOW);
            ShowWindow(win.Hwnd, SW_RESTORE);
            SetForegroundWindow(win.Hwnd);
            return true;
        }
        return false;
    }

    public static string GetConfigDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(localAppData, "LaForge", "config");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetConfFilePath(string packageName)
    {
        string safeName = string.Concat(packageName.Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-'));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "unknown.app";
        }
        return Path.Combine(GetConfigDirectory(), $"{safeName}.conf");
    }

    public static AppPolicyConfig LoadOrCreate(LaForgeConfig config, string bundleDir)
    {
        string pkg = config.App.PackageName;
        if (string.IsNullOrWhiteSpace(pkg))
        {
            pkg = "com.laforge.app";
        }

        string confPath = GetConfFilePath(pkg);
        AppPolicyConfig policy = File.Exists(confPath)
            ? ParseConfFile(confPath)
            : new AppPolicyConfig { PackageName = pkg, HostShareEnabled = config.Storage.EnableHostMount };

        policy.PackageName = pkg;
        if (!string.IsNullOrWhiteSpace(config.App.Title))
        {
            policy.AppTitle = config.App.Title;
        }
        if (!string.IsNullOrWhiteSpace(config.App.ActivityName))
        {
            policy.ActivityName = ApkManifestParser.ResolveDirectActivity(pkg, config.App.ActivityName);
        }
        else if (!string.IsNullOrWhiteSpace(policy.ActivityName))
        {
            policy.ActivityName = ApkManifestParser.ResolveDirectActivity(pkg, policy.ActivityName);
        }

        string fullBundleDir = Path.GetFullPath(bundleDir);
        policy.BundleDir = fullBundleDir;

        string exeCandidate = Path.Combine(fullBundleDir, config.App.ExecutableName);
        if (!File.Exists(exeCandidate))
        {
            exeCandidate = Path.Combine(fullBundleDir, "launcher.exe");
        }
        if (File.Exists(exeCandidate))
        {
            policy.ExecutablePath = exeCandidate;
        }

        string iconCandidate = Path.IsPathRooted(config.App.IconPath)
            ? config.App.IconPath
            : Path.GetFullPath(Path.Combine(fullBundleDir, config.App.IconPath));
        if (File.Exists(iconCandidate))
        {
            policy.IconPath = iconCandidate;
        }

        EnsureManifestWindowingProfile(policy, config.App.ApkPath);
        Save(policy);
        return policy;
    }

    public static void EnsureManifestWindowingProfile(AppPolicyConfig policy, string? configuredApkPath = null)
    {
        if (policy.SupportedScreenSizes.Count > 0 && !string.IsNullOrWhiteSpace(policy.PreferredScreenSize))
        {
            return;
        }

        try
        {
            string? apkPath = null;
            if (!string.IsNullOrWhiteSpace(configuredApkPath))
            {
                apkPath = Path.IsPathRooted(configuredApkPath)
                    ? configuredApkPath
                    : (!string.IsNullOrWhiteSpace(policy.BundleDir)
                        ? Path.GetFullPath(Path.Combine(policy.BundleDir, configuredApkPath))
                        : Path.GetFullPath(configuredApkPath));
            }
            if ((string.IsNullOrWhiteSpace(apkPath) || !File.Exists(apkPath)) && !string.IsNullOrWhiteSpace(policy.BundleDir))
            {
                string candidate = Path.Combine(policy.BundleDir, "payload.apk");
                if (File.Exists(candidate)) apkPath = candidate;
            }

            if (!string.IsNullOrWhiteSpace(apkPath) && File.Exists(apkPath))
            {
                var meta = ApkManifestParser.ParseApk(apkPath);
                policy.PreferredOrientation = meta.PreferredOrientation;
                policy.IsWindowable = meta.IsWindowable;
                policy.PreferredScreenSize = meta.PreferredScreenSize;
                policy.SupportedScreenSizes = new List<string>(meta.SupportedScreenSizes);
                if (!policy.IsWindowable)
                {
                    policy.DynamicAspectResize = false;
                }
                Save(policy);
                return;
            }
        }
        catch
        {
            // Fallback to defaults if APK is not accessible
        }

        if (policy.SupportedScreenSizes.Count == 0)
        {
            var fallbackMeta = new ApkMetadata
            {
                PackageName = policy.PackageName,
                PreferredOrientation = policy.PreferredOrientation,
                IsWindowable = policy.IsWindowable
            };
            ApkManifestParser.PopulateScreenSizeProfile(fallbackMeta);
            policy.PreferredScreenSize = fallbackMeta.PreferredScreenSize;
            policy.SupportedScreenSizes = fallbackMeta.SupportedScreenSizes;
        }
    }

    public static void RecordAppLaunch(LaForgeConfig config, string bundleDir, int scrcpyPid = 0)
    {
        AppPolicyConfig policy = LoadOrCreate(config, bundleDir);
        policy.LastLaunchedUtc = DateTime.UtcNow.ToString("O");
        if (scrcpyPid > 0)
        {
            policy.LastScrcpyPid = scrcpyPid;
        }
        Save(policy);
    }

    public static void Save(AppPolicyConfig policy)
    {
        if (string.IsNullOrWhiteSpace(policy.PackageName)) return;

        string confPath = GetConfFilePath(policy.PackageName);
        var sb = new StringBuilder();
        sb.AppendLine("# LaForge Per-App Configuration & Policy (.conf)");
        sb.AppendLine($"# Generated/Updated: {DateTime.UtcNow:O}");
        sb.AppendLine($"package_name={policy.PackageName}");
        sb.AppendLine($"app_title={policy.AppTitle}");
        sb.AppendLine($"activity_name={policy.ActivityName}");
        sb.AppendLine($"executable_path={policy.ExecutablePath}");
        sb.AppendLine($"bundle_dir={policy.BundleDir}");
        sb.AppendLine($"icon_path={policy.IconPath}");
        sb.AppendLine($"last_launched_utc={policy.LastLaunchedUtc}");
        sb.AppendLine($"last_scrcpy_pid={policy.LastScrcpyPid}");
        sb.AppendLine();
        sb.AppendLine("# Multi-Display & Isolation Policies");
        sb.AppendLine($"virtual_display={policy.VirtualDisplay.ToString().ToLowerInvariant()}");
        sb.AppendLine($"background_on_close={policy.BackgroundOnClose.ToString().ToLowerInvariant()}");
        sb.AppendLine($"block_network={policy.BlockNetwork.ToString().ToLowerInvariant()}");
        sb.AppendLine($"mute_audio={policy.MuteAudio.ToString().ToLowerInvariant()}");
        sb.AppendLine($"mute_mic={policy.MuteMic.ToString().ToLowerInvariant()}");
        sb.AppendLine($"block_hid={policy.BlockHid.ToString().ToLowerInvariant()}");
        sb.AppendLine($"host_share_enabled={policy.HostShareEnabled.ToString().ToLowerInvariant()}");
        sb.AppendLine();
        sb.AppendLine("# Windowing, Orientation & Screen Size Customization");
        sb.AppendLine($"preferred_orientation={policy.PreferredOrientation}");
        sb.AppendLine($"is_windowable={policy.IsWindowable.ToString().ToLowerInvariant()}");
        sb.AppendLine($"preferred_screen_size={policy.PreferredScreenSize}");
        sb.AppendLine($"supported_screen_sizes={string.Join('|', policy.SupportedScreenSizes)}");
        sb.AppendLine($"screen_size_override={policy.ScreenSizeOverride}");
        sb.AppendLine($"dynamic_aspect_resize={policy.DynamicAspectResize.ToString().ToLowerInvariant()}");

        File.WriteAllText(confPath, sb.ToString(), Encoding.UTF8);
    }

    public static AppPolicyConfig ParseConfFile(string confPath)
    {
        var policy = new AppPolicyConfig();
        if (!File.Exists(confPath)) return policy;

        foreach (string rawLine in File.ReadAllLines(confPath))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;

            string key = line[..eq].Trim().ToLowerInvariant();
            string val = line[(eq + 1)..].Trim();

            switch (key)
            {
                case "package_name": policy.PackageName = val; break;
                case "app_title": policy.AppTitle = val; break;
                case "activity_name": policy.ActivityName = val; break;
                case "executable_path": policy.ExecutablePath = val; break;
                case "bundle_dir": policy.BundleDir = val; break;
                case "icon_path": policy.IconPath = val; break;
                case "last_launched_utc": policy.LastLaunchedUtc = val; break;
                case "last_scrcpy_pid":
                    if (int.TryParse(val, out int pid)) policy.LastScrcpyPid = pid;
                    break;
                case "virtual_display": policy.VirtualDisplay = ParseBool(val, true); break;
                case "background_on_close": policy.BackgroundOnClose = ParseBool(val, false); break;
                case "block_network": policy.BlockNetwork = ParseBool(val, false); break;
                case "mute_audio": policy.MuteAudio = ParseBool(val, false); break;
                case "mute_mic": policy.MuteMic = ParseBool(val, false); break;
                case "block_hid": policy.BlockHid = ParseBool(val, false); break;
                case "host_share_enabled": policy.HostShareEnabled = ParseBool(val, true); break;
                case "preferred_orientation":
                    if (!string.IsNullOrWhiteSpace(val)) policy.PreferredOrientation = val;
                    break;
                case "is_windowable": policy.IsWindowable = ParseBool(val, true); break;
                case "preferred_screen_size":
                    if (!string.IsNullOrWhiteSpace(val)) policy.PreferredScreenSize = val;
                    break;
                case "supported_screen_sizes":
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        policy.SupportedScreenSizes = val.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    }
                    break;
                case "screen_size_override": policy.ScreenSizeOverride = val; break;
                case "dynamic_aspect_resize": policy.DynamicAspectResize = ParseBool(val, true); break;
            }
        }

        if (!string.IsNullOrWhiteSpace(policy.PackageName) && !string.IsNullOrWhiteSpace(policy.ActivityName))
        {
            policy.ActivityName = ApkManifestParser.ResolveDirectActivity(policy.PackageName, policy.ActivityName);
        }

        EnsureManifestWindowingProfile(policy);
        return policy;
    }

    public static List<AppPolicyConfig> DiscoverRememberedApps(string? workspaceRoot = null)
    {
        // Auto-seed any bundles found in dist/ so newly forged bundles appear right away
        try
        {
            var candidateDistDirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(workspaceRoot))
            {
                candidateDistDirs.Add(Path.Combine(workspaceRoot, "dist"));
            }
            candidateDistDirs.Add(Path.Combine(AppContext.BaseDirectory, "dist"));
            candidateDistDirs.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "dist")));
            candidateDistDirs.Add(@"c:\ag\APKForge\dist");

            var jsonOpts = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip
            };

            foreach (string distDir in candidateDistDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(distDir)) continue;
                foreach (string subDir in Directory.GetDirectories(distDir))
                {
                    string cfgFile = Path.Combine(subDir, "config.json");
                    if (!File.Exists(cfgFile)) continue;
                    try
                    {
                        var cfg = System.Text.Json.JsonSerializer.Deserialize<LaForgeConfig>(File.ReadAllText(cfgFile), jsonOpts);
                        if (cfg != null && !string.IsNullOrWhiteSpace(cfg.App.PackageName))
                        {
                            LoadOrCreate(cfg, subDir);
                        }
                    }
                    catch
                    {
                        // Ignore malformed bundle config
                    }
                }
            }
        }
        catch
        {
            // Ignore discovery errors
        }

        string configDir = GetConfigDirectory();
        var list = new List<AppPolicyConfig>();
        foreach (string file in Directory.GetFiles(configDir, "*.conf"))
        {
            try
            {
                var p = ParseConfFile(file);
                if (string.IsNullOrWhiteSpace(p.PackageName) ||
                    p.PackageName.Equals("com.example.placeholder", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Filter out phantom entries created from the root CLI test harness config.json (which has no APK payload)
                if (!string.IsNullOrWhiteSpace(p.BundleDir) && Directory.Exists(p.BundleDir))
                {
                    bool hasApk = Directory.GetFiles(p.BundleDir, "*.apk").Length > 0;
                    if (!hasApk)
                    {
                        try { File.Delete(file); } catch { }
                        continue;
                    }
                }

                list.Add(p);
            }
            catch
            {
                // Ignore
            }
        }

        return list.OrderBy(a => a.AppTitle, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public const string ConsoleServiceEventName = @"Local\LaForge_ConsoleServiceActiveEvent";

    public static bool IsConsoleServiceRunning()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ConsoleServiceEventName, out var evt))
            {
                using (evt)
                {
                    return evt.WaitOne(0);
                }
            }
        }
        catch
        {
            // Ignore
        }
        return false;
    }

    /// <summary>
    /// Enforces per-app policies (Network firewall, Audio PLAY_AUDIO, Mic RECORD_AUDIO, HID/device ops)
    /// inside the running Android container via ADB.
    /// </summary>
    public static void EnforcePoliciesOnGuest(
        string adbExe,
        AppPolicyConfig policy,
        Action<string>? logger = null,
        bool forceNetworkUpdate = false)
    {
        if (string.IsNullOrWhiteSpace(policy.PackageName) || !File.Exists(adbExe))
        {
            return;
        }

        try
        {
            string pkg = policy.PackageName;
            string audioMode = policy.MuteAudio ? "ignore" : "allow";
            string micMode = policy.MuteMic ? "ignore" : "allow";
            string hidMode = policy.BlockHid ? "ignore" : "allow";
            string storageMode = policy.HostShareEnabled ? "allow" : "default";

            // 1. Batch Audio, Mic, and HID/Sensor AppOps in a single adb shell call.
            // IMPORTANT: Do NOT unconditionally set MANAGE_EXTERNAL_STORAGE on a running app, because Android's
            // ActivityManager immediately kills any running process when MANAGE_EXTERNAL_STORAGE changes ("Killing <pkg>: MANAGE_EXTERNAL_STORAGE changed").
            string batchedAppOps =
                $"cmd appops set {pkg} PLAY_AUDIO {audioMode}; " +
                $"cmd appops set {pkg} TAKE_AUDIO_FOCUS {audioMode}; " +
                $"cmd appops set {pkg} RECORD_AUDIO {micMode}; " +
                $"cmd appops set {pkg} VIBRATE {hidMode}; " +
                $"cmd appops set {pkg} CAMERA {hidMode}";

            if (forceNetworkUpdate)
            {
                string currentStorage = Program.RunProcessCaptureOutput(adbExe, new[] { "shell", $"cmd appops get {pkg} MANAGE_EXTERNAL_STORAGE 2>/dev/null" }, 3000);
                if (!currentStorage.Contains($": {storageMode}", StringComparison.OrdinalIgnoreCase))
                {
                    batchedAppOps = $"cmd appops set {pkg} MANAGE_EXTERNAL_STORAGE {storageMode} 2>/dev/null; " + batchedAppOps;
                }
            }

            Program.RunProcessCaptureOutput(adbExe, new[] { "shell", batchedAppOps }, 4000);

            // 2. Per-UID Network blocking via su 0 /system/bin/iptables + netpolicy (only when blocking or explicitly toggled)
            if (policy.BlockNetwork || forceNetworkUpdate)
            {
                int uid = LookupPackageUid(adbExe, pkg);
                if (uid > 0)
                {
                    string netCmd = policy.BlockNetwork
                        ? $"cmd netpolicy add restrict-background-blacklist {uid}; " +
                          $"su 0 /system/bin/iptables -D OUTPUT -m owner --uid-owner {uid} -j REJECT; " +
                          $"su 0 /system/bin/iptables -I OUTPUT -m owner --uid-owner {uid} -j REJECT; " +
                          $"su 0 /system/bin/ip6tables -D OUTPUT -m owner --uid-owner {uid} -j REJECT; " +
                          $"su 0 /system/bin/ip6tables -I OUTPUT -m owner --uid-owner {uid} -j REJECT"
                        : $"cmd netpolicy remove restrict-background-blacklist {uid}; " +
                          $"su 0 /system/bin/iptables -D OUTPUT -m owner --uid-owner {uid} -j REJECT; " +
                          $"su 0 /system/bin/ip6tables -D OUTPUT -m owner --uid-owner {uid} -j REJECT";
                    Program.RunProcessCaptureOutput(adbExe, new[] { "shell", netCmd }, 4000);
                }
            }

            logger?.Invoke($"[Policy] Enforced {pkg}: Net={(policy.BlockNetwork ? "BLOCKED" : "ALLOW")}, Audio={(policy.MuteAudio ? "MUTED" : "ALLOW")}, Mic={(policy.MuteMic ? "MUTED" : "ALLOW")}, HID={(policy.BlockHid ? "LOCKED" : "ALLOW")}");
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[Policy Warning] Failed to apply guest policy for {policy.PackageName}: {ex.Message}");
        }
    }

    public static int LookupPackageUid(string adbExe, string packageName)
    {
        try
        {
            string outText = Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "cmd", "package", "list", "packages", "-U", packageName }, 4000);
            var m = Regex.Match(outText, $@"package:{Regex.Escape(packageName)}\s+uid:(\d+)", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int uid))
            {
                return uid;
            }
        }
        catch
        {
            // Ignore
        }
        return 0;
    }

    /// <summary>
    /// Queries live telemetry across all remembered apps in a single batched ADB shell invocation:
    /// - Active virtual display IDs per package
    /// - Running Android PIDs per package
    /// - Active Windows scrcpy processes per app
    /// - Audio playback & microphone recording state per UID/package
    /// - Active Android notifications per package
    /// </summary>
    public static Dictionary<string, AppLiveTelemetry> QueryLiveTelemetry(string adbExe, IReadOnlyList<AppPolicyConfig> apps)
    {
        var result = new Dictionary<string, AppLiveTelemetry>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            result[app.PackageName] = new AppLiveTelemetry { PackageName = app.PackageName };
        }

        // 1. Inspect local Windows scrcpy windows (both visible and hidden/backgrounded via SW_HIDE)
        try
        {
            var allWindows = FindAllScrcpyWindows();
            foreach (var app in apps)
            {
                var tel = result[app.PackageName];
                var win = FindAppScrcpyWindow(app, allWindows);
                if (win != null)
                {
                    tel.ScrcpyProcessId = win.ProcessId;
                    tel.ScrcpyWindowHandle = win.Hwnd;
                    tel.IsScrcpyWindowVisible = win.IsVisible;
                }
            }
        }
        catch
        {
            // Ignore process enumeration errors
        }

        if (string.IsNullOrWhiteSpace(adbExe) || !File.Exists(adbExe))
        {
            return result;
        }

        // Yield if an app is actively allocating a scrcpy VirtualDisplay so we never contend on adbd or DisplayManagerService
        using (var vdMutex = new Mutex(false, @"Local\LaForge_VirtualDisplayAllocMutex"))
        {
            bool lockTaken = false;
            try
            {
                try
                {
                    lockTaken = vdMutex.WaitOne(4500);
                }
                catch (AbandonedMutexException)
                {
                    lockTaken = true;
                }
            }
            finally
            {
                if (lockTaken)
                {
                    vdMutex.ReleaseMutex();
                }
            }
        }

        if (string.IsNullOrEmpty(Program.GetConnectedAdbDevice(adbExe)))
        {
            return result;
        }

        try
        {
            // 2. Single batched ADB shell call for UIDs, PIDs, Displays/Activities, Audio, and Notifications
            var pidCmds = apps.Select(a => $"echo \"PID:{a.PackageName}:$(pidof {a.PackageName})\"");
            string batchedShell =
                "echo '===LAFORGE_UIDS==='; cmd package list packages -U; " +
                "echo '===LAFORGE_PIDS==='; " + string.Join("; ", pidCmds) + "; " +
                "echo '===LAFORGE_ACTIVITIES==='; dumpsys activity activities; " +
                "echo '===LAFORGE_AUDIO==='; dumpsys audio; " +
                "echo '===LAFORGE_NOTIFS==='; dumpsys notification --noredact";

            string combinedOut = Program.RunProcessCaptureOutput(adbExe, new[] { "shell", batchedShell }, 7000);

            string uidOut = ExtractSection(combinedOut, "===LAFORGE_UIDS===", "===LAFORGE_PIDS===");
            string pidOut = ExtractSection(combinedOut, "===LAFORGE_PIDS===", "===LAFORGE_ACTIVITIES===");
            string actOut = ExtractSection(combinedOut, "===LAFORGE_ACTIVITIES===", "===LAFORGE_AUDIO===");
            string audioOut = ExtractSection(combinedOut, "===LAFORGE_AUDIO===", "===LAFORGE_NOTIFS===");
            string notifOut = ExtractSection(combinedOut, "===LAFORGE_NOTIFS===", null);

            var uidToPkg = new Dictionary<int, string>();
            foreach (var app in apps)
            {
                var m = Regex.Match(uidOut, $@"package:{Regex.Escape(app.PackageName)}\s+uid:(\d+)", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int uid))
                {
                    result[app.PackageName].AndroidUid = uid;
                    uidToPkg[uid] = app.PackageName;
                }
            }

            foreach (string rawLine in pidOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("PID:", StringComparison.Ordinal)) continue;
                string[] parts = line.Split(':');
                if (parts.Length >= 3 && result.TryGetValue(parts[1], out var tel))
                {
                    string firstPid = parts[2].Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    if (int.TryParse(firstPid, out int pid))
                    {
                        tel.AndroidPid = pid;
                    }
                }
            }

            // 3. Map active Virtual Display IDs from dumpsys activity activities (excluding dead 'Recent tasks:' entries)
            int currentDisplayId = -1;
            foreach (string rawLine in actOut.Split('\n'))
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
                    currentDisplayId = dId;
                    continue;
                }

                if (currentDisplayId >= 0 &&
                    !line.Contains(" f}}", StringComparison.Ordinal) &&
                    (line.Contains("topResumedActivity=", StringComparison.Ordinal) ||
                     line.Contains("ActivityRecord{", StringComparison.Ordinal)))
                {
                    foreach (var app in apps)
                    {
                        if (line.Contains(app.PackageName + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            var tel = result[app.PackageName];
                            tel.HasActiveAndroidTask = true;
                            if (currentDisplayId > 0 || tel.VirtualDisplayId < 0)
                            {
                                tel.VirtualDisplayId = currentDisplayId;
                            }
                        }
                    }
                }
            }

            // 4. Audio playback & recording telemetry from dumpsys audio
            foreach (string rawLine in audioOut.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Contains("AudioPlaybackConfiguration", StringComparison.Ordinal) &&
                    line.Contains("state:started", StringComparison.OrdinalIgnoreCase))
                {
                    var uidMatch = Regex.Match(line, @"u/pid:(\d+)/\d+");
                    if (uidMatch.Success && int.TryParse(uidMatch.Groups[1].Value, out int uid) &&
                        uidToPkg.TryGetValue(uid, out string? pkg))
                    {
                        result[pkg].IsPlayingAudio = true;
                    }
                }
            }

            // Check active recording in RecordActivityMonitor event log (last event for riid is 'rec start' or 'rec update')
            var activeRecByPkg = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in audioOut.Split('\n'))
            {
                string line = rawLine.Trim();
                var recMatch = Regex.Match(line, @"\brec\s+(start|update|stop)\s+riid:\d+\s+uid:(\d+).*?\bpack:([^\s]+)");
                if (recMatch.Success)
                {
                    string action = recMatch.Groups[1].Value;
                    string pack = recMatch.Groups[3].Value.Trim();
                    activeRecByPkg[pack] = !action.Equals("stop", StringComparison.OrdinalIgnoreCase);
                }
            }
            foreach (var kv in activeRecByPkg)
            {
                if (kv.Value && result.TryGetValue(kv.Key, out var tel))
                {
                    tel.IsRecordingAudio = true;
                }
            }

            // 5. Active Notifications from dumpsys notification --noredact
            ParseNotifications(notifOut, apps, result);
        }
        catch
        {
            // Ignore transient ADB errors
        }

        return result;
    }

    private static string ExtractSection(string text, string startMarker, string? endMarker)
    {
        int startIdx = text.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIdx < 0) return "";
        startIdx += startMarker.Length;
        if (endMarker == null)
        {
            return text[startIdx..];
        }
        int endIdx = text.IndexOf(endMarker, startIdx, StringComparison.Ordinal);
        return endIdx < 0 ? text[startIdx..] : text[startIdx..endIdx];
    }

    private static void ParseNotifications(
        string notifDump,
        IReadOnlyList<AppPolicyConfig> apps,
        Dictionary<string, AppLiveTelemetry> result)
    {
        string? currentPkg = null;
        string currentChannel = "";
        string currentTitle = "";
        string currentText = "";

        void FlushCurrent()
        {
            if (!string.IsNullOrWhiteSpace(currentPkg) &&
                result.TryGetValue(currentPkg, out var tel) &&
                (!string.IsNullOrWhiteSpace(currentTitle) || !string.IsNullOrWhiteSpace(currentText)))
            {
                string appTitle = apps.FirstOrDefault(a => a.PackageName.Equals(currentPkg, StringComparison.OrdinalIgnoreCase))?.AppTitle ?? currentPkg;
                // Avoid duplicate notification entries from summary/group lines
                if (!tel.Notifications.Any(n => n.Title == currentTitle && n.Text == currentText))
                {
                    tel.Notifications.Add(new AppNotificationItem
                    {
                        PackageName = currentPkg,
                        AppTitle = appTitle,
                        Title = string.IsNullOrWhiteSpace(currentTitle) ? appTitle : currentTitle,
                        Text = currentText,
                        Channel = currentChannel
                    });
                }
            }
            currentPkg = null;
            currentChannel = "";
            currentTitle = "";
            currentText = "";
        }

        foreach (string rawLine in notifDump.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("NotificationRecord(", StringComparison.Ordinal))
            {
                FlushCurrent();
                var pkgMatch = Regex.Match(line, @"\bpkg=([^\s]+)");
                if (pkgMatch.Success)
                {
                    currentPkg = pkgMatch.Groups[1].Value.Trim();
                }
                var chanMatch = Regex.Match(line, @"channel=([^\s\)]+)");
                if (chanMatch.Success)
                {
                    currentChannel = chanMatch.Groups[1].Value.Trim();
                }
                continue;
            }

            if (currentPkg != null)
            {
                var titleMatch = Regex.Match(line, @"android\.title=\w+\s*\((.*)\)$");
                if (titleMatch.Success)
                {
                    currentTitle = titleMatch.Groups[1].Value.Trim();
                }
                var textMatch = Regex.Match(line, @"android\.text=\w+\s*\((.*)\)$");
                if (textMatch.Success)
                {
                    currentText = textMatch.Groups[1].Value.Trim();
                }
                if (line.StartsWith("mAdjustments=", StringComparison.Ordinal) ||
                    line.StartsWith("RankingHelper", StringComparison.Ordinal) ||
                    line.StartsWith("NotificationListeners", StringComparison.Ordinal))
                {
                    FlushCurrent();
                }
            }
        }
        FlushCurrent();
    }

    private static bool ParseBool(string val, bool defaultValue)
    {
        if (bool.TryParse(val, out bool b)) return b;
        if (val == "1" || val.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (val == "0" || val.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
        return defaultValue;
    }
}
