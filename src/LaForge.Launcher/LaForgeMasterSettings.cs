using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace LaForge.Launcher;

public sealed class LaForgeMasterSettings
{
    public string Latitude { get; set; } = "";
    public string Longitude { get; set; } = "";
    public string RearCamera { get; set; } = "emulated";
    public string FrontCamera { get; set; } = "webcam0";
    public bool GamepadPassthrough { get; set; } = true;

    public static string GetSettingsFilePath()
    {
        return Path.Combine(LaForgeAppRegistry.GetConfigDirectory(), "master_settings.conf");
    }

    public static LaForgeMasterSettings Load()
    {
        string path = GetSettingsFilePath();
        var settings = new LaForgeMasterSettings();
        if (!File.Exists(path))
        {
            Save(settings);
            return settings;
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
                    case "latitude":
                        settings.Latitude = val;
                        break;
                    case "longitude":
                        settings.Longitude = val;
                        break;
                    case "rear_camera":
                    case "rearcamera":
                        if (!string.IsNullOrWhiteSpace(val)) settings.RearCamera = ExtractCameraToken(val);
                        break;
                    case "front_camera":
                    case "frontcamera":
                        if (!string.IsNullOrWhiteSpace(val)) settings.FrontCamera = ExtractCameraToken(val);
                        break;
                    case "gamepad_passthrough":
                    case "gamepadpassthrough":
                        if (bool.TryParse(val, out bool gp)) settings.GamepadPassthrough = gp;
                        break;
                }
            }
        }
        catch
        {
            // Return defaults on read error
        }

        SanitizeDuplicatePhysicalWebcam(settings);
        return settings;
    }

    /// <summary>
    /// QEMU's host webcam service cannot bind the same physical 'webcamN' device handle to both
    /// Rear and Front simultaneously (doing so leaves Front camera unregistered and causes CameraX
    /// CameraValidator to fail with CameraIdListIncorrectException). If both are set to the same
    /// webcamN, keep Front on webcamN and set Rear to 'emulated'.
    /// </summary>
    public static bool SanitizeDuplicatePhysicalWebcam(LaForgeMasterSettings settings)
    {
        string rear = ExtractCameraToken(settings.RearCamera);
        string front = ExtractCameraToken(settings.FrontCamera);
        if (rear.StartsWith("webcam", StringComparison.OrdinalIgnoreCase) &&
            rear.Equals(front, StringComparison.OrdinalIgnoreCase))
        {
            settings.RearCamera = "emulated";
            return true;
        }
        return false;
    }

    public static void Save(LaForgeMasterSettings settings)
    {
        SanitizeDuplicatePhysicalWebcam(settings);
        try
        {
            string path = GetSettingsFilePath();
            var sb = new StringBuilder();
            sb.AppendLine("# LaForge Master Settings (master_settings.conf)");
            sb.AppendLine($"# Updated: {DateTime.UtcNow:O}");
            sb.AppendLine($"latitude={settings.Latitude?.Trim() ?? ""}");
            sb.AppendLine($"longitude={settings.Longitude?.Trim() ?? ""}");
            sb.AppendLine($"rear_camera={ExtractCameraToken(settings.RearCamera)}");
            sb.AppendLine($"front_camera={ExtractCameraToken(settings.FrontCamera)}");
            sb.AppendLine($"gamepad_passthrough={settings.GamepadPassthrough.ToString().ToLowerInvariant()}");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Ignore write errors
        }

        SyncAvdHardwareIni(settings);
    }

    public static void SyncAvdHardwareIni(LaForgeMasterSettings settings, string avdName = "LaForge_x86_64_API34")
    {
        try
        {
            string cfgIni = Path.Combine(RuntimeProvisioner.GetAvdDir(avdName), "config.ini");
            if (!File.Exists(cfgIni)) return;

            string rearCam = ExtractCameraToken(settings.RearCamera);
            string frontCam = ExtractCameraToken(settings.FrontCamera);
            var lines = File.ReadAllLines(cfgIni).ToList();

            void SetIniKey(string key, string value)
            {
                int idx = lines.FindIndex(l => l.TrimStart().StartsWith(key + " ", StringComparison.OrdinalIgnoreCase) ||
                                               l.TrimStart().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    lines[idx] = $"{key} = {value}";
                }
                else
                {
                    lines.Add($"{key} = {value}");
                }
            }

            SetIniKey("hw.gps", "yes");
            SetIniKey("hw.camera.back", rearCam);
            SetIniKey("hw.camera.front", frontCam);
            File.WriteAllLines(cfgIni, lines);
        }
        catch
        {
            // Ignore AVD config.ini sync errors
        }
    }

    public static bool IsSnapshotCameraMatched(
        LaForgeMasterSettings settings,
        string avdName = "LaForge_x86_64_API34",
        string snapshotName = "default_boot")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(snapshotName)) return true;
            string snapHwIni = Path.Combine(RuntimeProvisioner.GetAvdDir(avdName), "snapshots", snapshotName, "hardware.ini");
            if (!File.Exists(snapHwIni)) return false;

            string expectedRear = ExtractCameraToken(settings.RearCamera);
            string expectedFront = ExtractCameraToken(settings.FrontCamera);
            string? actualRear = null;
            string? actualFront = null;

            foreach (string rawLine in File.ReadAllLines(snapHwIni))
            {
                string line = rawLine.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim();
                string val = line[(eq + 1)..].Trim();
                if (key.Equals("hw.camera.back", StringComparison.OrdinalIgnoreCase))
                {
                    actualRear = val;
                }
                else if (key.Equals("hw.camera.front", StringComparison.OrdinalIgnoreCase))
                {
                    actualFront = val;
                }
            }

            return string.Equals(actualRear, expectedRear, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(actualFront, expectedFront, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> ReinitializeEmulatorForCameraChangeAsync(
        string adbExe,
        string emulatorExe,
        LaForgeMasterSettings settings,
        Action<string>? onStatus = null,
        string avdName = "LaForge_x86_64_API34")
    {
        return await Task.Run(() =>
        {
            try
            {
                SanitizeDuplicatePhysicalWebcam(settings);
                Save(settings);

                if (string.IsNullOrWhiteSpace(emulatorExe) || !File.Exists(emulatorExe) ||
                    string.IsNullOrWhiteSpace(adbExe) || !File.Exists(adbExe))
                {
                    onStatus?.Invoke("Saved camera configuration (will apply on next emulator boot).");
                    return true;
                }

                onStatus?.Invoke("Stopping running Android guest to rebind camera HAL…");
                Program.StopHeadlessEmulator(adbExe);
                Thread.Sleep(1200);
                Program.CleanStaleAvdLocks(avdName);

                string rearCam = ExtractCameraToken(settings.RearCamera);
                string frontCam = ExtractCameraToken(settings.FrontCamera);

                onStatus?.Invoke($"Cold-booting Android runtime with Rear={rearCam}, Front={frontCam}…");
                var bootArgs = new List<string>
                {
                    "-avd", avdName,
                    "-no-window",
                    "-gpu", "host",
                    "-writable-system",
                    "-no-snapshot-load",
                    "-no-snapshot-save",
                    "-no-boot-anim",
                    "-allow-host-audio",
                    "-camera-back", rearCam,
                    "-camera-front", frontCam,
                    "-feature", "WindowsHypervisorPlatform"
                };

                var emuEnv = new Dictionary<string, string>
                {
                    ["QEMU_AUDIO_OUT_DRV"] = "none"
                };
                Program.StartBackgroundProcess(emulatorExe, bootArgs, hidden: true, emuEnv);

                DateTime deadline = DateTime.UtcNow.AddSeconds(75);
                bool booted = false;
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(1500);
                    string dev = Program.GetConnectedAdbDevice(adbExe);
                    if (string.IsNullOrEmpty(dev))
                    {
                        onStatus?.Invoke($"Waiting for ADB bridge on {avdName} (Rear={rearCam}, Front={frontCam})…");
                        continue;
                    }

                    string prop = Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "getprop", "sys.boot_completed" }, 3500).Trim();
                    if (prop == "1")
                    {
                        booted = true;
                        break;
                    }
                    onStatus?.Invoke($"Initializing Android Camera2 Provider & guest services…");
                }

                if (!booted)
                {
                    onStatus?.Invoke("Emulator boot timed out while reinitializing camera HAL.");
                    return false;
                }

                onStatus?.Invoke("Configuring guest display, audio bridge & saving default_boot snapshot…");
                Thread.Sleep(1500);
                Program.ConfigureGuestDisplay(adbExe, new WindowConfig());
                Program.RunProcessCaptureOutput(adbExe, new[] { "emu", "avd", "snapshot", "save", "default_boot" }, 25000);

                onStatus?.Invoke($"Camera HAL active & saved to snapshot (Rear={rearCam}, Front={frontCam}).");
                return true;
            }
            catch (Exception ex)
            {
                onStatus?.Invoke($"Camera reinit error: {ex.Message}");
                return false;
            }
        });
    }

    public static string ExtractCameraToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "webcam0";
        string token = raw.Trim().Split(' ')[0].Trim();
        return string.IsNullOrWhiteSpace(token) ? "webcam0" : token;
    }

    public bool TryGetValidCoordinates(out double lat, out double lon)
    {
        lat = 0;
        lon = 0;
        if (string.IsNullOrWhiteSpace(Latitude) || string.IsNullOrWhiteSpace(Longitude))
        {
            return false;
        }

        bool okLat = double.TryParse(Latitude.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lat) ||
                     double.TryParse(Latitude.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out lat);
        bool okLon = double.TryParse(Longitude.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lon) ||
                     double.TryParse(Longitude.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out lon);

        return okLat && okLon && lat >= -90.0 && lat <= 90.0 && lon >= -180.0 && lon <= 180.0;
    }

    /// <summary>
    /// Pushes configured coordinates to the running Android emulator via 'adb emu geo fix &lt;lon&gt; &lt;lat&gt;'.
    /// </summary>
    public static bool ApplyLocationToGuest(string adbExe, LaForgeMasterSettings? settings = null)
    {
        try
        {
            settings ??= Load();
            if (!settings.TryGetValidCoordinates(out double lat, out double lon))
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(adbExe) || !File.Exists(adbExe) ||
                string.IsNullOrEmpty(Program.GetConnectedAdbDevice(adbExe)))
            {
                return false;
            }

            string lonStr = lon.ToString("0.######", CultureInfo.InvariantCulture);
            string latStr = lat.ToString("0.######", CultureInfo.InvariantCulture);

            Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "settings put secure location_mode 3" }, 2500);
            string outText = Program.RunProcessCaptureOutput(adbExe, new[] { "emu", "geo", "fix", lonStr, latStr }, 3500);
            return outText.Contains("OK", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enumerates available host cameras via 'emulator -webcam-list', defaulting to webcam0 first.
    /// </summary>
    public static List<string> QueryAvailableHostCameras(string? emulatorExe)
    {
        var list = new List<string>();
        var seenTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddOption(string token, string description)
        {
            if (seenTokens.Add(token))
            {
                list.Add(string.IsNullOrWhiteSpace(description) ? token : $"{token} ({description})");
            }
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(emulatorExe) && File.Exists(emulatorExe))
            {
                string output = Program.RunProcessCaptureOutput(emulatorExe, new[] { "-webcam-list" }, 4000);
                foreach (string rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = rawLine.Trim();
                    var mLabel = Regex.Match(line, @"Camera\s+'([^']+)'\s+can\s+be\s+specified\s+by\s+(?:label|name)\s+as\s+'(webcam\d+)'", RegexOptions.IgnoreCase);
                    if (mLabel.Success)
                    {
                        AddOption(mLabel.Groups[2].Value.Trim(), mLabel.Groups[1].Value.Trim());
                        continue;
                    }

                    var mLegacy = Regex.Match(line, @"Camera\s+'([^']+)'\s+is\s+connected\s+to\s+device\s+'([^']+)'", RegexOptions.IgnoreCase);
                    if (mLegacy.Success)
                    {
                        AddOption(mLegacy.Groups[1].Value.Trim(), mLegacy.Groups[2].Value.Trim());
                    }
                }
            }
        }
        catch
        {
            // Ignore if emulator -webcam-list fails
        }

        AddOption("webcam0", "Default Host Webcam #0");
        AddOption("webcam1", "Host Webcam #1");
        AddOption("virtualscene", "Android 3D Virtual Scene Camera");
        AddOption("emulated", "Synthetic Test Pattern Camera");
        AddOption("none", "Disabled");

        return list;
    }

    /// <summary>
    /// Queries the host Windows OS Location Service (System.Device.Location.GeoCoordinateWatcher) for a one-time coordinate reading.
    /// </summary>
    public static async Task<(bool Success, string Latitude, string Longitude, string Message)> QueryWindowsHostLocationAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                string psScript =
                    "Add-Type -AssemblyName System.Device; " +
                    "$w = New-Object System.Device.Location.GeoCoordinateWatcher([System.Device.Location.GeoPositionAccuracy]::Default); " +
                    "$w.Start(); " +
                    "for ($i = 0; $i -lt 40; $i++) { " +
                    "  if ($w.Status -eq 'Ready' -and -not $w.Position.Location.IsUnknown) { break } " +
                    "  if ($w.Permission -eq 'Denied') { Write-Output 'DENIED'; $w.Stop(); exit 0 } " +
                    "  Start-Sleep -Milliseconds 200 " +
                    "}; " +
                    "$loc = $w.Position.Location; " +
                    "$w.Stop(); " +
                    "if ($loc -ne $null -and -not $loc.IsUnknown) { " +
                    "  Write-Output ('OK:' + $loc.Latitude.ToString([System.Globalization.CultureInfo]::InvariantCulture) + ',' + $loc.Longitude.ToString([System.Globalization.CultureInfo]::InvariantCulture)) " +
                    "} else { " +
                    "  Write-Output ('STATUS:' + $w.Status + ':' + $w.Permission) " +
                    "}";

                string outText = Program.RunProcessCaptureOutput(
                    "powershell.exe",
                    new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", psScript },
                    12000).Trim();

                foreach (string rawLine in outText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("OK:", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = line[3..].Split(',');
                        if (parts.Length == 2 &&
                            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                        {
                            return (
                                true,
                                lat.ToString("0.000000", CultureInfo.InvariantCulture),
                                lon.ToString("0.000000", CultureInfo.InvariantCulture),
                                "Retrieved coordinates from Windows Location Service."
                            );
                        }
                    }
                    else if (line.Equals("DENIED", StringComparison.OrdinalIgnoreCase))
                    {
                        return (
                            false,
                            "",
                            "",
                            "Windows Location access was denied. Enable 'Location services' and 'Let desktop apps access your location' in Windows Settings -> Privacy & security -> Location."
                        );
                    }
                }

                return (
                    false,
                    "",
                    "",
                    "Windows Location Service returned no position fix (check that Location Services and 'Let desktop apps access your location' are enabled in Windows Settings)."
                );
            }
            catch (Exception ex)
            {
                return (false, "", "", $"Failed to query Windows Location Service: {ex.Message}");
            }
        });
    }
}

