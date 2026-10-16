using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace LaForge.Launcher;

/// <summary>
/// First-run provisioning of the shared Android runtime on a machine that has never run LaForge.
/// The installer ships only the bridge (adb/scrcpy), GMS assets and the ARM64 translation bundle; the Android
/// emulator and the Android 14 system image are far too large to embed, so they are downloaded once from Google's
/// official SDK repository (pinned to the exact versions LaForge was validated against), the AVD is created, the
/// 64-bit ARM translation layer (libndk_translation) is injected, and the default_boot quick-boot snapshot is saved.
/// Every step is idempotent so an interrupted run resumes instead of starting over.
/// </summary>
public static class RuntimeProvisioner
{
    public const string AvdName = "LaForge_x86_64_API34";
    public const string SnapshotName = "default_boot";
    public const string DownloadSizeText = "about 1.2 GB download, about 4 GB on disk";

    // Pinned to the versions validated for LaForge (the services.jar signature-spoofing patch is built against this exact image).
    private const string EmulatorUrl = "https://dl.google.com/android/repository/emulator-windows_x64-16428233.zip";
    private const string PlatformToolsUrl = "https://dl.google.com/android/repository/platform-tools_r37.0.1-win.zip";
    private const string ImageUrl = "https://dl.google.com/android/repository/sys-img/android/x86_64-34_r04.zip";
    private const string SystemImgSha256 = "CEBCC7A69782898F6BC28372EB17ADA582D5F31CF851180E39A5561917B6B998";
    private const string ArmBundleRelPath = @"arm64\ndk_translation_arm64_api34.tar.gz";
    private const string ArmDoneMarker = "laforge_arm64.done";

    private static readonly string ImageRelDir = Path.Combine("system-images", "android-34", "default", "x86_64");

    // ---------------------------------------------------------------------------------------------
    // Locations
    // ---------------------------------------------------------------------------------------------

    public static string GetDefaultRuntimeRoot()
    {
        string? env = Environment.GetEnvironmentVariable("LAFORGE_RUNTIME_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaForge", "runtime");
    }

    public static string GetAvdHome()
    {
        string? env = Environment.GetEnvironmentVariable("LAFORGE_AVD_HOME");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd");
    }

    public static string GetAvdDir(string avdName = AvdName) => Path.Combine(GetAvdHome(), $"{avdName}.avd");

    private static bool IsRuntimeComplete(string root) =>
        File.Exists(Path.Combine(root, "emulator", "emulator.exe")) &&
        File.Exists(Path.Combine(root, ImageRelDir, "system.img"));

    /// <summary>
    /// Returns the runtime root that already holds a complete emulator + system image (explicit override, the install
    /// location, or a development checkout next to the executable); otherwise the default install location to provision into.
    /// </summary>
    public static string FindRuntimeRoot()
    {
        string? env = Environment.GetEnvironmentVariable("LAFORGE_RUNTIME_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env);
        }

        string defaultRoot = GetDefaultRuntimeRoot();
        if (IsRuntimeComplete(defaultRoot))
        {
            return defaultRoot;
        }

        string current = Path.GetFullPath(AppContext.BaseDirectory);
        for (int depth = 0; depth <= 3; depth++)
        {
            string candidate = Path.Combine(current, "runtime");
            if (IsRuntimeComplete(candidate))
            {
                return candidate;
            }
            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null) break;
            current = parent.FullName;
        }

