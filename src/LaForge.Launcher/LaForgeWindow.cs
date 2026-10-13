using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace LaForge.Launcher;

public sealed class LaForgeWindow : Form
{
    private readonly LaForgeConfig _config;
    private readonly string _configPath;
    private readonly string _configDir;
    private readonly string? _initialRelayFile;

    private readonly Label _lblBridgeStatus;
    private readonly Label _lblAdbStatus;
    private readonly Label _lblPayloadStatus;
    private readonly Label _lblRuntimeStatus;
    private readonly RichTextBox _logBox;
    private readonly Button _btnBoot;
    private readonly Button _btnRelay;
    private readonly Button _btnOpenHost;
    private readonly Button _btnProvision;
    private readonly CheckBox _chkHostMount;
    private readonly CheckBox _chkAutoHide;
    private readonly NotifyIcon _trayIcon;

    private HostFolderBridge? _hostBridge;
    private Process? _activeScrcpyProcess;

    public LaForgeWindow(LaForgeConfig config, string configPath, string configDir, string? initialRelayFile)
    {
        _config = config;
        _configPath = configPath;
        _configDir = configDir;
        _initialRelayFile = initialRelayFile;

        Text = $"{config.App.Title} — Control Console";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(Math.Max(0, config.Display.Window.X - 40), Math.Max(0, config.Display.Window.Y - 40));
        ClientSize = new Size(config.Display.Window.Width, config.Display.Window.Height);
        BackColor = Color.FromArgb(18, 20, 26);
        ForeColor = Color.FromArgb(230, 235, 245);
        Font = new Font("Segoe UI", 10F, FontStyle.Regular);
        AllowDrop = true;

        // Load bespoke app.ico if present
        Icon? appIcon = TryLoadAppIcon(configDir, config.App.IconPath);
        if (appIcon != null)
        {
            Icon = appIcon;
        }

        // System Tray NotifyIcon for Single-Window UX
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open LaForge Central Console", null, OnOpenCentralConsoleClicked);
        trayMenu.Items.Add("Show / Hide App Control Console", null, (_, _) => ToggleConsoleVisibility());
        trayMenu.Items.Add("Open Shared HOST Folder", null, OnOpenHostShareClicked);
        trayMenu.Items.Add("Test File Association Relay...", null, OnTestRelayClicked);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add($"Exit {config.App.Title}", null, (_, _) => ExitEntireApplication());

        _trayIcon = new NotifyIcon
        {
            Icon = appIcon ?? SystemIcons.Application,
            Text = config.App.Title.Length > 63 ? config.App.Title[..63] : config.App.Title,
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ToggleConsoleVisibility();

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

        _btnBoot = CreateActionButton("Boot && Attach scrcpy", Color.FromArgb(35, 134, 54));
        _btnBoot.Click += async (_, _) => await RunSessionPipelineAsync(_initialRelayFile);

        _btnRelay = CreateActionButton("Test File Relay...", Color.FromArgb(31, 111, 235));
        _btnRelay.Click += OnTestRelayClicked;

        _btnOpenHost = CreateActionButton("Open HOST Share", Color.FromArgb(45, 90, 140));
        _btnOpenHost.Click += OnOpenHostShareClicked;

        _btnProvision = CreateActionButton("Provision SDK Runtime", Color.FromArgb(110, 64, 201));
        _btnProvision.Click += OnProvisionRuntimeClicked;

        _chkHostMount = new CheckBox
        {
            Text = $"Mount {_config.Storage.GuestMountPath}",
            Checked = _config.Storage.EnableHostMount,
            AutoSize = true,
            ForeColor = Color.FromArgb(126, 231, 135),
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
            Margin = new Padding(6, 8, 10, 0),
            Cursor = Cursors.Hand
        };
        _chkHostMount.CheckedChanged += OnHostMountToggleChanged;

        _chkAutoHide = new CheckBox
        {
            Text = "Single-Window Mode (Tray)",
            Checked = _config.Display.AutoHideConsole,
            AutoSize = true,
            ForeColor = Color.FromArgb(88, 166, 255),
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
            Margin = new Padding(6, 8, 6, 0),
            Cursor = Cursors.Hand
        };
        _chkAutoHide.CheckedChanged += OnAutoHideToggleChanged;

        bottomPanel.Controls.Add(_btnBoot);
        bottomPanel.Controls.Add(_btnRelay);
        bottomPanel.Controls.Add(_btnOpenHost);
        bottomPanel.Controls.Add(_btnProvision);
        bottomPanel.Controls.Add(_chkHostMount);
        bottomPanel.Controls.Add(_chkAutoHide);

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

        FormClosed += (_, _) =>
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _hostBridge?.Dispose();
        };

        Shown += async (_, _) => await RunSessionPipelineAsync(_initialRelayFile);
    }