public sealed class MasterSettingsDialog : Form
{
    private readonly string _baseDir;
    private readonly string _adbExe;
    private readonly string _emulatorExe;
    private readonly LaForgeMasterSettings _settings;

    private readonly TextBox _txtLat;
    private readonly TextBox _txtLon;
    private readonly Label _lblLocStatus;
    private readonly ComboBox _cmbRearCam;
    private readonly ComboBox _cmbFrontCam;
    private readonly CheckBox _chkGamepad;

    public MasterSettingsDialog(string baseDir, string adbExe, string emulatorExe)
    {
        _baseDir = baseDir;
        _adbExe = adbExe;
        _emulatorExe = emulatorExe;
        _settings = LaForgeMasterSettings.Load();

        Text = "⚙ LaForge Master Settings — Folders, Location, Camera & Controller";
        Size = new Size(640, 565);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(20, 24, 33);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);
        var brandIco = LaForgeSplashOverlay.LoadBrandIcon();
        if (brandIco != null) Icon = brandIco;

        // 1. Quick Folders Section (Moved from main Console header)
        var pnlFolders = CreateSectionPanel(new Rectangle(20, 16, 584, 78));
        pnlFolders.Controls.Add(CreateSectionHeader("📁 LaForge System & Shared Folders", new Point(12, 10)));