        if (IsRuntimeComplete(@"c:\ag\APKForge\runtime"))
        {
            return @"c:\ag\APKForge\runtime";
        }
        return defaultRoot;
    }

    /// <summary>
    /// True once the emulator, system image, AVD, ARM64 translation overlay and default_boot snapshot all exist.
    /// </summary>
    public static bool IsRuntimeReady()
    {
        try
        {
            if (!IsRuntimeComplete(FindRuntimeRoot())) return false;
            string avdDir = GetAvdDir();
            if (!File.Exists(Path.Combine(GetAvdHome(), $"{AvdName}.ini"))) return false;
            if (!File.Exists(Path.Combine(avdDir, "config.ini"))) return false;
            if (!File.Exists(Path.Combine(avdDir, "snapshots", SnapshotName, "snapshot.pb"))) return false;
            // The ARM64 overlay lives in the AVD's writable system layer; either our marker or that layer proves it was applied.
            return File.Exists(Path.Combine(avdDir, ArmDoneMarker)) || File.Exists(Path.Combine(avdDir, "system.img.qcow2"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Points the emulator at LaForge's own SDK root and AVD home for this process tree, so a machine that already has
    /// Android Studio (with its own ANDROID_SDK_ROOT / ANDROID_AVD_HOME) cannot redirect the emulator elsewhere.
    /// </summary>
    public static void ApplyProcessEnvironment()
    {
        try
        {
            string root = FindRuntimeRoot();
            Environment.SetEnvironmentVariable("ANDROID_SDK_ROOT", root);
            Environment.SetEnvironmentVariable("ANDROID_HOME", root);
            Environment.SetEnvironmentVariable("ANDROID_AVD_HOME", GetAvdHome());
        }
        catch
        {
            // Non-fatal
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Provisioning
    // ---------------------------------------------------------------------------------------------

    /// <param name="adbExe">Resolved bridge adb.exe.</param>
    /// <param name="log">Persistent log lines.</param>
    /// <param name="status">Transient one-line progress (download percentage, current step).</param>
    public static async Task<bool> ProvisionAsync(
        string adbExe,
        Action<string> log,
        Action<string> status,
        CancellationToken ct = default)
    {
        // Several forged apps may be launched at once on a fresh machine: only one may provision, the rest wait.
        using var mutex = new Mutex(false, @"Global\LaForge_RuntimeProvisioner");
        bool held = false;
        try
        {
            try
            {
                if (!mutex.WaitOne(0))
                {
                    status("Another LaForge window is setting up the Android runtime — waiting for it to finish…");
                    log("Another LaForge window is already provisioning the Android runtime; waiting for it.");
                    held = await Task.Run(() => mutex.WaitOne(TimeSpan.FromMinutes(90)), ct);
                    if (!held) return false;
                }
                else
                {
                    held = true;
                }
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }

            if (IsRuntimeReady())
            {
                log("Android runtime is already provisioned.");
                return true;
            }

            return await ProvisionCoreAsync(adbExe, log, status, ct);
        }
        catch (OperationCanceledException)
        {
            log("Android runtime setup was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            log($"[Provisioning Error] {ex.Message}");
            log("Setup keeps its progress; launch the app again to resume. Details: " + GetLogPath());
            AppendFileLog($"ERROR {ex}");
            return false;
        }
        finally
        {
            if (held)
            {
                try { mutex.ReleaseMutex(); } catch { /* ignore */ }
            }
        }
    }

    private static async Task<bool> ProvisionCoreAsync(string adbExe, Action<string> log, Action<string> status, CancellationToken ct)
    {
        string root = GetDefaultRuntimeRoot();
        if (IsRuntimeComplete(FindRuntimeRoot())) root = FindRuntimeRoot();
        Directory.CreateDirectory(root);
        string tmp = Path.Combine(root, ".tmp");
        Directory.CreateDirectory(tmp);
        Environment.SetEnvironmentVariable("ANDROID_SDK_ROOT", root);
        Environment.SetEnvironmentVariable("ANDROID_HOME", root);
        Environment.SetEnvironmentVariable("ANDROID_AVD_HOME", GetAvdHome());

        log("=== LaForge one-time Android runtime setup ===");
        log($"Runtime location: {root}");
        long freeBytes = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
        if (freeBytes < 6L * 1024 * 1024 * 1024)
        {
            throw new IOException($"At least 6 GB of free disk space is required on {Path.GetPathRoot(root)} (found {freeBytes / (1024 * 1024 * 1024.0):F1} GB).");
        }

        // 1. Android Emulator ------------------------------------------------------------------
        string emulatorDir = Path.Combine(root, "emulator");
        string emulatorExe = Path.Combine(emulatorDir, "emulator.exe");
        if (!File.Exists(emulatorExe))
        {
            string zip = Path.Combine(tmp, "emulator.zip");
            await DownloadAsync(EmulatorUrl, zip, "Android Emulator", log, status, ct);
            status("Extracting Android Emulator…");
            log("Extracting Android Emulator…");
            string stage = Path.Combine(tmp, "emulator_x");
            ResetDir(stage);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, stage, overwriteFiles: true), ct);
            string extracted = Path.Combine(stage, "emulator");
            if (!File.Exists(Path.Combine(extracted, "emulator.exe")))
            {
                throw new InvalidDataException("The downloaded emulator archive did not contain emulator.exe.");
            }
            if (Directory.Exists(emulatorDir)) Directory.Delete(emulatorDir, recursive: true);
            Directory.Move(extracted, emulatorDir);
            TryDelete(zip);
            ResetDir(stage, createAfter: false);
        }
        else
        {
            log("Android Emulator already present.");
        }

        // The emulator treats a root without platform-tools as an invalid SDK ("Broken AVD system path"), so install it too.
        string platformToolsDir = Path.Combine(root, "platform-tools");
        if (!File.Exists(Path.Combine(platformToolsDir, "adb.exe")))
        {
            string zip = Path.Combine(tmp, "platform-tools.zip");
            await DownloadAsync(PlatformToolsUrl, zip, "Android Platform-Tools", log, status, ct);
            status("Extracting Android Platform-Tools…");
            string stage = Path.Combine(tmp, "pt_x");
            ResetDir(stage);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, stage, overwriteFiles: true), ct);
            string extracted = Path.Combine(stage, "platform-tools");
            if (!File.Exists(Path.Combine(extracted, "adb.exe")))
            {
                throw new InvalidDataException("The downloaded platform-tools archive did not contain adb.exe.");
            }
            if (Directory.Exists(platformToolsDir)) Directory.Delete(platformToolsDir, recursive: true);
            Directory.Move(extracted, platformToolsDir);
            TryDelete(zip);
            ResetDir(stage, createAfter: false);
        }

        // The hypervisor is the one prerequisite we cannot install for the user: tell them precisely what is missing.
        string accel = Program.RunProcessCaptureOutput(emulatorExe, new[] { "-accel-check" }, 20000);
        AppendFileLog("accel-check: " + accel.Replace("\r", "").Replace("\n", " | "));
        if (accel.IndexOf("installed and usable", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "Hardware virtualization is not available. Enable 'Windows Hypervisor Platform' (and 'Virtual Machine Platform') in " +
                "'Turn Windows features on or off', turn on virtualization (VT-x/AMD-V) in BIOS/UEFI if needed, restart Windows, then launch the app again. " +
                "Emulator said: " + accel.Replace("\r", " ").Replace("\n", " ").Trim());
        }
        log("Hypervisor check passed (WHPX is installed and usable).");

        // 2. Android 14 (API 34) x86_64 system image -------------------------------------------
        string imageDir = Path.Combine(root, ImageRelDir);
        if (!File.Exists(Path.Combine(imageDir, "system.img")))
        {
            string zip = Path.Combine(tmp, "sysimg.zip");
            await DownloadAsync(ImageUrl, zip, "Android 14 system image", log, status, ct);
            status("Extracting Android 14 system image…");
            log("Extracting Android 14 system image…");
            string stage = Path.Combine(tmp, "image_x");
            ResetDir(stage);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, stage, overwriteFiles: true), ct);
            string? found = Directory.GetFiles(stage, "system.img", SearchOption.AllDirectories).FirstOrDefault();
            if (found == null) throw new InvalidDataException("The downloaded system image archive did not contain system.img.");

            status("Verifying Android 14 system image…");
            string actual = await Task.Run(() => Sha256Of(found), ct);
            if (!actual.Equals(SystemImgSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(zip);
                TryDelete(stage, directory: true);
                throw new InvalidDataException($"System image checksum mismatch (expected {SystemImgSha256}, got {actual}). The download was discarded; launch again to retry.");
            }
            log("System image checksum verified.");

            Directory.CreateDirectory(Path.GetDirectoryName(imageDir)!);
            if (Directory.Exists(imageDir)) Directory.Delete(imageDir, recursive: true);
            Directory.Move(Path.GetDirectoryName(found)!, imageDir);
            TryDelete(zip);
            ResetDir(stage, createAfter: false);
        }
        else
        {
            log("Android 14 system image already present.");
        }

        // 3. AVD ---------------------------------------------------------------------------------
        CreateAvdIfMissing(log);

        // 4. Boot, inject 64-bit ARM translation, snapshot ---------------------------------------
        await BootAndFinalizeAsync(adbExe, emulatorExe, root, log, status, ct);

        TryDelete(tmp, directory: true);
        log("=== Android runtime setup complete ===");
        status("Android runtime ready.");
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // AVD
    // ---------------------------------------------------------------------------------------------

    private static void CreateAvdIfMissing(Action<string> log)
    {
        string avdHome = GetAvdHome();
        string avdDir = GetAvdDir();
        string iniPath = Path.Combine(avdHome, $"{AvdName}.ini");
        Directory.CreateDirectory(avdDir);

        string cfg = Path.Combine(avdDir, "config.ini");
        if (!File.Exists(cfg))
        {
            using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LaForge.Launcher.avd_config.ini");
            if (s == null) throw new InvalidOperationException("Embedded AVD configuration template is missing.");
            using var reader = new StreamReader(s, Encoding.ASCII);
            File.WriteAllText(cfg, reader.ReadToEnd(), new UTF8Encoding(false));
            log("Created AVD configuration.");
        }

        if (!File.Exists(iniPath))
        {
            File.WriteAllText(iniPath,
                "avd.ini.encoding=UTF-8\r\n" +
                $"path={avdDir}\r\n" +
                $"path.rel=avd\\{AvdName}.avd\r\n" +
                "target=android-34\r\n",
                new UTF8Encoding(false));
            log($"Registered AVD '{AvdName}'.");
        }

        // Apply the user's camera/GPS selections (Master Settings defaults on a new install).
        LaForgeMasterSettings.SyncAvdHardwareIni(LaForgeMasterSettings.Load(), AvdName);
    }

    // ---------------------------------------------------------------------------------------------
    // Guest provisioning
    // ---------------------------------------------------------------------------------------------

    private static async Task BootAndFinalizeAsync(
        string adbExe, string emulatorExe, string root, Action<string> log, Action<string> status, CancellationToken ct)
    {
        string avdDir = GetAvdDir();
        string armMarker = Path.Combine(avdDir, ArmDoneMarker);
        string? armBundle = LocateAsset(root, ArmBundleRelPath);
        if (armBundle == null && !File.Exists(armMarker))
        {
            throw new FileNotFoundException("The 64-bit ARM translation bundle (arm64\\ndk_translation_arm64_api34.tar.gz) is missing from this installation. Re-run the LaForge installer.");
        }

        var rt = new RuntimeConfig
        {
            EmulatorPath = emulatorExe,
            AdbPath = adbExe,
            AvdName = AvdName,
            Snapshot = SnapshotName,
            GpuMode = "host",
            Whpx = true,
            Headless = true,
            BootTimeoutSec = 600
        };

        Program.CleanStaleAvdLocks(AvdName);
        await Task.Run(() => Program.StopHeadlessEmulator(adbExe), ct);

        status("Starting Android for the first time (this takes a few minutes)…");
        log("First boot of the Android guest (writable system layer, no snapshot)…");
        using var emu = StartEmulatorLogged(emulatorExe, Program.BuildEmulatorArgs(rt, forExecution: true));
        try
        {
            await WaitForBootAsync(adbExe, emu, 900, "first boot", status, ct);

            if (!File.Exists(armMarker))
            {
                bool armActive = Adb(adbExe, 8000, "shell", "getprop", "ro.product.cpu.abilist").Contains("arm64-v8a", StringComparison.OrdinalIgnoreCase);
                if (!armActive)
                {
                    await InjectArmTranslationAsync(adbExe, emu, armBundle!, log, status, ct);
                }
                File.WriteAllText(armMarker, DateTime.UtcNow.ToString("O"));
            }

            status("Configuring the Android guest and saving the quick-boot snapshot…");
            log("Configuring guest (display, microG, licensing stub) and saving the default_boot snapshot…");
            await Task.Run(() => Program.ConfigureGuestDisplay(adbExe, new WindowConfig()), ct);

            // ConfigureGuestDisplay saves the snapshot when none matches; make sure it exists before we hand over.
            if (!File.Exists(Path.Combine(avdDir, "snapshots", SnapshotName, "snapshot.pb")))
            {
                Adb(adbExe, 60000, "emu", "avd", "snapshot", "save", SnapshotName);
            }
            if (!File.Exists(Path.Combine(avdDir, "snapshots", SnapshotName, "snapshot.pb")))
            {
                throw new InvalidOperationException("The default_boot snapshot could not be saved.");
            }
            log("Quick-boot snapshot saved.");
        }
        catch
        {
            try { Program.StopHeadlessEmulator(adbExe); } catch { /* ignore */ }
            throw;
        }
        // Leave the freshly provisioned emulator running: the caller attaches to it directly.
    }

    private static async Task InjectArmTranslationAsync(
        string adbExe, EmulatorHandle emu, string bundle, Action<string> log, Action<string> status, CancellationToken ct)
    {
        status("Enabling 64-bit ARM app support…");
        log("Enabling the writable system layer (disables dm-verity; one reboot required)…");
        Adb(adbExe, 15000, "root");
        Adb(adbExe, 15000, "wait-for-device");
        string remount = Adb(adbExe, 60000, "remount");
        AppendFileLog("remount#1: " + remount);

        if (remount.IndexOf("reboot", StringComparison.OrdinalIgnoreCase) >= 0 ||
            remount.IndexOf("Remount succeeded", StringComparison.OrdinalIgnoreCase) < 0)
        {
            Adb(adbExe, 15000, "reboot");
            await Task.Delay(8000, ct);
            await WaitForBootAsync(adbExe, emu, 600, "reboot", status, ct);
            Adb(adbExe, 15000, "root");
            Adb(adbExe, 15000, "wait-for-device");
            remount = Adb(adbExe, 60000, "remount");
            AppendFileLog("remount#2: " + remount);
        }
        if (remount.IndexOf("Remount succeeded", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("Could not remount the Android system partition writable: " + remount.Trim());
        }

        log("Installing the ARM64 translation layer (libndk_translation v0.2.3)…");
        string script = string.Join("\n", new[]
        {
            "#!/system/bin/sh",
            "set -e",
            "cd /system",
            "tar --numeric-owner -xzf /data/local/tmp/ndk_translation_arm64_api34.tar.gz",
            "chcon -R u:object_r:system_file:s0 /system/bin/arm64 /system/bin/ndk_translation_program_runner_binfmt_misc_arm64 /system/etc/binfmt_misc /system/etc/cpuinfo.arm64.txt /system/etc/cpuinfo.arm.txt /system/etc/init/ndk_translation.rc",
            "chcon u:object_r:system_linker_config_file:s0 /system/etc/ld.config.arm64.txt /system/etc/ld.config.arm.txt",
            "chcon -R u:object_r:system_lib_file:s0 /system/lib64/arm64 /system/lib64/libblasV8.so /system/lib64/libclcore_neon.bc /system/lib64/libndk_translation*",
            "sed -i 's/^ro\\.system\\.product\\.cpu\\.abilist=x86_64$/ro.system.product.cpu.abilist=x86_64,arm64-v8a/' /system/build.prop",
            "sed -i 's/^ro\\.system\\.product\\.cpu\\.abilist64=x86_64$/ro.system.product.cpu.abilist64=x86_64,arm64-v8a/' /system/build.prop",
            "sed -i 's/^ro\\.dalvik\\.vm\\.native\\.bridge=0$/ro.dalvik.vm.native.bridge=libndk_translation.so\\nro.dalvik.vm.isa.arm64=x86_64\\nro.dalvik.vm.isa.arm=x86\\nro.enable.native.bridge.exec=1\\nro.ndk_translation.version=0.2.3\\nro.ndk_translation.flags=accurate-sigsegv/' /system/build.prop",
            "chmod 0600 /system/build.prop",
            "chcon u:object_r:system_file:s0 /system/build.prop",
            "sed -i 's/^ro\\.vendor\\.product\\.cpu\\.abilist=x86_64$/ro.vendor.product.cpu.abilist=x86_64,arm64-v8a/' /vendor/build.prop",
            "sed -i 's/^ro\\.vendor\\.product\\.cpu\\.abilist64=x86_64$/ro.vendor.product.cpu.abilist64=x86_64,arm64-v8a/' /vendor/build.prop",
            "chmod 0600 /vendor/build.prop",
            "chcon u:object_r:vendor_file:s0 /vendor/build.prop",
            "sed -i 's/^ro\\.odm\\.product\\.cpu\\.abilist=x86_64$/ro.odm.product.cpu.abilist=x86_64,arm64-v8a/' /vendor/odm/etc/build.prop",
            "sed -i 's/^ro\\.odm\\.product\\.cpu\\.abilist64=x86_64$/ro.odm.product.cpu.abilist64=x86_64,arm64-v8a/' /vendor/odm/etc/build.prop",
            "chmod 0600 /vendor/odm/etc/build.prop",
            "chcon u:object_r:vendor_configs_file:s0 /vendor/odm/etc/build.prop",
            "grep -q 'ro.dalvik.vm.native.bridge=libndk_translation.so' /system/build.prop",
            "rm -f /data/local/tmp/ndk_translation_arm64_api34.tar.gz /data/local/tmp/laforge_arm_inject.sh",
            "sync",
            "echo LAFORGE_ARM_INJECT_OK",
            ""
        });
        string scriptPath = Path.Combine(Path.GetTempPath(), "laforge_arm_inject.sh");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        try
        {
            await Task.Run(() =>
            {
                Program.RunCheckedProcess(adbExe, new[] { "push", bundle, "/data/local/tmp/ndk_translation_arm64_api34.tar.gz" });
                Program.RunCheckedProcess(adbExe, new[] { "push", scriptPath, "/data/local/tmp/laforge_arm_inject.sh" });
            }, ct);
        }
        finally
        {
            TryDelete(scriptPath);
        }
        string result = Adb(adbExe, 120000, "shell", "sh", "/data/local/tmp/laforge_arm_inject.sh");
        AppendFileLog("inject: " + result);
        if (result.IndexOf("LAFORGE_ARM_INJECT_OK", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("Installing the ARM64 translation layer failed: " + result.Trim());
        }

        status("Restarting Android to activate 64-bit ARM support…");
        log("Rebooting the guest to activate the ARM64 translation layer…");
        Adb(adbExe, 15000, "reboot");
        await Task.Delay(8000, ct);
        await WaitForBootAsync(adbExe, emu, 600, "ARM64 reboot", status, ct);

        string abis = Adb(adbExe, 8000, "shell", "getprop", "ro.product.cpu.abilist");
        if (abis.IndexOf("arm64-v8a", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException($"ARM64 translation did not activate (abilist = '{abis.Trim()}').");
        }
        log($"64-bit ARM app support active (abilist = {abis.Trim()}).");
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static string Adb(string adbExe, int timeoutMs, params string[] args) =>
        Program.RunProcessCaptureOutput(adbExe, args, timeoutMs);

    private sealed class EmulatorHandle : IDisposable
    {
        public Process Process { get; init; } = null!;
        public Queue<string> Tail { get; } = new();
        public void Dispose()
        {
            // The emulator is intentionally left running for the caller; just release our handle.
            Process.Dispose();
        }
    }

    private static EmulatorHandle StartEmulatorLogged(string emulatorExe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = emulatorExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["QEMU_AUDIO_OUT_DRV"] = "none";

        AppendFileLog($"emulator: {emulatorExe} {string.Join(' ', args)}");
        Process p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the Android emulator.");
        var handle = new EmulatorHandle { Process = p };
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (handle.Tail)
            {
                handle.Tail.Enqueue(line);
                while (handle.Tail.Count > 40) handle.Tail.Dequeue();
            }
            AppendFileLog("emu> " + line);
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return handle;
    }

    private static async Task WaitForBootAsync(
        string adbExe, EmulatorHandle emu, int timeoutSec, string phase, Action<string> status, CancellationToken ct)
    {
        DateTime start = DateTime.UtcNow;
        DateTime deadline = start.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (emu.Process.HasExited)
            {
                string tail;
                lock (emu.Tail) tail = string.Join(" | ", emu.Tail.TakeLast(6));
                throw new InvalidOperationException($"The Android emulator exited unexpectedly during {phase} (code {emu.Process.ExitCode}). {tail}");
            }

            string dev = await Task.Run(() => Program.GetConnectedAdbDevice(adbExe), ct);
            if (!string.IsNullOrEmpty(dev))
            {
                string prop = (await Task.Run(() => Adb(adbExe, 5000, "shell", "getprop", "sys.boot_completed"), ct)).Trim();
                if (prop == "1")
                {
                    await Task.Delay(4000, ct);
                    return;
                }
            }
            status($"Waiting for Android ({phase})… {(int)(DateTime.UtcNow - start).TotalSeconds}s");
            await Task.Delay(2000, ct);
        }
        throw new TimeoutException($"Android did not finish booting within {timeoutSec}s ({phase}).");
    }

    private static async Task DownloadAsync(
        string url, string dest, string label, Action<string> log, Action<string> status, CancellationToken ct)
    {
        string part = dest + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        log(existing > 0 ? $"Resuming download of {label} ({existing / (1024 * 1024)} MB already received)…" : $"Downloading {label}…");

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LaForge-Runtime-Provisioner/1.0");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);
        using HttpResponseMessage resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Move(part, dest, overwrite: true); // .part was already complete
            return;
        }
        resp.EnsureSuccessStatusCode();

        bool resumed = resp.StatusCode == HttpStatusCode.PartialContent;
        long total = resp.Content.Headers.ContentRange?.Length
                     ?? (resp.Content.Headers.ContentLength.HasValue ? resp.Content.Headers.ContentLength.Value + (resumed ? existing : 0) : -1);
        long received = resumed ? existing : 0;

        await using (Stream net = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fs = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, useAsync: true))
        {
            byte[] buf = new byte[256 * 1024];
            var sw = Stopwatch.StartNew();
            long lastTick = 0;
            long sessionStart = received;
            int n;
            while ((n = await net.ReadAsync(buf.AsMemory(), ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                received += n;
                if (sw.ElapsedMilliseconds - lastTick >= 700)
                {
                    lastTick = sw.ElapsedMilliseconds;
                    double mb = received / (1024.0 * 1024.0);
                    double speed = (received - sessionStart) / (1024.0 * 1024.0) / Math.Max(1, sw.Elapsed.TotalSeconds);
                    status(total > 0
                        ? $"Downloading {label}: {received * 100 / total}%  ({mb:F0} / {total / (1024.0 * 1024.0):F0} MB, {speed:F1} MB/s)"
                        : $"Downloading {label}: {mb:F0} MB ({speed:F1} MB/s)");
                }
            }
        }

        if (total > 0 && new FileInfo(part).Length != total)
        {
            throw new IOException($"Download of {label} ended early ({new FileInfo(part).Length} of {total} bytes). It will resume next time.");
        }
        File.Move(part, dest, overwrite: true);
        log($"Downloaded {label}.");
    }

    private static string? LocateAsset(string root, string relPath)
    {
        string[] candidates =
        {
            Path.Combine(root, relPath),
            Path.Combine(AppContext.BaseDirectory, "runtime", relPath),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaForge", "runtime", relPath),
            Path.Combine(@"c:\ag\APKForge\runtime", relPath)
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string Sha256Of(string path)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static void ResetDir(string path, bool createAfter = true)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        if (createAfter) Directory.CreateDirectory(path);
    }

    private static void TryDelete(string path, bool directory = false)
    {
        try
        {
            if (directory) { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            else if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    public static string GetLogPath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaForge", "logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "provision.log");
    }

    private static void AppendFileLog(string line)
    {
        try
        {
            File.AppendAllText(GetLogPath(), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging failures
        }
    }
}
