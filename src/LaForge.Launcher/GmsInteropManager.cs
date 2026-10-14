using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LaForge.Launcher;

public sealed class GmsInteropConfig
{
    public bool InjectMicroGOnBoot { get; set; } = true;
    public bool InjectVendingStubOnBoot { get; set; } = true;
    public string MicroGApkRelativePath { get; set; } = @"runtime\gms\microg-gms-core.apk";
    public string VendingStubApkRelativePath { get; set; } = @"runtime\gms\vending-licensing-stub.apk";
    public string PreferredMicroGPackage { get; set; } = "com.google.android.gms";
    public string MicroGSettingsActivity { get; set; } = "org.microg.gms.ui.SettingsActivity";
    public string VendingPackage { get; set; } = "com.android.vending";
}

public sealed class GmsInteropLiveStatus
{
    public bool DeviceOnline { get; set; }
    public bool MicroGApkPresentOnHost { get; set; }
    public bool VendingApkPresentOnHost { get; set; }
    public bool MicroGInstalled { get; set; }
    public bool MicroGEnabled { get; set; }
    public string DetectedMicroGPackage { get; set; } = "com.google.android.gms";
    public string MicroGVersion { get; set; } = "";
    public bool VendingStubInstalled { get; set; }
    public bool VendingStubEnabled { get; set; }
    public string VendingStubVersion { get; set; } = "";
}