        var btnOpenConfig = CreateButton("📁 Open /config (.conf) Folder", Color.FromArgb(52, 60, 78), new Point(14, 36), new Size(265, 32));
        btnOpenConfig.Click += (_, _) => OpenFolder(LaForgeAppRegistry.GetConfigDirectory());

        var btnOpenHost = CreateButton("📂 Open Shared /HOST Folder", Color.FromArgb(35, 95, 145), new Point(295, 36), new Size(273, 32));
        btnOpenHost.Click += (_, _) =>
        {
            string hostPath = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\LaForge\HOST");
            OpenFolder(hostPath);
        };
        pnlFolders.Controls.Add(btnOpenConfig);
        pnlFolders.Controls.Add(btnOpenHost);

        // 2. Geolocation Section
        var pnlLocation = CreateSectionPanel(new Rectangle(20, 106, 584, 152));
        pnlLocation.Controls.Add(CreateSectionHeader("📍 Guest Geolocation Bridge (adb emu geo fix)", new Point(12, 10)));

        var lblLocDesc = new Label
        {
            Text = "Sets the GPS coordinates reported to location-aware Android apps inside the shared container.",
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(165, 180, 200),
            Location = new Point(14, 34),
            AutoSize = true
        };

        var lblLat = new Label { Text = "Latitude:", Location = new Point(14, 66), AutoSize = true, ForeColor = Color.FromArgb(210, 220, 235) };
        _txtLat = CreateTextBox(_settings.Latitude, new Rectangle(82, 62, 118, 26));

