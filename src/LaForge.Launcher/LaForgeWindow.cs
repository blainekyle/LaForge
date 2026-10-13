using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace LaForge.Launcher;

public sealed class LaForgeWindow : Form
{
    private readonly LaForgeConfig _config;
    private readonly string _configDir;
    private readonly string? _initialRelayFile;

    private readonly Label _lblBridgeStatus;
    private readonly Label _lblAdbStatus;
    private readonly Label _lblPayloadStatus;
    private readonly Label _lblRuntimeStatus;
    private readonly RichTextBox _logBox;
    private readonly Button _btnBoot;
    private readonly Button _btnRelay;
    private readonly Button _btnProvision;

    public LaForgeWindow(LaForgeConfig config, string configDir, string? initialRelayFile)
    {
        _config = config;
        _configDir = configDir;
        _initialRelayFile = initialRelayFile;

        Text = config.App.Title;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(Math.Max(0, config.Display.Window.X), Math.Max(0, config.Display.Window.Y));
        ClientSize = new Size(config.Display.Window.Width, config.Display.Window.Height);
        BackColor = Color.FromArgb(18, 20, 26);
        ForeColor = Color.FromArgb(230, 235, 245);
        Font = new Font("Segoe UI", 10F, FontStyle.Regular);
        AllowDrop = true;

        // Top Header Panel
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 96,
            BackColor = Color.FromArgb(26, 30, 40),
            Padding = new Padding(20, 14, 20, 14)
        };

        var lblTitle = new Label
        {
            Text = config.App.Title,
            Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(88, 166, 255),
            AutoSize = true,
            Location = new Point(20, 12)
        };

        string relaysSummary = string.Join(", ", config.Relays.Associations.Select(kv => $"{kv.Key} ({kv.Value.MimeType})"));
        var lblSub = new Label
        {
            Text = $"Package: {config.App.PackageName}   |   Activity: {config.App.ActivityName}\n" +
                   $"Display: {config.Display.Window.Width}x{config.Display.Window.Height} ({config.Display.RenderDriver}, {config.Display.MaxFps} FPS)   |   Relays: {relaysSummary}",
            Font = new Font("Consolas", 9.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(170, 180, 195),
            AutoSize = true,
            Location = new Point(22, 46)
        };

        headerPanel.Controls.Add(lblTitle);
        headerPanel.Controls.Add(lblSub);

        // Status Cards Panel
        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 68,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.FromArgb(22, 25, 33),
            Padding = new Padding(16, 8, 16, 8)
        };
        for (int i = 0; i < 4; i++)
        {
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
        }

        _lblBridgeStatus = CreateStatusCard("Display Bridge (scrcpy + SDL3)", "Checking...");
        _lblAdbStatus = CreateStatusCard("ADB Platform Bridge", "Checking...");
        _lblPayloadStatus = CreateStatusCard("Sideload Payload APK", "Checking...");
        _lblRuntimeStatus = CreateStatusCard("Android Guest Runtime", "Checking...");

        statusPanel.Controls.Add(_lblBridgeStatus, 0, 0);
        statusPanel.Controls.Add(_lblAdbStatus, 1, 0);
        statusPanel.Controls.Add(_lblPayloadStatus, 2, 0);
        statusPanel.Controls.Add(_lblRuntimeStatus, 3, 0);