/// <summary>
/// Manages the GMS &amp; Licensing Interop subsystem:
/// - Pre-flight ADB push/install pipeline for microG Services Core and the headless com.android.vending LVL stub (0x0001 NOT_LICENSED)
/// - UI exclusion filtering so system/interop packages never pollute the user-facing LaForge apps list
/// - Administrative controls for launching microG Settings, clearing microG data, and toggling boot-time injection
/// </summary>
public static class GmsInteropManager
{
    private static readonly HashSet<string> ExcludedInteropPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "com.google.android.gms",
        "com.mgoogle.android.gms",
        "org.microg.gms",
        "org.microg.gms.droidguard",
        "app.revanced.android.gms",
        "com.google.android.gsf",
        "com.mgoogle.android.gsf",
        "com.android.vending"
    };

    public static readonly string[] CandidateMicroGPackages =
    {
        "com.google.android.gms",
        "com.mgoogle.android.gms",
        "org.microg.gms"
    };

    /// <summary>
    /// Returns true if the specified package is an injected GMS/microG or Vending licensing interop package
    /// that must be excluded from the user-facing LaForge Remembered Applications list.
    /// </summary>
    public static bool IsExcludedInteropPackage(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return false;
        }
        return ExcludedInteropPackages.Contains(packageName.Trim());
    }

    public static string GetInteropConfigFilePath()
    {
        return Path.Combine(LaForgeAppRegistry.GetConfigDirectory(), "gms_interop.conf");
    }

    public static GmsInteropConfig LoadConfig()
    {
        string path = GetInteropConfigFilePath();
        var cfg = new GmsInteropConfig();
        if (!File.Exists(path))
        {
            SaveConfig(cfg);
            return cfg;
        }

        try
        {
            foreach (string rawLine in File.ReadAllLines(path))
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
                    case "inject_microg_on_boot":
                        if (bool.TryParse(val, out bool bMicroG)) cfg.InjectMicroGOnBoot = bMicroG;
                        break;
                    case "inject_vending_stub_on_boot":
                        if (bool.TryParse(val, out bool bVending)) cfg.InjectVendingStubOnBoot = bVending;
                        break;
                    case "microg_apk_path":
                        if (!string.IsNullOrWhiteSpace(val)) cfg.MicroGApkRelativePath = val;
                        break;
                    case "vending_stub_apk_path":
                        if (!string.IsNullOrWhiteSpace(val)) cfg.VendingStubApkRelativePath = val;
                        break;
                    case "preferred_microg_package":
                        if (!string.IsNullOrWhiteSpace(val)) cfg.PreferredMicroGPackage = val;
                        break;
                    case "microg_settings_activity":
                        if (!string.IsNullOrWhiteSpace(val)) cfg.MicroGSettingsActivity = val;
                        break;
                }
            }
        }
        catch
        {
            // Return default config on read error
        }

        return cfg;
    }

    public static void SaveConfig(GmsInteropConfig cfg)
    {
        try
        {
            string path = GetInteropConfigFilePath();
            var sb = new StringBuilder();
            sb.AppendLine("# LaForge GMS & Licensing Interop Configuration (gms_interop.conf)");
            sb.AppendLine($"# Updated: {DateTime.UtcNow:O}");
            sb.AppendLine($"inject_microg_on_boot={cfg.InjectMicroGOnBoot.ToString().ToLowerInvariant()}");
            sb.AppendLine($"inject_vending_stub_on_boot={cfg.InjectVendingStubOnBoot.ToString().ToLowerInvariant()}");
            sb.AppendLine($"microg_apk_path={cfg.MicroGApkRelativePath}");
            sb.AppendLine($"vending_stub_apk_path={cfg.VendingStubApkRelativePath}");
            sb.AppendLine($"preferred_microg_package={cfg.PreferredMicroGPackage}");
            sb.AppendLine($"microg_settings_activity={cfg.MicroGSettingsActivity}");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Ignore save failures
        }
    }

    public static string ResolveGmsAssetPath(string? baseDir, string relativeOrFileName)
    {
        if (Path.IsPathRooted(relativeOrFileName) && File.Exists(relativeOrFileName))
        {
            return relativeOrFileName;
        }

        string fileName = Path.GetFileName(relativeOrFileName);
        string[] roots =
        {
            baseDir ?? AppContext.BaseDirectory,
            AppContext.BaseDirectory,
            @"c:\ag\APKForge"
        };

        foreach (string root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string current = Path.GetFullPath(root);
            for (int depth = 0; depth <= 3; depth++)
            {
                string c1 = Path.Combine(current, relativeOrFileName);
                if (File.Exists(c1)) return Path.GetFullPath(c1);

                string c2 = Path.Combine(current, "runtime", "gms", fileName);
                if (File.Exists(c2)) return Path.GetFullPath(c2);

                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
        }

        return Path.Combine(@"c:\ag\APKForge\runtime\gms", fileName);
    }

    /// <summary>
    /// Pre-flight routine to silently push/install (or disable when toggled off) microG Services Core
    /// and the headless com.android.vending LVL stub (0x0001 NOT_LICENSED) during initial AVD setup.
    /// </summary>
    public static void ExecutePreFlightInjection(
        string adbExe,
        string? baseDir = null,
        Action<string>? logger = null,
        bool forceReconcile = false)
    {
        if (string.IsNullOrWhiteSpace(adbExe) || !File.Exists(adbExe))
        {
            return;
        }

        try
        {
            if (!forceReconcile)
            {
                string doneProp = Program.RunProcessCaptureOutput(
                    adbExe,
                    new[] { "shell", "getprop", "debug.laforge.gms_preflight_done" },
                    2500).Trim();
                if (doneProp == "1")
                {
                    return;
                }
            }

            var cfg = LoadConfig();
            string microGApk = ResolveGmsAssetPath(baseDir, cfg.MicroGApkRelativePath);
            string vendingApk = ResolveGmsAssetPath(baseDir, cfg.VendingStubApkRelativePath);

            // 1. Reconcile microG Services Core
            if (cfg.InjectMicroGOnBoot)
            {
                string installedPath = Program.RunProcessCaptureOutput(
                    adbExe,
                    new[] { "shell", "pm path com.google.android.gms 2>/dev/null; pm path com.mgoogle.android.gms 2>/dev/null" },
                    4000).Trim();

                if (!installedPath.Contains("package:", StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(microGApk))
                    {
                        logger?.Invoke($"[GMS Interop] Pre-flight injecting microG Services Core ({Path.GetFileName(microGApk)})...");
                        Program.RunProcessCaptureOutput(
                            adbExe,
                            new[] { "install", "-r", "-g", "--force-queryable", microGApk },
                            90000);
                    }
                }
                else if (forceReconcile && File.Exists(microGApk))
                {
                    logger?.Invoke($"[GMS Interop] Re-enabling microG Services Core in guest runtime...");
                }

                Program.RunProcessCaptureOutput(
                    adbExe,
                    new[]
                    {
                        "shell",
                        "pm enable com.google.android.gms 2>/dev/null; " +
                        "pm enable com.mgoogle.android.gms 2>/dev/null; " +
                        "cmd appops set com.google.android.gms SYSTEM_ALERT_WINDOW allow 2>/dev/null"
                    },
                    4000);
            }
            else
            {
                logger?.Invoke("[GMS Interop] Boot toggle OFF for microG Services Core -> disabling in guest...");
                Program.RunProcessCaptureOutput(
                    adbExe,
                    new[]
                    {
                        "shell",
                        "pm disable-user --user 0 com.google.android.gms 2>/dev/null; " +
                        "pm disable-user --user 0 com.mgoogle.android.gms 2>/dev/null"
                    },
                    4000);
            }

            // 2. Reconcile Custom Vending LVL Stub (com.android.vending -> 0x0001 NOT_LICENSED)
            if (cfg.InjectVendingStubOnBoot)
            {
                string vendingPath = Program.RunProcessCaptureOutput(
                    adbExe,
                    new[] { "shell", "pm path com.android.vending 2>/dev/null" },
                    3500).Trim();

                if (!vendingPath.Contains("package:", StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(vendingApk))
                    {
                        logger?.Invoke($"[GMS Interop] Pre-flight injecting Vending LVL Stub ({Path.GetFileName(vendingApk)} -> 0x0001 NOT_LICENSED)...");
                        Program.RunProcessCaptureOutput(
                            adbExe,
                            new[] { "install", "-r", "-g", "--force-queryable", vendingApk },
                            25000);
                    }
                }

                Program.RunProcessCaptureOutput(
                    adbExe,
                    new[] { "shell", "pm enable com.android.vending 2>/dev/null" },
                    3500);
            }
            else
            {
                logger?.Invoke("[GMS Interop] Boot toggle OFF for Vending LVL Stub -> disabling com.android.vending...");
                Program.RunProcessCaptureOutput(
                    adbExe,
                    new[] { "shell", "pm disable-user --user 0 com.android.vending 2>/dev/null" },
                    3500);
            }

            Program.RunProcessCaptureOutput(
                adbExe,
                new[] { "shell", "setprop", "debug.laforge.gms_preflight_done", "1" },
                2500);
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[GMS Interop Warning] {ex.Message}");
        }
    }

    public static GmsInteropLiveStatus QueryInteropStatus(string adbExe, string? baseDir = null)
    {
        var cfg = LoadConfig();
        string microGApk = ResolveGmsAssetPath(baseDir, cfg.MicroGApkRelativePath);
        string vendingApk = ResolveGmsAssetPath(baseDir, cfg.VendingStubApkRelativePath);

        var status = new GmsInteropLiveStatus
        {
            MicroGApkPresentOnHost = File.Exists(microGApk),
            VendingApkPresentOnHost = File.Exists(vendingApk)
        };

        if (string.IsNullOrWhiteSpace(adbExe) || !File.Exists(adbExe))
        {
            return status;
        }

        if (string.IsNullOrEmpty(Program.GetConnectedAdbDevice(adbExe)))
        {
            return status;
        }

        status.DeviceOnline = true;
        try
        {
            string shellCmd =
                "echo '===ENABLED==='; pm list packages -e; " +
                "echo '===DISABLED==='; pm list packages -d; " +
                "echo '===GMS_VER==='; dumpsys package com.google.android.gms 2>/dev/null | grep -m1 versionName; " +
                "dumpsys package com.mgoogle.android.gms 2>/dev/null | grep -m1 versionName; " +
                "echo '===VENDING_VER==='; dumpsys package com.android.vending 2>/dev/null | grep -m1 versionName";

            string outText = Program.RunProcessCaptureOutput(adbExe, new[] { "shell", shellCmd }, 4500);

            foreach (string candidate in CandidateMicroGPackages)
            {
                if (Regex.IsMatch(outText, $@"package:{Regex.Escape(candidate)}\b", RegexOptions.IgnoreCase))
                {
                    status.MicroGInstalled = true;
                    status.DetectedMicroGPackage = candidate;
                    break;
                }
            }

            int enabledIdx = outText.IndexOf("===ENABLED===", StringComparison.Ordinal);
            int disabledIdx = outText.IndexOf("===DISABLED===", StringComparison.Ordinal);
            int gmsVerIdx = outText.IndexOf("===GMS_VER===", StringComparison.Ordinal);
            int vendVerIdx = outText.IndexOf("===VENDING_VER===", StringComparison.Ordinal);

            string enabledSection = (enabledIdx >= 0 && disabledIdx > enabledIdx)
                ? outText[enabledIdx..disabledIdx]
                : outText;

            if (status.MicroGInstalled)
            {
                status.MicroGEnabled = Regex.IsMatch(
                    enabledSection,
                    $@"package:{Regex.Escape(status.DetectedMicroGPackage)}\b",
                    RegexOptions.IgnoreCase);
            }

            if (Regex.IsMatch(outText, @"package:com\.android\.vending\b", RegexOptions.IgnoreCase))
            {
                status.VendingStubInstalled = true;
                status.VendingStubEnabled = Regex.IsMatch(
                    enabledSection,
                    @"package:com\.android\.vending\b",
                    RegexOptions.IgnoreCase);
            }

            if (gmsVerIdx >= 0 && vendVerIdx > gmsVerIdx)
            {
                string gmsVerBlock = outText[gmsVerIdx..vendVerIdx];
                var m = Regex.Match(gmsVerBlock, @"versionName=([^\s\r\n]+)");
                if (m.Success) status.MicroGVersion = m.Groups[1].Value.Trim();
            }

            if (vendVerIdx >= 0)
            {
                string vendVerBlock = outText[vendVerIdx..];
                var m = Regex.Match(vendVerBlock, @"versionName=([^\s\r\n]+)");
                if (m.Success) status.VendingStubVersion = m.Groups[1].Value.Trim();
            }
        }
        catch
        {
            // Ignore transient ADB errors
        }

        return status;
    }

    /// <summary>
    /// Launches the microG Settings UI directly via 'adb shell am start -n &lt;pkg&gt;/org.microg.gms.ui.SettingsActivity'
    /// inside an isolated scrcpy virtual display window so the user can inspect and configure microG on the desktop.
    /// </summary>
    public static string LaunchMicroGSettings(string adbExe, string scrcpyExe, string? baseDir = null)
    {
        if (!File.Exists(adbExe))
        {
            return "ADB executable not found.";
        }

        var status = QueryInteropStatus(adbExe, baseDir);
        if (!status.DeviceOnline)
        {
            return "Android runtime is offline. Launch an app or boot the runtime first.";
        }

        if (!status.MicroGInstalled)
        {
            ExecutePreFlightInjection(adbExe, baseDir, null, forceReconcile: true);
            status = QueryInteropStatus(adbExe, baseDir);
        }

        string pkg = status.DetectedMicroGPackage;
        string component = $"{pkg}/org.microg.gms.ui.SettingsActivity";

        // Ensure microG is enabled before launching its settings activity
        Program.RunProcessCaptureOutput(adbExe, new[] { "shell", $"pm enable {pkg} 2>/dev/null" }, 3000);

        if (File.Exists(scrcpyExe))
        {
            using var vdMutex = new Mutex(false, @"Local\LaForge_VirtualDisplayAllocMutex");
            bool lockTaken = false;
            try
            {
                try
                {
                    lockTaken = vdMutex.WaitOne(10000);
                }
                catch (AbandonedMutexException)
                {
                    lockTaken = true;
                }

                // Focus existing microG Settings window if already open
                var existing = LaForgeAppRegistry.FindAllScrcpyWindows()
                    .FirstOrDefault(w => w.Title.Equals("LaForge - microG Settings", StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    var dummyPolicy = new AppPolicyConfig
                    {
                        PackageName = pkg,
                        AppTitle = "LaForge - microG Settings",
                        LastScrcpyPid = existing.ProcessId
                    };
                    LaForgeAppRegistry.TryRestoreOrFocusAppWindow(dummyPolicy);
                }
                else
                {
                    var prevIds = Program.GetActiveScrcpyDisplayIds(adbExe);
                    string iconPath = Path.Combine(Path.GetDirectoryName(scrcpyExe) ?? "", "scrcpy.png");
                    var scrcpyArgs = new List<string>
                    {
                        "--window-title=LaForge - microG Settings",
                        "--window-width=540",
                        "--window-height=960",
                        "--max-fps=60",
                        "--stay-awake",
                        "--new-display=540x960/160",
                        "--no-vd-system-decorations",
                        "--no-audio"
                    };

                    Program.EnsureSingleScrcpyInstance(
                        scrcpyExe,
                        scrcpyArgs,
                        iconPath,
                        "LaForge - microG Settings",
                        previousPid: 0,
                        freeAspectRatio: true,
                        killExisting: false);

                    int displayId = Program.WaitForNewScrcpyDisplayId(adbExe, prevIds);
                    if (displayId > 0)
                    {
                        Program.RunProcessCaptureOutput(
                            adbExe,
                            new[] { "shell", $"wm user-rotation -d {displayId} lock 0; wm fixed-to-user-rotation -d {displayId} disabled; wm set-ignore-orientation-request -d {displayId} true" },
                            2500);
                        string outVd = Program.RunProcessCaptureOutput(
                            adbExe,
                            new[] { "shell", "am", "start", "--display", displayId.ToString(), "-n", component },
                            6000);
                        return $"Launched {component} on Display #{displayId}: {outVd.Trim()}";
                    }
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

        string outStd = Program.RunProcessCaptureOutput(
            adbExe,
            new[] { "shell", "am", "start", "-n", component },
            6000);
        return $"Launched {component}: {outStd.Trim()}";
    }

    public static string ClearMicroGData(string adbExe)
    {
        if (!File.Exists(adbExe))
        {
            return "ADB executable not found.";
        }

        string outText = Program.RunProcessCaptureOutput(
            adbExe,
            new[]
            {
                "shell",
                "pm clear com.google.android.gms 2>/dev/null; pm clear com.mgoogle.android.gms 2>/dev/null"
            },
            6000);
        return string.IsNullOrWhiteSpace(outText) ? "Cleared microG app data." : $"microG data reset: {outText.Trim()}";
    }
}