        var lblLon = new Label { Text = "Longitude:", Location = new Point(212, 66), AutoSize = true, ForeColor = Color.FromArgb(210, 220, 235) };
        _txtLon = CreateTextBox(_settings.Longitude, new Rectangle(288, 62, 118, 26));

        var btnSaveLoc = CreateButton("💾 Save", Color.FromArgb(35, 134, 54), new Point(416, 60), new Size(72, 30));
        var btnGetPcLoc = CreateButton("📍 Get from PC", Color.FromArgb(45, 95, 155), new Point(494, 60), new Size(76, 30));
        btnGetPcLoc.Font = new Font("Segoe UI Semibold", 8.25F, FontStyle.Bold);

        _lblLocStatus = new Label
        {
            Text = _settings.TryGetValidCoordinates(out double initLat, out double initLon)
                ? $"Saved coordinates: {initLat:0.######}, {initLon:0.######}"
                : "No custom coordinates saved yet.",
            Font = new Font("Consolas", 8.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(140, 195, 255),
            Location = new Point(14, 102),
            Size = new Size(555, 38)
        };

        btnSaveLoc.Click += (_, _) => SaveAndPushLocation(showMessageOnInvalid: true);
        btnGetPcLoc.Click += async (_, _) =>
        {
            btnGetPcLoc.Enabled = false;
            btnSaveLoc.Enabled = false;
            _lblLocStatus.ForeColor = Color.FromArgb(255, 205, 95);
            _lblLocStatus.Text = "Querying Windows Location Service (approve Windows prompt if shown)…";
            try
            {
                var (ok, latStr, lonStr, msg) = await LaForgeMasterSettings.QueryWindowsHostLocationAsync();
                if (ok)
                {
                    _txtLat.Text = latStr;
                    _txtLon.Text = lonStr;
                    SaveAndPushLocation(showMessageOnInvalid: false, prefixMessage: msg);
                }
                else
                {
                    _lblLocStatus.ForeColor = Color.FromArgb(255, 140, 140);
                    _lblLocStatus.Text = msg;
                }
            }
            finally
            {
                btnGetPcLoc.Enabled = true;
                btnSaveLoc.Enabled = true;
            }
        };

        pnlLocation.Controls.AddRange(new Control[] { lblLocDesc, lblLat, _txtLat, lblLon, _txtLon, btnSaveLoc, btnGetPcLoc, _lblLocStatus });

        string initialRearCam = LaForgeMasterSettings.ExtractCameraToken(_settings.RearCamera);
        string initialFrontCam = LaForgeMasterSettings.ExtractCameraToken(_settings.FrontCamera);

        // 3. Camera Passthrough Section
        var pnlCamera = CreateSectionPanel(new Rectangle(20, 270, 584, 134));
        pnlCamera.Controls.Add(CreateSectionHeader("📷 Host Camera Passthrough (-camera-back / -camera-front)", new Point(12, 10)));

        var lblCamDesc = new Label
        {
            Text = "Links host webcams to Android's rear/front cameras (a physical webcam can bind to one side; the other uses Emulated).",
            Font = new Font("Segoe UI", 8.25F, FontStyle.Regular),
            ForeColor = Color.FromArgb(165, 180, 200),
            Location = new Point(14, 34),
            AutoSize = true
        };

        var lblRear = new Label { Text = "Rear Camera:", Location = new Point(14, 62), AutoSize = true, ForeColor = Color.FromArgb(210, 220, 235) };
        _cmbRearCam = CreateComboBox(new Rectangle(112, 58, 340, 26));

        var lblFront = new Label { Text = "Front Camera:", Location = new Point(14, 92), AutoSize = true, ForeColor = Color.FromArgb(210, 220, 235) };
        _cmbFrontCam = CreateComboBox(new Rectangle(112, 88, 340, 26));

        var btnRefreshCams = CreateButton("🔄 Detect Cameras", Color.FromArgb(52, 60, 78), new Point(464, 58), new Size(106, 56));
        btnRefreshCams.Click += (_, _) => PopulateCameraDropdowns();

        bool suppressCamSync = false;
        _cmbRearCam.SelectedIndexChanged += (_, _) =>
        {
            if (suppressCamSync) return;
            string rearTok = LaForgeMasterSettings.ExtractCameraToken(_cmbRearCam.SelectedItem?.ToString());
            string frontTok = LaForgeMasterSettings.ExtractCameraToken(_cmbFrontCam.SelectedItem?.ToString());
            if (rearTok.StartsWith("webcam", StringComparison.OrdinalIgnoreCase) &&
                rearTok.Equals(frontTok, StringComparison.OrdinalIgnoreCase))
            {
                suppressCamSync = true;
                SelectTokenOnCombo(_cmbFrontCam, "emulated");
                suppressCamSync = false;
            }
        };
        _cmbFrontCam.SelectedIndexChanged += (_, _) =>
        {
            if (suppressCamSync) return;
            string rearTok = LaForgeMasterSettings.ExtractCameraToken(_cmbRearCam.SelectedItem?.ToString());
            string frontTok = LaForgeMasterSettings.ExtractCameraToken(_cmbFrontCam.SelectedItem?.ToString());
            if (frontTok.StartsWith("webcam", StringComparison.OrdinalIgnoreCase) &&
                frontTok.Equals(rearTok, StringComparison.OrdinalIgnoreCase))
            {
                suppressCamSync = true;
                SelectTokenOnCombo(_cmbRearCam, "emulated");
                suppressCamSync = false;
            }
        };

        suppressCamSync = true;
        PopulateCameraDropdowns();
        suppressCamSync = false;
        pnlCamera.Controls.AddRange(new Control[] { lblCamDesc, lblRear, _cmbRearCam, lblFront, _cmbFrontCam, btnRefreshCams });

        // 4. Controller / XInput Section
        var pnlGamepad = CreateSectionPanel(new Rectangle(20, 412, 584, 52));
        _chkGamepad = new CheckBox
        {
            Text = "🎮 Enable Host XInput / Gamepad Passthrough (scrcpy --gamepad=uhid)",
            Checked = _settings.GamepadPassthrough,
            Location = new Point(14, 15),
            AutoSize = true,
            ForeColor = Color.FromArgb(126, 231, 135),
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        pnlGamepad.Controls.Add(_chkGamepad);

        // Bottom Buttons
        var btnSaveAll = CreateButton("Save && Close", Color.FromArgb(35, 134, 54), new Point(314, 478), new Size(140, 36));
        var btnCancel = CreateButton("Close", Color.FromArgb(62, 70, 86), new Point(464, 478), new Size(140, 36));

        btnSaveAll.Click += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_txtLat.Text) || !string.IsNullOrWhiteSpace(_txtLon.Text))
            {
                if (!SaveAndPushLocation(showMessageOnInvalid: true))
                {
                    return;
                }
            }
            else
            {
                _settings.Latitude = "";
                _settings.Longitude = "";
            }