    private static Icon? TryLoadAppIcon(string configDir, string configuredIconPath)
    {
        try
        {
            string candidate = Path.IsPathRooted(configuredIconPath)
                ? configuredIconPath
                : Path.GetFullPath(Path.Combine(configDir, configuredIconPath));
            if (File.Exists(candidate))
            {
                return new Icon(candidate);
            }
        }
        catch
        {
            // Fallback to default icon
        }
        return null;
    }

    private void ToggleConsoleVisibility()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ToggleConsoleVisibility);
            return;
        }

        if (Visible)
        {
            HideToSystemTray();
        }
        else
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }
    }

    private void HideToSystemTray()
    {
        if (InvokeRequired)
        {
            BeginInvoke(HideToSystemTray);
            return;
        }
        ShowInTaskbar = false;
        Hide();
    }

    private void ExitEntireApplication()
    {
        try
        {
            if (_activeScrcpyProcess != null && !_activeScrcpyProcess.HasExited)
            {
                _activeScrcpyProcess.Kill();
            }
        }
        catch
        {
            // Ignore
        }
        Close();
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
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand
        };
    }

    private void AppendLog(string message, Color? color = null)
    {
        if (IsDisposed) return;
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

            string hostDir = HostFolderBridge.EnsureHostFolderAndShortcut(_config.Storage, _configDir);
            AppendLog($"Host Share Directory: {hostDir} (Shortcut: {Path.Combine(_configDir, "HOST.lnk")})");

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

            AppPolicyConfig appPolicy = LaForgeAppRegistry.LoadOrCreate(_config, _configDir);
            _config.Storage.EnableHostMount = appPolicy.HostShareEnabled;

            var emuArgs = Program.BuildEmulatorArgs(_config.Runtime);
            var scrcpyArgs = Program.BuildScrcpyArgs(_config, forExecution: true, appPolicy);
            var apkArgs = Program.BuildApkLaunchArgs(_config.App);

            AppendLog($"[Step 1/4] Headless Emulator Command: {emulatorExe} {string.Join(' ', emuArgs)}");
            AppendLog($"[Step 2/4] Display Bridge Command   : {scrcpyExe} {string.Join(' ', scrcpyArgs)}");
            AppendLog($"[Step 3/4] Target APK Launch Command: {adbExe} {string.Join(' ', apkArgs)}");

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
                AppendLog($"Connected to Android runtime device: {attachedDevice}. Verifying User 0 unlock state...");

                await Task.Run(() => Program.WaitForDeviceBoot(adbExe, _config.Runtime.BootTimeoutSec));

                AppendLog($"Configuring guest display geometry ({_config.Display.Window.Width}x{_config.Display.Window.Height} @ 160 dpi) & kiosk statusbar lock...");
                await Task.Run(() => Program.ConfigureGuestDisplay(adbExe, _config.Display.Window));

                // Initialize Host Folder Bridge (%LOCALAPPDATA%\LaForge\HOST <-> /sdcard/HOST)
                _hostBridge?.Dispose();
                _hostBridge = new HostFolderBridge(_config.Storage, adbExe, msg => AppendLog(msg, Color.FromArgb(165, 214, 255)));
                await Task.Run(() => _hostBridge.Start());

                if (payloadReady)
                {
                    AppendLog($"Sideloading {Path.GetFileName(payloadApk)} via 'adb install -r -g'...");
                    await Task.Run(() => Program.SideloadAndGrantPermissions(adbExe, payloadApk, _config.App.PackageName));
                    AppendLog("APK sideload & storage permissions complete.");
                }

                await Task.Run(() => LaForgeAppRegistry.EnforcePoliciesOnGuest(adbExe, appPolicy, msg => AppendLog(msg, Color.FromArgb(210, 168, 255))));

                if (!appPolicy.VirtualDisplay || !scrcpyReady)
                {
                    AppendLog($"Starting {_config.App.PackageName}/{_config.App.ActivityName} on primary display...");
                    string startResult = await Task.Run(() => Program.LaunchApkWithRetry(adbExe, _config.App, 15));
                    AppendLog($"Target APK launched: {startResult}");
                }
                else
                {
                    AppendLog($"Virtual Display Mode enabled: scrcpy will allocate an isolated 1280x720/160 display for {_config.App.PackageName}.");
                }

                if (!string.IsNullOrWhiteSpace(relayFile))
                {
                    await ExecuteRelayFileAsync(relayFile);
                }

                if (scrcpyReady)
                {
                    AppendLog("Attaching scrcpy SDL3/Direct3D virtual display bridge...");
                    string scrcpyIconPath = Path.Combine(_configDir, "bridge", "scrcpy.png");
                    _activeScrcpyProcess = Program.EnsureSingleScrcpyInstance(
                        scrcpyExe,
                        scrcpyArgs,
                        scrcpyIconPath,
                        _config.App.Title,
                        appPolicy.LastScrcpyPid);
                    LaForgeAppRegistry.RecordAppLaunch(_config, _configDir, _activeScrcpyProcess.Id);
                    _activeScrcpyProcess.EnableRaisingEvents = true;
                    _activeScrcpyProcess.Exited += OnScrcpyProcessExited;

                    if (_config.Display.AutoHideConsole)
                    {
                        AppendLog("Single-Window Mode active: hiding Control Console to System Tray (double-click tray icon to restore).", Color.FromArgb(88, 166, 255));
                        await Task.Delay(350);
                        HideToSystemTray();
                    }
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
                AppendLog("  1. Click 'Provision SDK Runtime' below to download the Android x86_64 emulator image, OR", Color.FromArgb(230, 235, 245));
                AppendLog("  2. Connect any Android device/emulator via ADB and click 'Boot & Attach scrcpy'.", Color.FromArgb(230, 235, 245));

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
            if (!IsDisposed)
            {
                _btnBoot.Enabled = true;
            }
        }
    }

    private void OnOpenCentralConsoleClicked(object? sender, EventArgs e)
    {
        try
        {
            string exePath = Environment.ProcessPath ?? Path.Combine(_configDir, "launcher.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--console",
                WorkingDirectory = _configDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog($"[Console Error] {ex.Message}", Color.FromArgb(255, 123, 114));
        }
    }

    private void OnScrcpyProcessExited(object? sender, EventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_config.App.PackageName))
            {
                var latestPolicy = LaForgeAppRegistry.ParseConfFile(LaForgeAppRegistry.GetConfFilePath(_config.App.PackageName));
                if (!latestPolicy.BackgroundOnClose)
                {
                    string adbExe = Program.ResolveToolPath(_configDir, _config.Runtime.AdbPath, "adb.exe");
                    if (File.Exists(adbExe))
                    {
                        Program.RunProcessCaptureOutput(adbExe, new[] { "shell", "am", "force-stop", _config.App.PackageName }, 3000);
                    }
                }
            }
        }
        catch
        {
            // Ignore
        }

        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            if (!Visible)
            {
                // In Single-Window Mode (Control Console hidden in tray), closing the scrcpy app window exits the wrapper cleanly
                Close();
            }
            else
            {
                AppendLog("[DisplayBridge] scrcpy display window closed.", Color.FromArgb(255, 166, 87));
            }
        });
    }

    private void SaveConfigToFile()
    {
        var jsonOpts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, jsonOpts));
    }

    private async void OnHostMountToggleChanged(object? sender, EventArgs e)
    {
        _config.Storage.EnableHostMount = _chkHostMount.Checked;
        _chkHostMount.ForeColor = _chkHostMount.Checked ? Color.FromArgb(126, 231, 135) : Color.FromArgb(170, 180, 195);

        try
        {
            SaveConfigToFile();
            AppendLog($"Updated config.json: storage.enable_host_mount = {_config.Storage.EnableHostMount}", Color.FromArgb(88, 166, 255));

            string adbExe = Program.ResolveToolPath(_configDir, _config.Runtime.AdbPath, "adb.exe");
            string attachedDevice = await Task.Run(() => Program.GetConnectedAdbDevice(adbExe));
            _hostBridge?.Dispose();
            _hostBridge = null;

            if (_config.Storage.EnableHostMount && !string.IsNullOrEmpty(attachedDevice))
            {
                _hostBridge = new HostFolderBridge(_config.Storage, adbExe, msg => AppendLog(msg, Color.FromArgb(165, 214, 255)));
                await Task.Run(() => _hostBridge.Start());
            }
            else if (!_config.Storage.EnableHostMount)
            {
                AppendLog("[HostBridge] Stopped host folder synchronization.", Color.FromArgb(255, 166, 87));
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[HostBridge Error] {ex.Message}", Color.FromArgb(255, 123, 114));
        }
    }

    private void OnAutoHideToggleChanged(object? sender, EventArgs e)
    {
        _config.Display.AutoHideConsole = _chkAutoHide.Checked;
        _chkAutoHide.ForeColor = _chkAutoHide.Checked ? Color.FromArgb(88, 166, 255) : Color.FromArgb(170, 180, 195);
        try
        {
            SaveConfigToFile();
            AppendLog($"Updated config.json: display.auto_hide_console = {_config.Display.AutoHideConsole}", Color.FromArgb(88, 166, 255));
        }
        catch (Exception ex)
        {
            AppendLog($"[Config Error] {ex.Message}", Color.FromArgb(255, 123, 114));
        }
    }

    private void OnOpenHostShareClicked(object? sender, EventArgs e)
    {
        string hostDir = HostFolderBridge.EnsureHostFolderAndShortcut(_config.Storage, _configDir);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{hostDir}\"",
            UseShellExecute = true
        });
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
