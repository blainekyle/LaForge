using System.Diagnostics;
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
    public int ScrcpyProcessId { get; set; }
    public bool IsPlayingAudio { get; set; }
    public bool IsRecordingAudio { get; set; }
    public List<AppNotificationItem> Notifications { get; set; } = new();

    public bool IsRunningOnDisplay => VirtualDisplayId > 0 || ScrcpyProcessId > 0;
    public bool IsBackgrounded => !IsRunningOnDisplay && AndroidPid > 0;
    public bool IsStopped => !IsRunningOnDisplay && AndroidPid <= 0;
}

/// <summary>
/// Manages %LOCALAPPDATA%\LaForge\config\*.conf per-app configuration files,
/// app registration ("remembered" apps), live ADB policy enforcement (network, audio, mic, HID),
/// and real-time multi-display / audio / notification telemetry for LaForge Console.
/// </summary>
public static class LaForgeAppRegistry
{
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
            policy.ActivityName = config.App.ActivityName;
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

        Save(policy);
        return policy;
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
            }
        }

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
                if (!string.IsNullOrWhiteSpace(p.PackageName) &&
                    !p.PackageName.Equals("com.example.placeholder", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(p);
                }
            }
            catch
            {
                // Ignore
            }
        }

        return list.OrderBy(a => a.AppTitle, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Enforces per-app policies (Network firewall, Audio PLAY_AUDIO, Mic RECORD_AUDIO, HID/device ops)
    /// inside the running Android container via ADB.
    /// </summary>
    public static void EnforcePoliciesOnGuest(string adbExe, AppPolicyConfig policy, Action<string>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(policy.PackageName) || !File.Exists(adbExe))
        {
            return;
        }

        try
        {
            string pkg = policy.PackageName;

            // 1. Audio output muting (PLAY_AUDIO + TAKE_AUDIO_FOCUS)
            string audioMode = policy.MuteAudio ? "ignore" : "allow";
            Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "cmd", "appops", "set", pkg, "PLAY_AUDIO", audioMode }, 4000);
            Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "cmd", "appops", "set", pkg, "TAKE_AUDIO_FOCUS", audioMode }, 4000);

            // 2. Microphone input muting (RECORD_AUDIO)
            string micMode = policy.MuteMic ? "ignore" : "allow";
            Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "cmd", "appops", "set", pkg, "RECORD_AUDIO", micMode }, 4000);

            // 3. HID / Device sensor restrictions (VIBRATE, CAMERA)
            string hidMode = policy.BlockHid ? "ignore" : "allow";
            Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "cmd", "appops", "set", pkg, "VIBRATE", hidMode }, 4000);
            Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "cmd", "appops", "set", pkg, "CAMERA", hidMode }, 4000);

            // 4. Per-UID Network blocking via su 0 iptables + netpolicy
            int uid = LookupPackageUid(adbExe, pkg);
            if (uid > 0)
            {
                if (policy.BlockNetwork)
                {
                    Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "cmd", "netpolicy", "add", "restrict-background-blacklist", uid.ToString() }, 4000);
                    Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "su", "0", "sh", "-c",
                        $"iptables -C OUTPUT -m owner --uid-owner {uid} -j REJECT 2>/dev/null || iptables -I OUTPUT -m owner --uid-owner {uid} -j REJECT" }, 4000);
                    Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "su", "0", "sh", "-c",
                        $"ip6tables -C OUTPUT -m owner --uid-owner {uid} -j REJECT 2>/dev/null || ip6tables -I OUTPUT -m owner --uid-owner {uid} -j REJECT" }, 4000);
                }
                else
                {
                    Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "cmd", "netpolicy", "remove", "restrict-background-blacklist", uid.ToString() }, 4000);
                    Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "su", "0", "sh", "-c",
                        $"while iptables -D OUTPUT -m owner --uid-owner {uid} -j REJECT 2>/dev/null; do :; done" }, 4000);
                    Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "su", "0", "sh", "-c",
                        $"while ip6tables -D OUTPUT -m owner --uid-owner {uid} -j REJECT 2>/dev/null; do :; done" }, 4000);
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
    /// Queries live telemetry across all remembered apps in a fast batch:
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

        // 1. Inspect local Windows scrcpy processes
        try
        {
            var scrcpyProcs = Process.GetProcessesByName("scrcpy");
            foreach (var app in apps)
            {
                var tel = result[app.PackageName];
                Process? matched = null;
                if (app.LastScrcpyPid > 0)
                {
                    matched = scrcpyProcs.FirstOrDefault(p => p.Id == app.LastScrcpyPid);
                }
                if (matched == null && !string.IsNullOrWhiteSpace(app.AppTitle))
                {
                    matched = scrcpyProcs.FirstOrDefault(p =>
                    {
                        try
                        {
                            return p.MainWindowTitle.Equals(app.AppTitle, StringComparison.OrdinalIgnoreCase);
                        }
                        catch
                        {
                            return false;
                        }
                    });
                }
                if (matched != null)
                {
                    tel.ScrcpyProcessId = matched.Id;
                }
            }
        }
        catch
        {
            // Ignore process enumeration errors
        }

        if (string.IsNullOrWhiteSpace(adbExe) || !File.Exists(adbExe) || string.IsNullOrEmpty(Program.GetConnectedAdbDevice(adbExe)))
        {
            return result;
        }

        try
        {
            // 2. Map UIDs & PIDs
            string uidOut = Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "cmd", "package", "list", "packages", "-U" }, 5000);
            var uidToPkg = new Dictionary<int, string>();
            foreach (var app in apps)
            {
                var m = Regex.Match(uidOut, $@"package:{Regex.Escape(app.PackageName)}\s+uid:(\d+)", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int uid))
                {
                    result[app.PackageName].AndroidUid = uid;
                    uidToPkg[uid] = app.PackageName;
                }

                string pidOut = Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "pidof", app.PackageName }, 3000).Trim();
                if (!string.IsNullOrEmpty(pidOut))
                {
                    string firstToken = pidOut.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    if (int.TryParse(firstToken, out int pid))
                    {
                        result[app.PackageName].AndroidPid = pid;
                    }
                }
            }

            // 3. Map Virtual Display IDs from dumpsys activity activities
            string actOut = Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "dumpsys", "activity", "activities" }, 5000);
            int currentDisplayId = -1;
            foreach (string rawLine in actOut.Split('\n'))
            {
                string line = rawLine.Trim();
                var dispMatch = Regex.Match(line, @"^Display\s+#(\d+)\b");
                if (dispMatch.Success && int.TryParse(dispMatch.Groups[1].Value, out int dId))
                {
                    currentDisplayId = dId;
                    continue;
                }

                if (currentDisplayId >= 0 && line.Contains("topResumedActivity=", StringComparison.Ordinal))
                {
                    foreach (var app in apps)
                    {
                        if (line.Contains(app.PackageName + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            var tel = result[app.PackageName];
                            if (currentDisplayId > 0 || tel.VirtualDisplayId < 0)
                            {
                                tel.VirtualDisplayId = currentDisplayId;
                            }
                        }
                    }
                }
            }

            // 4. Audio playback & recording telemetry from dumpsys audio
            string audioOut = Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "dumpsys", "audio" }, 5000);
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
            string notifOut = Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "dumpsys", "notification", "--noredact" }, 6000);
            ParseNotifications(notifOut, apps, result);
        }
        catch
        {
            // Ignore transient ADB errors
        }

        return result;
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