            string newRear = LaForgeMasterSettings.ExtractCameraToken(_cmbRearCam.SelectedItem?.ToString());
            string newFront = LaForgeMasterSettings.ExtractCameraToken(_cmbFrontCam.SelectedItem?.ToString());
            _settings.RearCamera = newRear;
            _settings.FrontCamera = newFront;
            _settings.GamepadPassthrough = _chkGamepad.Checked;
            LaForgeMasterSettings.Save(_settings);

            bool cameraChanged = !string.Equals(initialRearCam, _settings.RearCamera, StringComparison.OrdinalIgnoreCase) ||
                                 !string.Equals(initialFrontCam, _settings.FrontCamera, StringComparison.OrdinalIgnoreCase) ||
                                 !LaForgeMasterSettings.IsSnapshotCameraMatched(_settings);

            if (cameraChanged)
            {
                btnSaveAll.Enabled = false;
                btnCancel.Enabled = false;
                _cmbRearCam.Enabled = false;
                _cmbFrontCam.Enabled = false;
                btnRefreshCams.Enabled = false;
                btnSaveAll.Text = "Reinit Camera…";

                await LaForgeMasterSettings.ReinitializeEmulatorForCameraChangeAsync(
                    _adbExe,
                    _emulatorExe,
                    _settings,
                    status =>
                    {
                        if (!IsDisposed)
                        {
                            BeginInvoke(() =>
                            {
                                _lblLocStatus.ForeColor = Color.FromArgb(255, 205, 95);
                                _lblLocStatus.Text = status;
                            });
                        }
                    });
            }

