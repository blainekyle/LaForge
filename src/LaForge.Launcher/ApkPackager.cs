using System.Text.Json;

namespace LaForge.Launcher;

public static class ApkPackager
{
    public static string ForgeBundle(string apkPath, string outputRootDir, bool enableHostMount = true, bool allowOnlineIcons = false, Action<string>? log = null)
    {
        ApkMetadata meta = ApkManifestParser.ParseApk(apkPath);
        string appSlug = DeriveAppSlug(meta.PackageName);
        string bespokeExeName = DeriveExecutableName(appSlug);
        string bundleDir = Path.Combine(Path.GetFullPath(outputRootDir), meta.PackageName);
        string bundleRuntimeDir = Path.Combine(bundleDir, "runtime");
        string bundleBridgeDir = Path.Combine(bundleDir, "bridge");

        Directory.CreateDirectory(bundleDir);
        Directory.CreateDirectory(bundleRuntimeDir);
        Directory.CreateDirectory(bundleBridgeDir);

        // 1. Copy APK to payload.apk
        string payloadDest = Path.Combine(bundleDir, "payload.apk");
        File.Copy(meta.ApkPath, payloadDest, overwrite: true);

        // 2. Copy launcher executable & create bespoke <AppName>.exe binary
        string baseDir = AppContext.BaseDirectory;
        foreach (string file in Directory.GetFiles(baseDir, "launcher*"))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".exe" or ".dll" or ".json")
            {
                string destFile = Path.Combine(bundleDir, Path.GetFileName(file));
                if (!Path.GetFullPath(file).Equals(Path.GetFullPath(destFile), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(file, destFile, overwrite: true);
                }
            }
        }