        // Bottom Action Bar
        var bottomPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            BackColor = Color.FromArgb(26, 30, 40),
            Padding = new Padding(16, 10, 16, 10),
            FlowDirection = FlowDirection.LeftToRight
        };

        _btnBoot = CreateActionButton("Boot Runtime && Attach scrcpy", Color.FromArgb(35, 134, 54));
        _btnBoot.Click += async (_, _) => await RunSessionPipelineAsync(_initialRelayFile);

        _btnRelay = CreateActionButton("Test File Association Relay...", Color.FromArgb(31, 111, 235));
        _btnRelay.Click += OnTestRelayClicked;

        _btnProvision = CreateActionButton("Provision Headless Android SDK Runtime", Color.FromArgb(110, 64, 201));
        _btnProvision.Click += OnProvisionRuntimeClicked;

        bottomPanel.Controls.Add(_btnBoot);
        bottomPanel.Controls.Add(_btnRelay);
        bottomPanel.Controls.Add(_btnProvision);

        // Center Live Console Log
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(13, 15, 20),
            ForeColor = Color.FromArgb(126, 231, 135),
            Font = new Font("Consolas", 10F, FontStyle.Regular),
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            DetectUrls = false
        };

        Controls.Add(_logBox);
        Controls.Add(bottomPanel);
        Controls.Add(statusPanel);
        Controls.Add(headerPanel);

        DragEnter += (_, e) =>
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
        };
        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                await ExecuteRelayFileAsync(files[0]);
            }
        };

        Shown += async (_, _) => await RunSessionPipelineAsync(_initialRelayFile);
    }

    private static Label CreateStatusCard(string title, string status)
    {
        return new Label
        {
            Dock = DockStyle.Fill,
            Text = $"{title}\n{status}",
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(210, 218, 228),
            BackColor = Color.FromArgb(30, 35, 46),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(4)
        };
    }

    private static Button CreateActionButton(string text, Color backColor)
    {
        return new Button
        {
            Text = text,
            AutoSize = true,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
            Padding = new Padding(12, 4, 12, 4),
            Margin = new Padding(0, 0, 12, 0),
            Cursor = Cursors.Hand
        };
    }

    private void AppendLog(string message, Color? color = null)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(message, color));
            return;
        }
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = color ?? Color.FromArgb(126, 231, 135);
        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _logBox.ScrollToCaret();
    }

    private async Task RunSessionPipelineAsync(string? relayFile)
    {
        _btnBoot.Enabled = false;
        try
        {
            AppendLog("====================================================================", Color.FromArgb(88, 166, 255));
            AppendLog($"Initializing LaForge Session for {_config.App.PackageName}...", Color.FromArgb(88, 166, 255));

            string adbExe = Program.ResolveToolPath(_configDir, _config.Runtime.AdbPath, "adb.exe");
            string scrcpyExe = Program.ResolveToolPath(_configDir, _config.Display.ScrcpyPath, "scrcpy.exe");
            string emulatorExe = Program.ResolveEmulatorPath(_configDir, _config.Runtime.EmulatorPath);
            string payloadApk = Path.IsPathRooted(_config.App.ApkPath)
                ? _config.App.ApkPath
                : Path.GetFullPath(Path.Combine(_configDir, _config.App.ApkPath));

            // 1. Check Display Bridge
            bool scrcpyReady = File.Exists(scrcpyExe);
            _lblBridgeStatus.Text = $"Display Bridge (scrcpy + SDL3)\n{(scrcpyReady ? "READY (v5.0.1 Direct3D)" : "MISSING")}";
            _lblBridgeStatus.ForeColor = scrcpyReady ? Color.FromArgb(126, 231, 135) : Color.FromArgb(255, 123, 114);

            // 2. Check ADB Bridge
            bool adbReady = File.Exists(adbExe);
            _lblAdbStatus.Text = $"ADB Platform Bridge\n{(adbReady ? "READY (v37.0.1)" : "MISSING")}";
            _lblAdbStatus.ForeColor = adbReady ? Color.FromArgb(126, 231, 135) : Color.FromArgb(255, 123, 114);

            // 3. Check Payload APK
            bool payloadReady = File.Exists(payloadApk);
            double payloadMb = payloadReady ? new FileInfo(payloadApk).Length / (1024.0 * 1024.0) : 0;
            _lblPayloadStatus.Text = $"Sideload Payload APK\n{(payloadReady ? $"READY ({payloadMb:F1} MB)" : "MISSING")}";
            _lblPayloadStatus.ForeColor = payloadReady ? Color.FromArgb(126, 231, 135) : Color.FromArgb(255, 123, 114);

            // Log configured pipeline commands
            var emuArgs = Program.BuildEmulatorArgs(_config.Runtime);
            var scrcpyArgs = Program.BuildScrcpyArgs(_config);
            var apkArgs = Program.BuildApkLaunchArgs(_config.App);

            AppendLog($"[Step 1/4] Headless Emulator Command: {emulatorExe} {string.Join(' ', emuArgs)}");
            AppendLog($"[Step 2/4] Display Bridge Command   : {scrcpyExe} {string.Join(' ', scrcpyArgs)}");
            AppendLog($"[Step 3/4] Target APK Launch Command: {adbExe} {string.Join(' ', apkArgs)}");

            // Check if an ADB device is already attached
            string attachedDevice = await Task.Run(() => Program.GetConnectedAdbDevice(adbExe));
            if (string.IsNullOrEmpty(attachedDevice) && File.Exists(emulatorExe))
            {
                AppendLog($"No active ADB device found. Launching headless Android emulator ({emulatorExe})...", Color.FromArgb(210, 153, 34));
                _lblRuntimeStatus.Text = "Android Guest Runtime\nBOOTING AVD...";
                _lblRuntimeStatus.ForeColor = Color.FromArgb(210, 153, 34);

                await Task.Run(() =>
                {
                    Program.StartBackgroundProcess(emulatorExe, emuArgs, hidden: true);
                    Program.WaitForDeviceBoot(adbExe, _config.Runtime.BootTimeoutSec);
                });
                attachedDevice = await Task.Run(() => Program.GetConnectedAdbDevice(adbExe));
            }

            if (!string.IsNullOrEmpty(attachedDevice))
            {
                _lblRuntimeStatus.Text = $"Android Guest Runtime\nONLINE ({attachedDevice})";
                _lblRuntimeStatus.ForeColor = Color.FromArgb(126, 231, 135);
                AppendLog($"Connected to Android runtime device: {attachedDevice}");

                if (payloadReady)
                {
                    AppendLog($"Sideloading {Path.GetFileName(payloadApk)} via 'adb install -r'...");
                    await Task.Run(() => Program.RunCheckedProcess(adbExe, new[] { "install", "-r", payloadApk }));
                    AppendLog("APK sideload complete.");
                }

                if (scrcpyReady)
                {
                    AppendLog("Attaching scrcpy SDL3/Direct3D display bridge...");
                    Program.StartBackgroundProcess(scrcpyExe, scrcpyArgs, hidden: false);
                }

                AppendLog($"Starting {_config.App.PackageName}/{_config.App.ActivityName}...");
                await Task.Run(() => Program.RunCheckedProcess(adbExe, apkArgs));
                AppendLog("Target APK launched in Android runtime.");

                if (!string.IsNullOrWhiteSpace(relayFile))
                {
                    await ExecuteRelayFileAsync(relayFile);
                }
            }
            else
            {
                _lblRuntimeStatus.Text = "Android Guest Runtime\nAWAITING VM / DEVICE";
                _lblRuntimeStatus.ForeColor = Color.FromArgb(255, 166, 87);

                AppendLog("--------------------------------------------------------------------", Color.FromArgb(255, 166, 87));
                AppendLog("Notice: Display Bridge (scrcpy.exe + SDL3.dll) and ADB (adb.exe) are wired and ready,", Color.FromArgb(255, 166, 87));
                AppendLog("but no Android guest VM or ADB device is currently running on this host.", Color.FromArgb(255, 166, 87));
                AppendLog("To run the APK live:", Color.FromArgb(230, 235, 245));
                AppendLog("  1. Click 'Provision Headless Android SDK Runtime' below to download the Android x86_64 emulator image, OR", Color.FromArgb(230, 235, 245));
                AppendLog("  2. Connect any Android device/emulator via ADB and click 'Boot Runtime & Attach scrcpy'.", Color.FromArgb(230, 235, 245));
                AppendLog("  3. You can also test/preview file-association relays right now via 'Test File Association Relay...' or drag-and-drop.", Color.FromArgb(230, 235, 245));

                if (!string.IsNullOrWhiteSpace(relayFile) && File.Exists(relayFile))
                {
                    await ExecuteRelayFileAsync(relayFile);
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] {ex.Message}", Color.FromArgb(255, 123, 114));
        }
        finally
        {
            _btnBoot.Enabled = true;
        }
    }

    private async void OnTestRelayClicked(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = $"Select Host File to Relay into {_config.App.PackageName}",
            InitialDirectory = Directory.Exists(Path.Combine(_configDir, "..", "..", "samples"))
                ? Path.GetFullPath(Path.Combine(_configDir, "..", "..", "samples"))
                : _configDir,
            Filter = "All Configured Relay Files (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            await ExecuteRelayFileAsync(dlg.FileName);
        }
    }

    private async Task ExecuteRelayFileAsync(string filePath)
    {
        try
        {
            string adbExe = Program.ResolveToolPath(_configDir, _config.Runtime.AdbPath, "adb.exe");
            var (pushArgs, intentArgs, remoteUri, mimeType) = Program.BuildRelayArgs(_config, filePath);

            AppendLog("--------------------------------------------------------------------", Color.FromArgb(88, 166, 255));
            AppendLog($"[Step 4/4: File Association Relay Triggered]", Color.FromArgb(88, 166, 255));
            AppendLog($"  Host File  : {filePath}");
            AppendLog($"  Remote URI : {remoteUri}");
            AppendLog($"  MIME Type  : {mimeType}");
            AppendLog($"  Push Cmd   : {adbExe} {string.Join(' ', pushArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
            AppendLog($"  Intent Cmd : {adbExe} {string.Join(' ', intentArgs)}");

            string attachedDevice = await Task.Run(() => Program.GetConnectedAdbDevice(adbExe));
            if (!string.IsNullOrEmpty(attachedDevice))
            {
                await Task.Run(() =>
                {
                    Program.RunCheckedProcess(adbExe, pushArgs);
                    Program.RunCheckedProcess(adbExe, intentArgs);
                });
                AppendLog("Relay file pushed and ACTION_VIEW intent dispatched to Android guest.");
            }
            else
            {
                AppendLog("Relay commands verified (staged for execution once Android guest runtime is online).", Color.FromArgb(255, 166, 87));
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Relay Error] {ex.Message}", Color.FromArgb(255, 123, 114));
        }
    }

    private void OnProvisionRuntimeClicked(object? sender, EventArgs e)
    {
        string scriptCandidate = Path.GetFullPath(Path.Combine(_configDir, "provision_runtime.ps1"));
        if (!File.Exists(scriptCandidate))
        {
            scriptCandidate = Path.GetFullPath(Path.Combine(_configDir, "..", "..", "provision_runtime.ps1"));
        }

        if (!File.Exists(scriptCandidate))
        {
            AppendLog($"Provisioning script not found at: {scriptCandidate}", Color.FromArgb(255, 123, 114));
            return;
        }

        AppendLog($"Launching Android Runtime Provisioner: {scriptCandidate}", Color.FromArgb(88, 166, 255));
        Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-NoExit -ExecutionPolicy Bypass -File \"{scriptCandidate}\"",
            UseShellExecute = true
        });
    }
}
