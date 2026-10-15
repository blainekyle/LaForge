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
        "com.android.vending",
        "com.speedsoftware.rootexplorer"
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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaForge"),
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
    /// Applies the headless AOSP Android 14 (API 34) signature spoofing patch (`runtime/gms/services_patched.jar`)
    /// to `/system/framework/services.jar` via `adb root` + bind-mount BEFORE microG is injected.
    /// </summary>
    public static void ApplySignatureSpoofingPatch(
        string adbExe,
        string? baseDir = null,
        Action<string>? logger = null)
    {
        try
        {
            string activeProp = Program.RunProcessCaptureOutput(
                adbExe,
                new[] { "shell", "getprop", "debug.laforge.sigspoof_active" },
                2500).Trim();
            if (activeProp == "1")
            {
                return;
            }

            // Check if `/data/system/laforge_services.jar` is already mounted over `/system/framework/services.jar`
            string mountCheck = Program.RunProcessCaptureOutput(
                adbExe,
                new[] { "shell", "mount | grep '/system/framework/services.jar'" },
                2500).Trim();
            if (mountCheck.Contains("/system/framework/services.jar", StringComparison.OrdinalIgnoreCase))
            {
                Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "setenforce 0 2>/dev/null; setprop debug.laforge.sigspoof_active 1" }, 2000);
                return;
            }

            string patchedJar = ResolveGmsAssetPath(baseDir, @"runtime\gms\services_patched.jar");
            if (!File.Exists(patchedJar))
            {
                return;
            }

            logger?.Invoke("[GMS Interop] Applying AOSP services.jar signature spoofing patch before microG injection...");
            Program.RunProcessCaptureOutput(adbExe, new[] { "root" }, 8000);
            Program.RunProcessCaptureOutput(adbExe, new[] { "wait-for-device" }, 10000);
            Program.RunProcessCaptureOutput(adbExe, new[] { "push", patchedJar, "/data/system/laforge_services.jar" }, 25000);

            string mountCmd =
                "setenforce 0 2>/dev/null; " +
                "chmod 644 /data/system/laforge_services.jar; " +
                "chown root:root /data/system/laforge_services.jar; " +
                "chcon u:object_r:system_file:s0 /data/system/laforge_services.jar; " +
                "umount /system/framework/services.jar 2>/dev/null; " +
                "mount -o bind /data/system/laforge_services.jar /system/framework/services.jar; " +
                "if [ ! -f /data/system/laforge_services.odex ]; then " +
                "  /apex/com.android.art/bin/dex2oat64 --dex-file=/system/framework/services.jar " +
                "    --oat-file=/data/system/laforge_services.odex " +
                "    --app-image-file=/data/system/laforge_services.art " +
                "    --instruction-set=x86_64 --compiler-filter=verify 2>/dev/null; " +
                "fi; " +
                "chmod 644 /data/system/laforge_services.*; " +
                "chown root:root /data/system/laforge_services.*; " +
                "chcon u:object_r:system_file:s0 /data/system/laforge_services.*; " +
                "umount /system/framework/oat/x86_64/services.art 2>/dev/null; " +
                "umount /system/framework/oat/x86_64/services.odex 2>/dev/null; " +
                "umount /system/framework/oat/x86_64/services.vdex 2>/dev/null; " +
                "mount -o bind /data/system/laforge_services.art /system/framework/oat/x86_64/services.art; " +
                "mount -o bind /data/system/laforge_services.odex /system/framework/oat/x86_64/services.odex; " +
                "mount -o bind /data/system/laforge_services.vdex /system/framework/oat/x86_64/services.vdex; " +
                "setprop sys.boot_completed 0; stop; start";

            Program.RunProcessCaptureOutput(adbExe, new[] { "shell", mountCmd }, 25000);

            DateTime deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                string boot = Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "getprop", "sys.boot_completed" }, 3000).Trim();
                if (boot == "1")
                {
                    Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "setprop", "debug.laforge.sigspoof_active", "1" }, 2000);
                    logger?.Invoke("[GMS Interop] AOSP signature spoofing active (LaForgeSpoof enabled in system_server).");
                    break;
                }
                Thread.Sleep(1000);
            }
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[GMS Spoof Warning] {ex.Message}");
        }
    }

    /// <summary>
    /// Diagnostic check after microG injection: polls `adb shell top -b -n 1 -m 5` and `adb logcat -d`
    /// to verify signature spoofing (`LaForgeSpoof`) and detect any `SecurityException` tied to
    /// `com.google.android.gms` or `PackageManager`.
    /// </summary>
    public static void RunPostInjectionDiagnostics(string adbExe, Action<string>? logger = null)
    {
        try
        {
            string topOut = Program.RunProcessCaptureOutput(
                adbExe,
                new[] { "shell", "top", "-b", "-n", "1", "-m", "5" },
                5000).Trim();

            string logOut = Program.RunProcessCaptureOutput(
                adbExe,
                new[] { "shell", "logcat", "-d", "-t", "300" },
                5000);

            int spoofCount = 0;
            var secExceptions = new List<string>();
            foreach (string rawLine in logOut.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Contains("LaForgeSpoof", StringComparison.OrdinalIgnoreCase))
                {
                    spoofCount++;
                }
                else if (line.Contains("SecurityException", StringComparison.OrdinalIgnoreCase) &&
                         (line.Contains("com.google.android.gms", StringComparison.OrdinalIgnoreCase) ||
                          line.Contains("PackageManager", StringComparison.OrdinalIgnoreCase) ||
                          line.Contains("signature", StringComparison.OrdinalIgnoreCase)))
                {
                    secExceptions.Add(line);
                }
            }

            logger?.Invoke($"[GMS Diagnostics] Spoof events={spoofCount}, GMS SecurityExceptions={secExceptions.Count}");
            if (!string.IsNullOrWhiteSpace(topOut))
            {
                string firstCpuLine = topOut.Split('\n').FirstOrDefault(l => l.Contains("%cpu", StringComparison.OrdinalIgnoreCase))?.Trim() ?? "";
                if (!string.IsNullOrEmpty(firstCpuLine))
                {
                    logger?.Invoke($"[GMS Diagnostics] CPU Summary: {firstCpuLine}");
                }
            }
            foreach (string sec in secExceptions.TakeLast(3))
            {
                logger?.Invoke($"[GMS SecurityException] {sec}");
            }
        }
        catch
        {
            // Non-fatal diagnostic check
        }
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
                    Program.RunProcessCaptureOutput(
                        adbExe,
                        new[] { "shell", "dumpsys deviceidle whitelist +com.google.android.gms 2>/dev/null; dumpsys deviceidle whitelist +com.mgoogle.android.gms 2>/dev/null" },
                        2500);
                    return;
                }
            }

            var cfg = LoadConfig();
            string microGApk = ResolveGmsAssetPath(baseDir, cfg.MicroGApkRelativePath);
            string vendingApk = ResolveGmsAssetPath(baseDir, cfg.VendingStubApkRelativePath);

            // 0. Ensure AOSP signature spoofing patch is active BEFORE microG injection
            if (cfg.InjectMicroGOnBoot || cfg.InjectVendingStubOnBoot)
            {
                ApplySignatureSpoofingPatch(adbExe, baseDir, logger);
            }

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
                        "pm grant com.google.android.gms android.permission.FAKE_PACKAGE_SIGNATURE 2>/dev/null; " +
                        "dumpsys deviceidle whitelist +com.google.android.gms 2>/dev/null; " +
                        "dumpsys deviceidle whitelist +com.mgoogle.android.gms 2>/dev/null; " +
                        "for p in POST_NOTIFICATIONS GET_ACCOUNTS READ_PHONE_STATE ACCESS_FINE_LOCATION ACCESS_COARSE_LOCATION ACCESS_BACKGROUND_LOCATION READ_CONTACTS; do " +
                        "pm grant com.google.android.gms android.permission.$p 2>/dev/null; done; " +
                        "cmd appops set com.google.android.gms SYSTEM_ALERT_WINDOW allow 2>/dev/null"
                    },
                    4000);

                // GsfProxy (com.google.android.gsf) is required by microG's self-test.
                string gsfApk = Path.Combine(Path.GetDirectoryName(microGApk) ?? baseDir ?? AppContext.BaseDirectory, "gsf-proxy.apk");
                string gsfInstalled = Program.RunProcessCaptureOutput(
                    adbExe,
                    new[] { "shell", "pm path com.google.android.gsf 2>/dev/null" },
                    4000).Trim();
                if (!gsfInstalled.Contains("package:", StringComparison.OrdinalIgnoreCase) && File.Exists(gsfApk))
                {
                    logger?.Invoke("[GMS Interop] Installing GsfProxy (com.google.android.gsf)...");
                    Program.RunProcessCaptureOutput(adbExe, new[] { "install", "-r", "-g", gsfApk }, 30000);
                }
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
                    new[]
                    {
                        "shell",
                        "pm enable com.android.vending 2>/dev/null; " +
                        "pm grant com.android.vending android.permission.FAKE_PACKAGE_SIGNATURE 2>/dev/null"
                    },
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

            // 3. Run Post-Injection Diagnostic Check (CPU top 5 + Logcat SecurityException / Spoof check)
            RunPostInjectionDiagnostics(adbExe, logger);

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