            DialogResult = DialogResult.OK;
            Close();
        };

        btnCancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { pnlFolders, pnlLocation, pnlCamera, pnlGamepad, btnSaveAll, btnCancel });
    }

    private void PopulateCameraDropdowns()
    {
        string rearTarget = LaForgeMasterSettings.ExtractCameraToken(_cmbRearCam?.SelectedItem?.ToString() ?? _settings.RearCamera);
        string frontTarget = LaForgeMasterSettings.ExtractCameraToken(_cmbFrontCam?.SelectedItem?.ToString() ?? _settings.FrontCamera);
        if (rearTarget.StartsWith("webcam", StringComparison.OrdinalIgnoreCase) &&
            rearTarget.Equals(frontTarget, StringComparison.OrdinalIgnoreCase))
        {
            rearTarget = "emulated";
        }

        var options = LaForgeMasterSettings.QueryAvailableHostCameras(_emulatorExe);
        SelectCameraOption(_cmbRearCam!, options, rearTarget);
        SelectCameraOption(_cmbFrontCam!, options, frontTarget);
    }

    private static void SelectTokenOnCombo(ComboBox cmb, string token)
    {
        for (int i = 0; i < cmb.Items.Count; i++)
        {
            if (LaForgeMasterSettings.ExtractCameraToken(cmb.Items[i]?.ToString()).Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                cmb.SelectedIndex = i;
                return;
            }
        }
    }

    private static void SelectCameraOption(ComboBox cmb, List<string> options, string token)
    {
        cmb.Items.Clear();
        int matchIdx = 0;
        for (int i = 0; i < options.Count; i++)
        {
            cmb.Items.Add(options[i]);
            if (LaForgeMasterSettings.ExtractCameraToken(options[i]).Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                matchIdx = i;
            }
        }
        if (cmb.Items.Count > 0)
        {
            cmb.SelectedIndex = matchIdx;
        }
    }

    private bool SaveAndPushLocation(bool showMessageOnInvalid, string? prefixMessage = null)
    {
        _settings.Latitude = _txtLat.Text.Trim();
        _settings.Longitude = _txtLon.Text.Trim();

        if (!_settings.TryGetValidCoordinates(out double lat, out double lon))
        {
            if (showMessageOnInvalid)
            {
                MessageBox.Show(
                    this,
                    "Please enter valid decimal Latitude (-90 to 90) and Longitude (-180 to 180) coordinates.",
                    "Invalid Coordinates",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            _lblLocStatus.ForeColor = Color.FromArgb(255, 140, 140);
            _lblLocStatus.Text = "Invalid coordinates. Latitude must be [-90..90] and Longitude [-180..180].";
            return false;
        }

        _settings.Latitude = lat.ToString("0.######", CultureInfo.InvariantCulture);
        _settings.Longitude = lon.ToString("0.######", CultureInfo.InvariantCulture);
        _settings.RearCamera = LaForgeMasterSettings.ExtractCameraToken(_cmbRearCam.SelectedItem?.ToString());
        _settings.FrontCamera = LaForgeMasterSettings.ExtractCameraToken(_cmbFrontCam.SelectedItem?.ToString());
        _settings.GamepadPassthrough = _chkGamepad.Checked;
        LaForgeMasterSettings.Save(_settings);

        bool pushed = LaForgeMasterSettings.ApplyLocationToGuest(_adbExe, _settings);
        _lblLocStatus.ForeColor = Color.FromArgb(126, 231, 135);
        string statusSuffix = pushed
            ? $"Saved & applied live to running emulator ({_settings.Latitude}, {_settings.Longitude})."
            : $"Saved ({_settings.Latitude}, {_settings.Longitude}) — will apply automatically when emulator boots.";
        _lblLocStatus.Text = string.IsNullOrWhiteSpace(prefixMessage) ? statusSuffix : $"{prefixMessage} {statusSuffix}";
        return true;
    }

    private static Panel CreateSectionPanel(Rectangle bounds)
    {
        return new Panel
        {
            Location = bounds.Location,
            Size = bounds.Size,
            BackColor = Color.FromArgb(28, 34, 46),
            BorderStyle = BorderStyle.FixedSingle
        };
    }

    private static Label CreateSectionHeader(string text, Point location)
    {
        return new Label
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(121, 192, 255),
            Location = location,
            AutoSize = true
        };
    }

    private static TextBox CreateTextBox(string text, Rectangle bounds)
    {
        return new TextBox
        {
            Text = text,
            Location = bounds.Location,
            Size = bounds.Size,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(255, 225, 130),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9.5F, FontStyle.Bold)
        };
    }

    private static ComboBox CreateComboBox(Rectangle bounds)
    {
        return new ComboBox
        {
            Location = bounds.Location,
            Size = bounds.Size,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular)
        };
    }

    private static Button CreateButton(string text, Color backColor, Point location, Size size)
    {
        var btn = new Button
        {
            Text = text,
            Location = location,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }
}