        string launcherExePath = Path.Combine(bundleDir, "launcher.exe");
        if (!File.Exists(launcherExePath) && !string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
        {
            // Forging from an EXE that is not named launcher* (e.g. LaForgeConsole.exe run outside the repo root): reuse the running binary.
            File.Copy(Environment.ProcessPath, launcherExePath, overwrite: true);
        }
        string bespokeExePath = Path.Combine(bundleDir, bespokeExeName);
        if (File.Exists(launcherExePath) &&
            !launcherExePath.Equals(bespokeExePath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(launcherExePath, bespokeExePath, overwrite: true);
        }

        // 3. Copy embedded scrcpy + SDL + ADB bridge binaries into bundle/bridge/
        string sourceBridgeDir = Path.Combine(baseDir, "bridge");
        if (!Directory.Exists(sourceBridgeDir))
        {
            sourceBridgeDir = Path.Combine(Directory.GetCurrentDirectory(), "bridge");
        }
        // A previously resolved real/custom icon must survive a rebuild: do not overwrite bridge/scrcpy.png with the default logo.
        string iconMarkerPath = Path.Combine(bundleBridgeDir, AppIconGenerator.IconSourceMarkerFile);
        string cachedIconSource = File.Exists(iconMarkerPath) ? File.ReadAllText(iconMarkerPath).Trim() : string.Empty;
        bool keepCachedIcon = cachedIconSource == "custom" || cachedIconSource == "embedded" || cachedIconSource.StartsWith("online:", StringComparison.Ordinal);
        if (Directory.Exists(sourceBridgeDir))
        {
            foreach (string bridgeFile in Directory.GetFiles(sourceBridgeDir))
            {
                string fileName = Path.GetFileName(bridgeFile);
                if (fileName.Equals(".gitkeep", StringComparison.OrdinalIgnoreCase)) continue;
                if (keepCachedIcon && fileName.Equals("scrcpy.png", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(bundleBridgeDir, fileName))) continue;
                string destBridgeFile = Path.Combine(bundleBridgeDir, fileName);
                if (!Path.GetFullPath(bridgeFile).Equals(Path.GetFullPath(destBridgeFile), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(bridgeFile, destBridgeFile, overwrite: true);
                }
            }
        }

        // 4. Generate bespoke app.ico and bridge/scrcpy.png icons
        AppIconGenerator.GenerateBundleIcons(meta.PackageName, appSlug, bundleDir, apkPath, allowOnlineIcons, log);

        // 5. Build bespoke config.json for the ingested APK
        var config = new LaForgeConfig
        {
            App = new AppConfig
            {
                Title = $"LaForge - {appSlug} (v{meta.VersionName})",
                ExecutableName = bespokeExeName,
                IconPath = "./app.ico",
                ApkPath = "./payload.apk",
                PackageName = meta.PackageName,
                ActivityName = meta.LauncherActivity
            },
            Runtime = new RuntimeConfig
            {
                EmulatorPath = "emulator",
                AdbPath = "adb",
                AvdName = "LaForge_x86_64_API34",
                Snapshot = "default_boot",
                GpuMode = "host",
                Whpx = true,
                Headless = true,
                BootTimeoutSec = 60
            },
            Display = new DisplayConfig
            {
                ScrcpyPath = "bridge/scrcpy.exe",
                RenderDriver = "direct3d",
                Window = new WindowConfig
                {
                    X = 140,
                    Y = 140,
                    Width = AppPolicyConfig.TryParseDimensions(meta.PreferredScreenSize, out int pw, out int ph) ? pw : 1280,
                    Height = AppPolicyConfig.TryParseDimensions(meta.PreferredScreenSize, out _, out int ph2) ? ph2 : 720,
                    Borderless = false,
                    AlwaysOnTop = false,
                    Fullscreen = false
                },
                Vsync = true,
                MaxFps = 60,
                StayAwake = true,
                AutoHideConsole = true
            },
            Storage = new StorageConfig
            {
                EnableHostMount = enableHostMount,
                HostFolderPath = @"%LOCALAPPDATA%\LaForge\HOST",
                GuestMountPath = "/sdcard/HOST",
                CreateShortcut = true
            },
            Relays = new RelaysConfig
            {
                RemoteStagingDir = "/sdcard/Download",
                Associations = DeriveRelayAssociations(meta)
            }
        };

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        string configJson = JsonSerializer.Serialize(config, jsonOpts);
        File.WriteAllText(Path.Combine(bundleDir, "config.json"), configJson);

        // 6. Ensure dedicated %LOCALAPPDATA%\LaForge\HOST directory exists and drop HOST.lnk next to launcher.exe
        HostFolderBridge.EnsureHostFolderAndShortcut(config.Storage, bundleDir);

        // 7. Register app in %LOCALAPPDATA%\LaForge\config\<package>.conf for LaForge Central Console
        LaForgeAppRegistry.LoadOrCreate(config, bundleDir);

        return bundleDir;
    }

    public static string DeriveExecutableName(string appSlug)
    {
        string clean = new string(appSlug.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "LaForgeApp.exe" : $"{clean}.exe";
    }

    private static string DeriveAppSlug(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return "AndroidApp";
        string[] parts = packageName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return string.Join(' ', parts.Skip(1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        }
        return packageName;
    }

    private static Dictionary<string, RelayRule> DeriveRelayAssociations(ApkMetadata meta)
    {
        var map = new Dictionary<string, RelayRule>(StringComparer.OrdinalIgnoreCase);

        bool hasImage = meta.MimeTypes.Any(m => m.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
        bool hasVideo = meta.MimeTypes.Any(m => m.StartsWith("video/", StringComparison.OrdinalIgnoreCase));
        bool hasAudio = meta.MimeTypes.Any(m => m.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                                                m.Equals("application/ogg", StringComparison.OrdinalIgnoreCase));
        bool hasArchiveOrAny = meta.MimeTypes.Any(m => m.Equals("*/*", StringComparison.OrdinalIgnoreCase) ||
                                                       m.Contains("zip", StringComparison.OrdinalIgnoreCase) ||
                                                       m.StartsWith("text/", StringComparison.OrdinalIgnoreCase));

        if (hasImage || meta.PackageName.Contains("gallery", StringComparison.OrdinalIgnoreCase))
        {
            map[".png"] = new RelayRule { MimeType = "image/png", Action = "android.intent.action.VIEW" };
            map[".jpg"] = new RelayRule { MimeType = "image/jpeg", Action = "android.intent.action.VIEW" };
            map[".jpeg"] = new RelayRule { MimeType = "image/jpeg", Action = "android.intent.action.VIEW" };
            map[".webp"] = new RelayRule { MimeType = "image/webp", Action = "android.intent.action.VIEW" };
        }

        if (hasVideo || meta.PackageName.Contains("gallery", StringComparison.OrdinalIgnoreCase))
        {
            map[".mp4"] = new RelayRule { MimeType = "video/mp4", Action = "android.intent.action.VIEW" };
            map[".mkv"] = new RelayRule { MimeType = "video/x-matroska", Action = "android.intent.action.VIEW" };
        }

        if (hasAudio ||
            meta.PackageName.Contains("music", StringComparison.OrdinalIgnoreCase) ||
            meta.PackageName.Contains("voice", StringComparison.OrdinalIgnoreCase) ||
            meta.PackageName.Contains("recorder", StringComparison.OrdinalIgnoreCase))
        {
            map[".mp3"] = new RelayRule { MimeType = "audio/mpeg", Action = "android.intent.action.VIEW" };
            map[".wav"] = new RelayRule { MimeType = "audio/wav", Action = "android.intent.action.VIEW" };
            map[".m4a"] = new RelayRule { MimeType = "audio/mp4", Action = "android.intent.action.VIEW" };
            map[".ogg"] = new RelayRule { MimeType = "audio/ogg", Action = "android.intent.action.VIEW" };
            map[".flac"] = new RelayRule { MimeType = "audio/flac", Action = "android.intent.action.VIEW" };
        }

        if (hasArchiveOrAny || meta.PackageName.Contains("filemanager", StringComparison.OrdinalIgnoreCase) || map.Count == 0)
        {
            map[".zip"] = new RelayRule { MimeType = "application/zip", Action = "android.intent.action.VIEW" };
            map[".txt"] = new RelayRule { MimeType = "text/plain", Action = "android.intent.action.VIEW" };
            map[".pdf"] = new RelayRule { MimeType = "application/pdf", Action = "android.intent.action.VIEW" };
        }

        return map;
    }
}
