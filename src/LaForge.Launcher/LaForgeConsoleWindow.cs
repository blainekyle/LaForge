using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LaForge.Launcher;

public sealed class LaForgeConsoleWindow : Form
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    private readonly string _baseDir;
    private readonly string _adbExe;
    private readonly string _scrcpyExe;
    private readonly FlowLayoutPanel _appsFlowPanel;
    private readonly ListView _notifListView;
    private readonly Label _lblHeaderStats;
    private readonly Label _lblGmsStatus;
    private readonly CheckBox _chkInjectMicroG;
    private readonly CheckBox _chkInjectVending;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly Dictionary<string, AppCardControls> _cardsByPackage = new(StringComparer.OrdinalIgnoreCase);
    private List<AppPolicyConfig> _apps = new();
    private Process? _headlessAudioBridgeProcess;
    private EventWaitHandle? _consoleActiveEvent;
    private bool _isRefreshing;
    private bool _suppressGmsToggleEvents;
    private bool _allowExit;

    private sealed class AppCardControls
    {
        public required Panel CardPanel { get; init; }
        public required PictureBox IconBox { get; init; }
        public required Label TitleLabel { get; init; }
        public required Label PackageLabel { get; init; }
        public required Label StatusBadge { get; init; }
        public required Label AudioBadge { get; init; }
        public required Label NotifBadge { get; init; }
        public required Label PolicyBadge { get; init; }
        public required Button BtnLaunchFocus { get; init; }
        public required Button BtnBackground { get; init; }
        public required Button BtnStop { get; init; }
        public required Button BtnGear { get; init; }
    }

    public LaForgeConsoleWindow(string baseDir)
    {
        _baseDir = baseDir;
        _adbExe = Program.ResolveToolPath(baseDir, "bridge/adb.exe", "adb.exe");
        _scrcpyExe = Program.ResolveToolPath(baseDir, "bridge/scrcpy.exe", "scrcpy.exe");

        try
        {
            _consoleActiveEvent = new EventWaitHandle(true, EventResetMode.ManualReset, LaForgeAppRegistry.ConsoleServiceEventName);
            _consoleActiveEvent.Set();
        }
        catch
        {
            // Ignore
        }

        Text = "LaForge Console — Multi-Display App & Notification Manager";
        Size = new Size(1060, 740);
        MinimumSize = new Size(920, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 17, 23);
        ForeColor = Color.FromArgb(230, 235, 245);
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);

        string consoleIco = Path.Combine(LaForgeAppRegistry.GetConfigDirectory(), "console.ico");
        string consolePng = Path.Combine(LaForgeAppRegistry.GetConfigDirectory(), "console.png");
        AppIconGenerator.EnsureAppIcons("com.laforge.console", "LaForge Console", consoleIco, consolePng);
        if (File.Exists(consoleIco))
        {
            Icon = new Icon(consoleIco);
        }

        // System Tray Service Setup
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Show LaForge Console", null, (_, _) => ShowAndActivate());
        trayMenu.Items.Add("Open Shared HOST Folder (%LOCALAPPDATA%\\LaForge\\HOST)", null, (_, _) => OpenFolderInExplorer(HostFolderBridge.ResolveHostDirectory(@"%LOCALAPPDATA%\LaForge\HOST")));
        trayMenu.Items.Add("Open App Config Folder (%LOCALAPPDATA%\\LaForge\\config)", null, (_, _) => OpenFolderInExplorer(LaForgeAppRegistry.GetConfigDirectory()));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit LaForge Console Service", null, (_, _) => ExitService());

        _trayIcon = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "LaForge Console — Multi-App Tray Service",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowAndActivate();

        // Top Header Bar
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 76,
            BackColor = Color.FromArgb(22, 26, 35),
            Padding = new Padding(20, 12, 20, 12)
        };

        var lblTitle = new Label
        {
            Text = "LaForge Central Console",
            Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(88, 166, 255),
            AutoSize = true,
            Location = new Point(20, 10)
        };

        _lblHeaderStats = new Label
        {
            Text = $"Config Store: {LaForgeAppRegistry.GetConfigDirectory()}   |   Scanning remembered LaForge apps...",
            Font = new Font("Consolas", 9F, FontStyle.Regular),
            ForeColor = Color.FromArgb(160, 175, 195),
            AutoSize = true,
            Location = new Point(22, 44)
        };

        var headerActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 10, 4, 0)
        };

        var btnForgeNew = CreateButton("➕ Forge New APK…", Color.FromArgb(88, 56, 145));
        btnForgeNew.Click += async (_, _) =>
        {
            using var wizard = new ForgeWizardDialog(_baseDir);
            wizard.ShowDialog(this);
            if (wizard.Forged)
            {
                RebuildAppCards();
                await RefreshTelemetryAsync();
            }
        };

        var btnOpenConfigDir = CreateButton("📂 /config (.conf)", Color.FromArgb(45, 55, 72));
        btnOpenConfigDir.Click += (_, _) => OpenFolderInExplorer(LaForgeAppRegistry.GetConfigDirectory());

        var btnOpenHostDir = CreateButton("📂 /HOST Share", Color.FromArgb(35, 90, 135));
        btnOpenHostDir.Click += (_, _) => OpenFolderInExplorer(HostFolderBridge.ResolveHostDirectory(@"%LOCALAPPDATA%\LaForge\HOST"));

        var btnRefreshNow = CreateButton("⟳ Refresh", Color.FromArgb(35, 134, 54));
        btnRefreshNow.Click += async (_, _) => await RefreshTelemetryAsync();

        var btnExitService = CreateButton("⏻ Quit Console", Color.FromArgb(140, 45, 50));
        btnExitService.Click += (_, _) => ExitService();

        headerActions.Controls.Add(btnForgeNew);
        headerActions.Controls.Add(btnOpenConfigDir);
        headerActions.Controls.Add(btnOpenHostDir);
        headerActions.Controls.Add(btnRefreshNow);
        headerActions.Controls.Add(btnExitService);

        headerPanel.Controls.Add(headerActions);
        headerPanel.Controls.Add(lblTitle);
        headerPanel.Controls.Add(_lblHeaderStats);

        // Native "microG & Services" Administrative Menu Section (bypasses user-facing apps list)
        var gmsInteropCfg = GmsInteropManager.LoadConfig();
        var gmsPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 78,
            BackColor = Color.FromArgb(19, 23, 33),
            Padding = new Padding(20, 8, 20, 8)
        };

        var lblGmsHeader = new Label
        {
            Text = "🧩 microG & Services   (GMS Core & LVL 0x0001 Licensing Stub — Excluded from User Apps List)",
            Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(210, 168, 255),
            AutoSize = true,
            Location = new Point(20, 9)
        };

        _lblGmsStatus = new Label
        {
            Text = "microG Core: Checking...   |   Vending LVL Stub (0x0001 NOT_LICENSED): Checking...",
            Font = new Font("Consolas", 8.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(155, 175, 200),
            AutoSize = true,
            Location = new Point(22, 31)
        };

        _chkInjectMicroG = new CheckBox
        {
            Text = "Inject microG on AVD Boot",
            Checked = gmsInteropCfg.InjectMicroGOnBoot,
            Font = new Font("Segoe UI Semibold", 8.75F, FontStyle.Regular),
            ForeColor = Color.FromArgb(220, 230, 242),
            AutoSize = true,
            Location = new Point(24, 52),
            Cursor = Cursors.Hand
        };

        _chkInjectVending = new CheckBox
        {
            Text = "Inject Vending LVL Stub (0x0001 NOT_LICENSED) on AVD Boot",
            Checked = gmsInteropCfg.InjectVendingStubOnBoot,
            Font = new Font("Segoe UI Semibold", 8.75F, FontStyle.Regular),
            ForeColor = Color.FromArgb(220, 230, 242),
            AutoSize = true,
            Location = new Point(225, 52),
            Cursor = Cursors.Hand
        };

        async Task HandleGmsToggleChangedAsync()
        {
            if (_suppressGmsToggleEvents) return;
            var updatedCfg = GmsInteropManager.LoadConfig();
            updatedCfg.InjectMicroGOnBoot = _chkInjectMicroG.Checked;
            updatedCfg.InjectVendingStubOnBoot = _chkInjectVending.Checked;
            GmsInteropManager.SaveConfig(updatedCfg);

            _lblGmsStatus.Text = "Applying GMS & Vending LVL boot policy...";
            _lblGmsStatus.ForeColor = Color.FromArgb(255, 205, 95);
            await Task.Run(() => GmsInteropManager.ExecutePreFlightInjection(_adbExe, _baseDir, null, forceReconcile: true));
            await RefreshTelemetryAsync();
        }

        _chkInjectMicroG.CheckedChanged += async (_, _) => await HandleGmsToggleChangedAsync();
        _chkInjectVending.CheckedChanged += async (_, _) => await HandleGmsToggleChangedAsync();

        var gmsActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 14, 4, 0)
        };

        var btnMicroGSettings = CreateButton("⚙ microG Settings", Color.FromArgb(88, 56, 145));
        btnMicroGSettings.Click += async (_, _) =>
        {
            var confirm = MessageBox.Show(
                "WARNING: microG Settings is an advanced troubleshooting tool.\n\n" +
                "LaForge configures microG automatically. Changing settings here can BREAK " +
                "app compatibility, push notifications, or the licensing stub, and may require " +
                "clearing microG data or re-injecting to recover.\n\n" +
                "Do NOT sign in with a real Google account. Signing in from an uncertified " +
                "device can cause Google to FLAG, restrict, or lock your account. Google Play " +
                "purchases cannot be authorized through microG in any case.\n\n" +
                "Open microG Settings anyway?",
                "LaForge - Advanced / Risky",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            btnMicroGSettings.Enabled = false;
            _lblGmsStatus.Text = "Launching microG Settings (org.microg.gms.ui.SettingsActivity)...";
            _lblGmsStatus.ForeColor = Color.FromArgb(210, 168, 255);
            try
            {
                string msg = await Task.Run(() => GmsInteropManager.LaunchMicroGSettings(_adbExe, _scrcpyExe, _baseDir));
                _lblGmsStatus.Text = msg;
            }
            finally
            {
                btnMicroGSettings.Enabled = true;
            }
        };

        var btnClearMicroG = CreateButton("🧹 Clear microG Data", Color.FromArgb(128, 72, 28));
        btnClearMicroG.Click += async (_, _) =>
        {
            btnClearMicroG.Enabled = false;
            try
            {
                string msg = await Task.Run(() => GmsInteropManager.ClearMicroGData(_adbExe));
                _lblGmsStatus.Text = msg;
                _lblGmsStatus.ForeColor = Color.FromArgb(255, 205, 95);
            }
            finally
            {
                btnClearMicroG.Enabled = true;
            }
        };

        var btnSyncGms = CreateButton("🔄 Re-Inject / Sync", Color.FromArgb(35, 95, 145));
        btnSyncGms.Click += async (_, _) =>
        {
            btnSyncGms.Enabled = false;
            _lblGmsStatus.Text = "Running ADB pre-flight injection for microG + com.android.vending stub...";
            _lblGmsStatus.ForeColor = Color.FromArgb(121, 192, 255);
            try
            {
                await Task.Run(() => GmsInteropManager.ExecutePreFlightInjection(_adbExe, _baseDir, null, forceReconcile: true));
                await RefreshTelemetryAsync();
            }
            finally
            {
                btnSyncGms.Enabled = true;
            }
        };

        gmsActions.Controls.Add(btnMicroGSettings);
        gmsActions.Controls.Add(btnClearMicroG);
        gmsActions.Controls.Add(btnSyncGms);

        gmsPanel.Controls.Add(gmsActions);
        gmsPanel.Controls.Add(lblGmsHeader);
        gmsPanel.Controls.Add(_lblGmsStatus);
        gmsPanel.Controls.Add(_chkInjectMicroG);
        gmsPanel.Controls.Add(_chkInjectVending);

        // Bottom Notification Center Panel
        var notifContainer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 195,
            BackColor = Color.FromArgb(19, 22, 30),
            Padding = new Padding(18, 8, 18, 12)
        };

        var notifHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 32
        };

        var lblNotifTitle = new Label
        {
            Text = "🔔 Intercepted Android Notification Center (Suppressed from App Shades)",
            Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(255, 190, 90),
            AutoSize = true,
            Location = new Point(2, 5)
        };

        var btnDismissNotifs = new Button
        {
            Text = "Clear All Notifications",
            Dock = DockStyle.Right,
            Width = 165,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 52, 66),
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnDismissNotifs.Click += async (_, _) =>
        {
            await Task.Run(() =>
            {
                try
                {
                    if (File.Exists(_adbExe))
                    {
                        Program.RunProcessCaptureOutput(_adbExe, new[] { "shell", "service", "call", "notification", "1" }, 4000);
                    }
                }
                catch { }
            });
            await RefreshTelemetryAsync();
        };

        notifHeader.Controls.Add(btnDismissNotifs);
        notifHeader.Controls.Add(lblNotifTitle);

        _notifListView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BackColor = Color.FromArgb(13, 15, 21),
            ForeColor = Color.FromArgb(225, 232, 240),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular)
        };
        _notifListView.Columns.Add("LaForge App", 210);
        _notifListView.Columns.Add("Notification Title", 270);
        _notifListView.Columns.Add("Message / Content", 380);
        _notifListView.Columns.Add("Channel", 140);

        notifContainer.Controls.Add(_notifListView);
        notifContainer.Controls.Add(notifHeader);

        // Center Remembered Apps Grid / List
        _appsFlowPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(18, 14, 18, 14),
            BackColor = Color.FromArgb(15, 17, 23)
        };

        Controls.Add(_appsFlowPanel);
        Controls.Add(notifContainer);
        Controls.Add(gmsPanel);
        Controls.Add(headerPanel);

        FormClosing += (_, e) =>
        {
            if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                return;
            }

            try
            {
                _consoleActiveEvent?.Reset();
                _consoleActiveEvent?.Dispose();
                _consoleActiveEvent = null;
            }
            catch { }

            _refreshTimer?.Stop();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            StopHeadlessAudioBridge();

            try
            {
                var scrcpyWins = LaForgeAppRegistry.FindAllScrcpyWindows();
                bool anyVisibleAppWindow = false;
                foreach (var win in scrcpyWins)
                {
                    if (!win.IsVisible)
                    {
                        try
                        {
                            using var p = Process.GetProcessById((int)win.ProcessId);
                            p.Kill(entireProcessTree: true);
                        }
                        catch { }
                    }
                    else
                    {
                        anyVisibleAppWindow = true;
                    }
                }

                if (!anyVisibleAppWindow)
                {
                    Program.StopHeadlessEmulator(_adbExe);
                }
            }
            catch { }
        };

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 3500 };
        _refreshTimer.Tick += async (_, _) => await RefreshTelemetryAsync();

        Shown += async (_, _) =>
        {
            RebuildAppCards();
            await RefreshTelemetryAsync();
            _refreshTimer.Start();
        };
    }

    private void RebuildAppCards()
    {
        _apps = LaForgeAppRegistry.DiscoverRememberedApps(_baseDir);
        _appsFlowPanel.SuspendLayout();
        _appsFlowPanel.Controls.Clear();
        _cardsByPackage.Clear();

        foreach (var app in _apps)
        {
            var card = CreateAppCard(app);
            _cardsByPackage[app.PackageName] = card;
            _appsFlowPanel.Controls.Add(card.CardPanel);
        }

        _appsFlowPanel.ResumeLayout();
    }

    private AppCardControls CreateAppCard(AppPolicyConfig app)
    {
        var panel = new Panel
        {
            Width = 995,
            Height = 82,
            BackColor = Color.FromArgb(24, 28, 38),
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(12)
        };

        var iconBox = new PictureBox
        {
            Size = new Size(52, 52),
            Location = new Point(14, 15),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent
        };
        LoadCardIcon(iconBox, app);

        var lblTitle = new Label
        {
            Text = string.IsNullOrWhiteSpace(app.AppTitle) ? app.PackageName : app.AppTitle,
            Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(80, 12)
        };

        var lblPkg = new Label
        {
            Text = $"{app.PackageName}   •   {Path.GetFileName(LaForgeAppRegistry.GetConfFilePath(app.PackageName))}",
            Font = new Font("Consolas", 8.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(145, 160, 180),
            AutoSize = true,
            Location = new Point(82, 38)
        };

        var lblPolicy = new Label
        {
            Text = FormatPolicySummary(app),
            Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(130, 180, 240),
            AutoSize = true,
            Location = new Point(82, 56)
        };

        var statusBadge = CreateBadgeLabel("○ STOPPED", new Point(390, 16), 175, Color.FromArgb(36, 42, 54), Color.FromArgb(165, 175, 190));
        var audioBadge = CreateBadgeLabel("Idle", new Point(390, 45), 175, Color.FromArgb(30, 35, 46), Color.FromArgb(140, 150, 165));
        var notifBadge = CreateBadgeLabel("🔔 0", new Point(575, 16), 95, Color.FromArgb(30, 35, 46), Color.FromArgb(160, 170, 185));

        var btnLaunch = CreateButton("▶ Launch / Focus", Color.FromArgb(35, 134, 54), new Point(685, 22), new Size(122, 36));
        btnLaunch.Click += async (_, _) => await LaunchOrFocusAppAsync(app.PackageName);

        var btnBg = CreateButton("🌙 Background", Color.FromArgb(45, 95, 155), new Point(813, 22), new Size(110, 36));
        btnBg.Click += async (_, _) => await BackgroundAppAsync(app.PackageName);

        var btnStop = CreateButton("■", Color.FromArgb(135, 48, 52), new Point(929, 22), new Size(28, 36));
        btnStop.Click += async (_, _) => await StopAppAsync(app.PackageName);

        var btnIcon = CreateButton("🖼", Color.FromArgb(62, 70, 86), new Point(48, 48), new Size(22, 22));
        btnIcon.Font = new Font("Segoe UI Emoji", 8F);
        btnIcon.Padding = Padding.Empty;
        new ToolTip().SetToolTip(btnIcon, "Change this app's icon (choose an image file)");
        btnIcon.Click += (_, _) => ChangeAppIcon(app, iconBox);

        var btnGear = CreateButton("⚙", Color.FromArgb(62, 70, 86), new Point(961, 22), new Size(28, 36));
        btnGear.Click += async (_, _) => await OpenAppConfigModalAsync(app.PackageName);

        panel.Controls.Add(iconBox);
        panel.Controls.Add(lblTitle);
        panel.Controls.Add(lblPkg);
        panel.Controls.Add(lblPolicy);
        panel.Controls.Add(statusBadge);
        panel.Controls.Add(audioBadge);
        panel.Controls.Add(notifBadge);
        panel.Controls.Add(btnLaunch);
        panel.Controls.Add(btnBg);
        panel.Controls.Add(btnStop);
        panel.Controls.Add(btnGear);
        panel.Controls.Add(btnIcon);
        btnIcon.BringToFront();

        return new AppCardControls
        {
            CardPanel = panel,
            IconBox = iconBox,
            TitleLabel = lblTitle,
            PackageLabel = lblPkg,
            StatusBadge = statusBadge,
            AudioBadge = audioBadge,
            NotifBadge = notifBadge,
            PolicyBadge = lblPolicy,
            BtnLaunchFocus = btnLaunch,
            BtnBackground = btnBg,
            BtnStop = btnStop,
            BtnGear = btnGear
        };
    }

    private async Task RefreshTelemetryAsync()
    {
        if (_isRefreshing || IsDisposed) return;
        _isRefreshing = true;

        try
        {
            _apps = LaForgeAppRegistry.DiscoverRememberedApps(_baseDir);
            if (_apps.Count != _cardsByPackage.Count)
            {
                RebuildAppCards();
            }

            var snapshotApps = _apps.ToList();
            var telemetryTask = Task.Run(() => LaForgeAppRegistry.QueryLiveTelemetry(_adbExe, snapshotApps));
            var gmsStatusTask = Task.Run(() => (Cfg: GmsInteropManager.LoadConfig(), Status: GmsInteropManager.QueryInteropStatus(_adbExe, _baseDir)));
            await Task.WhenAll(telemetryTask, gmsStatusTask);

            var telemetry = telemetryTask.Result;
            var (gmsCfg, gmsStatus) = gmsStatusTask.Result;

            if (IsDisposed) return;

            _suppressGmsToggleEvents = true;
            try
            {
                _chkInjectMicroG.Checked = gmsCfg.InjectMicroGOnBoot;
                _chkInjectVending.Checked = gmsCfg.InjectVendingStubOnBoot;
            }
            finally
            {
                _suppressGmsToggleEvents = false;
            }

            if (gmsStatus.DeviceOnline)
            {
                string mgText = gmsStatus.MicroGInstalled
                    ? $"● microG Installed ({gmsStatus.DetectedMicroGPackage}{(string.IsNullOrEmpty(gmsStatus.MicroGVersion) ? "" : $" v{gmsStatus.MicroGVersion}")})"
                    : "○ microG Not Installed";
                string vndText = gmsStatus.VendingStubInstalled
                    ? "● Vending LVL Stub Installed (0x0001 NOT_LICENSED)"
                    : "○ Vending LVL Stub Not Installed";
                _lblGmsStatus.Text = $"{mgText}   |   {vndText}";
                _lblGmsStatus.ForeColor = (gmsStatus.MicroGInstalled && gmsStatus.VendingStubInstalled)
                    ? Color.FromArgb(126, 231, 135)
                    : Color.FromArgb(227, 179, 65);
            }
            else
            {
                string mgHost = gmsStatus.MicroGApkPresentOnHost ? "Ready" : "Missing APK";
                string vndHost = gmsStatus.VendingApkPresentOnHost ? "Ready" : "Missing APK";
                _lblGmsStatus.Text = $"AVD Offline — Next Boot: microG={(gmsCfg.InjectMicroGOnBoot ? "ON" : "OFF")} ({mgHost})  |  Vending LVL Stub={(gmsCfg.InjectVendingStubOnBoot ? "ON" : "OFF")} ({vndHost})";
                _lblGmsStatus.ForeColor = Color.FromArgb(165, 175, 195);
            }

            int runningCount = 0;
            int bgCount = 0;
            int totalNotifs = 0;
            bool anyBackgroundPlayingAudio = false;
            bool anyVisualScrcpyOpen = false;

            var allNotifs = new List<AppNotificationItem>();

            foreach (var app in snapshotApps)
            {
                if (!_cardsByPackage.TryGetValue(app.PackageName, out var card)) continue;
                if (!telemetry.TryGetValue(app.PackageName, out var tel)) continue;

                card.PolicyBadge.Text = FormatPolicySummary(app);

                if (tel.ScrcpyProcessId > 0)
                {
                    anyVisualScrcpyOpen = true;
                }

                // 1. Running / Virtual Display / Backgrounded state
                if (tel.IsRunningOnDisplay)
                {
                    runningCount++;
                    string vdText = tel.VirtualDisplayId > 0 ? $"Display #{tel.VirtualDisplayId}" : $"PID {tel.ScrcpyProcessId}";
                    card.StatusBadge.Text = $"● RUNNING ({vdText})";
                    card.StatusBadge.BackColor = Color.FromArgb(22, 68, 42);
                    card.StatusBadge.ForeColor = Color.FromArgb(126, 231, 135);
                    card.BtnLaunchFocus.Text = tel.ScrcpyProcessId > 0 ? "🎯 Focus Window" : "▶ Attach Display";
                    card.BtnBackground.Enabled = tel.ScrcpyProcessId > 0;
                    card.BtnStop.Enabled = true;
                }
                else if (tel.IsBackgrounded)
                {
                    bgCount++;
                    string bgDetail = tel.VirtualDisplayId > 0
                        ? $"Display #{tel.VirtualDisplayId}"
                        : $"PID {(tel.AndroidPid > 0 ? tel.AndroidPid : tel.ScrcpyProcessId)}";
                    card.StatusBadge.Text = $"◐ BACKGROUND ({bgDetail})";
                    card.StatusBadge.BackColor = Color.FromArgb(72, 54, 20);
                    card.StatusBadge.ForeColor = Color.FromArgb(255, 200, 95);
                    card.BtnLaunchFocus.Text = tel.ScrcpyWindowHandle != IntPtr.Zero ? "▶ Restore Window" : "▶ Resume Display";
                    card.BtnBackground.Enabled = false;
                    card.BtnStop.Enabled = true;

                    if (tel.IsPlayingAudio && !app.MuteAudio && tel.ScrcpyProcessId <= 0)
                    {
                        anyBackgroundPlayingAudio = true;
                    }
                }
                else
                {
                    card.StatusBadge.Text = "○ STOPPED";
                    card.StatusBadge.BackColor = Color.FromArgb(34, 39, 50);
                    card.StatusBadge.ForeColor = Color.FromArgb(150, 160, 175);
                    card.BtnLaunchFocus.Text = "▶ Launch App";
                    card.BtnBackground.Enabled = false;
                    card.BtnStop.Enabled = false;
                }

                // 2. Audio playback / recording state
                if (app.MuteAudio && app.MuteMic)
                {
                    card.AudioBadge.Text = "🔇 Audio & Mic Muted";
                    card.AudioBadge.BackColor = Color.FromArgb(68, 30, 34);
                    card.AudioBadge.ForeColor = Color.FromArgb(255, 140, 140);
                }
                else if (tel.IsRecordingAudio)
                {
                    card.AudioBadge.Text = "🎙 RECORDING AUDIO";
                    card.AudioBadge.BackColor = Color.FromArgb(88, 28, 32);
                    card.AudioBadge.ForeColor = Color.FromArgb(255, 130, 130);
                }
                else if (tel.IsPlayingAudio)
                {
                    card.AudioBadge.Text = app.MuteAudio ? "🔇 Playing (Muted)" : "🔊 PLAYING AUDIO";
                    card.AudioBadge.BackColor = Color.FromArgb(20, 62, 75);
                    card.AudioBadge.ForeColor = Color.FromArgb(105, 230, 255);
                }
                else
                {
                    card.AudioBadge.Text = app.MuteAudio ? "🔇 Muted" : "Audio: Idle";
                    card.AudioBadge.BackColor = Color.FromArgb(30, 35, 46);
                    card.AudioBadge.ForeColor = Color.FromArgb(140, 150, 165);
                }

                // 3. Notification Counter
                int nCount = tel.Notifications.Count;
                totalNotifs += nCount;
                card.NotifBadge.Text = $"🔔 {nCount} Notif{(nCount == 1 ? "" : "s")}";
                card.NotifBadge.BackColor = nCount > 0 ? Color.FromArgb(75, 55, 20) : Color.FromArgb(30, 35, 46);
                card.NotifBadge.ForeColor = nCount > 0 ? Color.FromArgb(255, 205, 95) : Color.FromArgb(150, 160, 175);

                allNotifs.AddRange(tel.Notifications);
            }

            // Maintain headless audio bridge when a backgrounded app is playing audio and no scrcpy process is running
            if (anyBackgroundPlayingAudio && !anyVisualScrcpyOpen)
            {
                EnsureHeadlessAudioBridge();
            }
            else if (anyVisualScrcpyOpen)
            {
                StopHeadlessAudioBridge();
            }

            _lblHeaderStats.Text =
                $"Remembered Apps: {snapshotApps.Count}   |   Active Displays: {runningCount}   |   Backgrounded: {bgCount}   |   Active Notifications: {totalNotifs}   |   Config: %LOCALAPPDATA%\\LaForge\\config";

            UpdateNotificationListView(allNotifs);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void UpdateNotificationListView(List<AppNotificationItem> notifs)
    {
        _notifListView.BeginUpdate();
        _notifListView.Items.Clear();
        if (notifs.Count == 0)
        {
            var emptyItem = new ListViewItem("No active app notifications");
            emptyItem.SubItems.Add("—");
            emptyItem.SubItems.Add("Notifications from running or backgrounded LaForge apps appear here automatically.");
            emptyItem.SubItems.Add("—");
            emptyItem.ForeColor = Color.FromArgb(130, 140, 155);
            _notifListView.Items.Add(emptyItem);
        }
        else
        {
            foreach (var n in notifs)
            {
                var item = new ListViewItem(n.AppTitle);
                item.SubItems.Add(n.Title);
                item.SubItems.Add(n.Text);
                item.SubItems.Add(n.Channel);
                item.ForeColor = Color.FromArgb(255, 215, 130);
                _notifListView.Items.Add(item);
            }
        }
        _notifListView.EndUpdate();
    }

    private async Task LaunchOrFocusAppAsync(string packageName)
    {
        var app = _apps.FirstOrDefault(a => a.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));
        if (app == null) return;

        // 1. Instant 0ms restore/focus if the app's scrcpy window already exists (visible OR hidden/backgrounded via SW_HIDE)
        if (LaForgeAppRegistry.TryRestoreOrFocusAppWindow(app))
        {
            await RefreshTelemetryAsync();
            return;
        }

        StopHeadlessAudioBridge();

        if (!string.IsNullOrWhiteSpace(app.ExecutablePath) && File.Exists(app.ExecutablePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = app.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(app.ExecutablePath) ?? _baseDir,
                UseShellExecute = true
            });
            await Task.Delay(1200);
            await RefreshTelemetryAsync();
        }
    }

    private async Task BackgroundAppAsync(string packageName)
    {
        var app = _apps.FirstOrDefault(a => a.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));
        if (app == null) return;

        // Instant 0ms window hide via SW_HIDE — keeps scrcpy, its virtual display, and audio running in the background
        LaForgeAppRegistry.BackgroundAppWindow(app);
        await RefreshTelemetryAsync();
    }

    private async Task StopAppAsync(string packageName)
    {
        var app = _apps.FirstOrDefault(a => a.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));
        if (app == null) return;

        try
        {
            var win = LaForgeAppRegistry.FindAppScrcpyWindow(app);
            if (win != null && win.ProcessId > 0)
            {
                using var proc = Process.GetProcessById(win.ProcessId);
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore
        }

        await Task.Run(() =>
        {
            try
            {
                if (File.Exists(_adbExe))
                {
                    Program.RunProcessCaptureOutput(_adbExe, new[] { "shell", "am", "force-stop", packageName }, 4000);
                }
            }
            catch
            {
                // Ignore if adb or device is not currently reachable
            }
        });
        await RefreshTelemetryAsync();
    }

    private async Task OpenAppConfigModalAsync(string packageName)
    {
        string confPath = LaForgeAppRegistry.GetConfFilePath(packageName);
        var policy = LaForgeAppRegistry.ParseConfFile(confPath);
        if (string.IsNullOrWhiteSpace(policy.PackageName))
        {
            policy.PackageName = packageName;
        }

        using var dlg = new Form
        {
            Text = $"⚙ App Policy & Configuration — {policy.AppTitle} ({policy.PackageName})",
            Size = new Size(620, 620),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.FromArgb(20, 24, 33),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular)
        };

        var lblPath = new Label
        {
            Text = $"Config File: {confPath}",
            Font = new Font("Consolas", 8.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(140, 175, 220),
            Location = new Point(20, 14),
            AutoSize = true
        };

        CheckBox MakeToggle(string label, bool isChecked, int y, Color color)
        {
            return new CheckBox
            {
                Text = label,
                Checked = isChecked,
                Location = new Point(24, y),
                AutoSize = true,
                ForeColor = color,
                Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Regular),
                Cursor = Cursors.Hand
            };
        }

        var chkVd = MakeToggle("Launch on Isolated Virtual Display (scrcpy --new-display)", policy.VirtualDisplay, 46, Color.FromArgb(126, 231, 135));
        var chkBg = MakeToggle("Background App When Window Closes (keep running in Android RAM)", policy.BackgroundOnClose, 80, Color.FromArgb(255, 205, 95));
        var chkNet = MakeToggle("Block Network Access (Per-UID iptables REJECT & netpolicy blacklist)", policy.BlockNetwork, 114, Color.FromArgb(255, 140, 140));
        var chkAudio = MakeToggle("Mute Audio Output (Android AppOps PLAY_AUDIO ignore)", policy.MuteAudio, 148, Color.FromArgb(255, 166, 87));
        var chkMic = MakeToggle("Mute Microphone Input (Android AppOps RECORD_AUDIO ignore)", policy.MuteMic, 182, Color.FromArgb(255, 166, 87));
        var chkHid = MakeToggle("Block HID / Mouse, Keyboard & Device Sensors (--no-control & AppOps)", policy.BlockHid, 216, Color.FromArgb(210, 168, 255));
        var chkHost = MakeToggle("Enable Shared HOST Folder Mount (%LOCALAPPDATA%\\LaForge\\HOST <-> /sdcard/HOST)", policy.HostShareEnabled, 250, Color.FromArgb(105, 210, 255));

        // Windowing & Screen Size Customization Panel
        var pnlWindowing = new Panel
        {
            Location = new Point(20, 286),
            Size = new Size(564, 215),
            BackColor = Color.FromArgb(28, 34, 46),
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblWinHeader = new Label
        {
            Text = "📐 Windowing, Orientation & Screen Size Customization",
            Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(121, 192, 255),
            Location = new Point(12, 10),
            AutoSize = true
        };

        string modeDesc = policy.IsWindowable
            ? "Window-able (Multi-Window Resizable)"
            : $"Fixed Orientation ({policy.PreferredOrientation})";
        var lblManifestProfile = new Label
        {
            Text = $"Polled APK Manifest:  Orientation = {policy.PreferredOrientation.ToUpperInvariant()}   |   {modeDesc}   |   Preferred = {policy.PreferredScreenSize}",
            Font = new Font("Consolas", 8.25F, FontStyle.Regular),
            ForeColor = Color.FromArgb(165, 214, 255),
            Location = new Point(12, 34),
            AutoSize = true
        };

        var lblPreset = new Label
        {
            Text = "Supported Sizes:",
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Regular),
            ForeColor = Color.FromArgb(210, 220, 235),
            Location = new Point(12, 64),
            AutoSize = true
        };

        var cmbPresets = new ComboBox
        {
            Location = new Point(122, 60),
            Size = new Size(245, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular)
        };

        string defaultOptionText = $"Default ({policy.PreferredScreenSize} — Manifest Preferred)";
        cmbPresets.Items.Add(defaultOptionText);
        foreach (string sz in policy.SupportedScreenSizes)
        {
            cmbPresets.Items.Add(sz);
        }
        const string customOptionText = "Custom Freetext Override (WxH) *";
        cmbPresets.Items.Add(customOptionText);

        var lblOverride = new Label
        {
            Text = "Override (WxH)*:",
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Regular),
            ForeColor = Color.FromArgb(255, 205, 95),
            Location = new Point(378, 64),
            AutoSize = true
        };

        var txtCustomSize = new TextBox
        {
            Location = new Point(480, 60),
            Size = new Size(70, 26),
            BackColor = Color.FromArgb(18, 22, 30),
            ForeColor = Color.FromArgb(255, 225, 130),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9.5F, FontStyle.Bold),
            Text = policy.ScreenSizeOverride ?? ""
        };

        bool suppressPresetSync = false;
        if (string.IsNullOrWhiteSpace(policy.ScreenSizeOverride))
        {
            cmbPresets.SelectedIndex = 0;
        }
        else
        {
            int matchedIndex = -1;
            for (int i = 1; i < cmbPresets.Items.Count - 1; i++)
            {
                string itemText = cmbPresets.Items[i]?.ToString() ?? "";
                if (itemText.StartsWith(policy.ScreenSizeOverride, StringComparison.OrdinalIgnoreCase))
                {
                    matchedIndex = i;
                    break;
                }
            }
            cmbPresets.SelectedIndex = matchedIndex >= 0 ? matchedIndex : cmbPresets.Items.Count - 1;
        }

        cmbPresets.SelectedIndexChanged += (_, _) =>
        {
            if (suppressPresetSync) return;
            string selected = cmbPresets.SelectedItem?.ToString() ?? "";
            suppressPresetSync = true;
            if (selected == defaultOptionText)
            {
                txtCustomSize.Text = "";
            }
            else if (selected != customOptionText)
            {
                string token = selected.Split(' ')[0].Trim();
                if (AppPolicyConfig.TryParseDimensions(token, out int pw, out int ph))
                {
                    txtCustomSize.Text = $"{pw}x{ph}";
                }
            }
            else
            {
                txtCustomSize.Focus();
            }
            suppressPresetSync = false;
        };

        txtCustomSize.TextChanged += (_, _) =>
        {
            if (suppressPresetSync) return;
            suppressPresetSync = true;
            string raw = txtCustomSize.Text.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                cmbPresets.SelectedIndex = 0;
            }
            else
            {
                int matched = -1;
                for (int i = 1; i < cmbPresets.Items.Count - 1; i++)
                {
                    string itemText = cmbPresets.Items[i]?.ToString() ?? "";
                    if (itemText.StartsWith(raw + " ", StringComparison.OrdinalIgnoreCase) ||
                        itemText.Equals(raw, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = i;
                        break;
                    }
                }
                cmbPresets.SelectedIndex = matched >= 0 ? matched : cmbPresets.Items.Count - 1;
            }
            suppressPresetSync = false;
        };

        var lblAsteriskWarn = new Label
        {
            Text = "* Compatibility Risk: Custom pixel sizes or aspect ratios outside the app's declared orientation\r\n  may cause UI layout clipping, stretching, or camera sensor preview distortion.",
            Font = new Font("Segoe UI", 8.25F, FontStyle.Italic),
            ForeColor = Color.FromArgb(240, 180, 90),
            Location = new Point(12, 95),
            Size = new Size(540, 34)
        };

        string dynamicResizeLabel = policy.IsWindowable
            ? "Allow Live Aspect-Ratio Resizing While Open (App is Window-able)"
            : "Force Live Aspect-Ratio Resizing While Open * (App declares fixed orientation)";
        var chkDynamicResize = new CheckBox
        {
            Text = dynamicResizeLabel,
            Checked = policy.DynamicAspectResize,
            Location = new Point(14, 138),
            AutoSize = true,
            ForeColor = policy.IsWindowable ? Color.FromArgb(126, 231, 135) : Color.FromArgb(255, 205, 95),
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };

        var lblDynamicHint = new Label
        {
            Text = "When enabled, SDL3 aspect-ratio locking (SDL_SetWindowAspectRatio) is bypassed via SCRCPY_FREE_ASPECT=1\r\nand dragging any side/corner handle hot-swaps the virtual display to the new aspect ratio upon mouse release.",
            Font = new Font("Segoe UI", 8F, FontStyle.Regular),
            ForeColor = Color.FromArgb(150, 165, 190),
            Location = new Point(32, 164),
            Size = new Size(520, 34)
        };

        pnlWindowing.Controls.Add(lblWinHeader);
        pnlWindowing.Controls.Add(lblManifestProfile);
        pnlWindowing.Controls.Add(lblPreset);
        pnlWindowing.Controls.Add(cmbPresets);
        pnlWindowing.Controls.Add(lblOverride);
        pnlWindowing.Controls.Add(txtCustomSize);
        pnlWindowing.Controls.Add(lblAsteriskWarn);
        pnlWindowing.Controls.Add(chkDynamicResize);
        pnlWindowing.Controls.Add(lblDynamicHint);

        var btnOpenConfFile = CreateButton("Open .conf in Notepad", Color.FromArgb(50, 58, 75), new Point(20, 520), new Size(185, 38));
        btnOpenConfFile.Click += (_, _) =>
        {
            LaForgeAppRegistry.Save(policy);
            Process.Start(new ProcessStartInfo { FileName = "notepad.exe", Arguments = $"\"{confPath}\"", UseShellExecute = true });
        };

        var btnSave = CreateButton("Save && Apply Live", Color.FromArgb(35, 134, 54), new Point(399, 520), new Size(185, 38));
        btnSave.Click += (_, _) =>
        {
            string rawOverride = txtCustomSize.Text.Trim();
            if (!string.IsNullOrEmpty(rawOverride))
            {
                if (!AppPolicyConfig.TryParseDimensions(rawOverride, out int ow, out int oh))
                {
                    MessageBox.Show(
                        dlg,
                        "Please enter a valid WxH pixel resolution (for example: 540x960, 1280x720, or 900x900) between 240 and 3840 pixels, or leave the field blank to use the manifest-preferred size.",
                        "Invalid Screen Size Override",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
                policy.ScreenSizeOverride = $"{ow}x{oh}";
            }
            else
            {
                policy.ScreenSizeOverride = "";
            }

            policy.VirtualDisplay = chkVd.Checked;
            policy.BackgroundOnClose = chkBg.Checked;
            policy.BlockNetwork = chkNet.Checked;
            policy.MuteAudio = chkAudio.Checked;
            policy.MuteMic = chkMic.Checked;
            policy.BlockHid = chkHid.Checked;
            policy.HostShareEnabled = chkHost.Checked;
            policy.DynamicAspectResize = chkDynamicResize.Checked;
            LaForgeAppRegistry.Save(policy);

            // Sync BundleDir/config.json Window dimensions so direct .exe launches also use the effective size
            try
            {
                if (!string.IsNullOrWhiteSpace(policy.BundleDir))
                {
                    string bundleCfgPath = Path.Combine(policy.BundleDir, "config.json");
                    if (File.Exists(bundleCfgPath))
                    {
                        var jsonOpts = new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            WriteIndented = true
                        };
                        var bundleCfg = System.Text.Json.JsonSerializer.Deserialize<LaForgeConfig>(File.ReadAllText(bundleCfgPath), jsonOpts);
                        if (bundleCfg != null)
                        {
                            var (effW, effH) = policy.GetEffectiveScreenSize(bundleCfg.Display.Window);
                            bundleCfg.Display.Window.Width = effW;
                            bundleCfg.Display.Window.Height = effH;
                            File.WriteAllText(bundleCfgPath, System.Text.Json.JsonSerializer.Serialize(bundleCfg, jsonOpts));
                        }
                    }
                }
            }
            catch
            {
                // Ignore config.json sync failure
            }

            dlg.DialogResult = DialogResult.OK;
            dlg.Close();
        };

        dlg.Controls.Add(lblPath);
        dlg.Controls.Add(chkVd);
        dlg.Controls.Add(chkBg);
        dlg.Controls.Add(chkNet);
        dlg.Controls.Add(chkAudio);
        dlg.Controls.Add(chkMic);
        dlg.Controls.Add(chkHid);
        dlg.Controls.Add(chkHost);
        dlg.Controls.Add(pnlWindowing);
        dlg.Controls.Add(btnOpenConfFile);
        dlg.Controls.Add(btnSave);

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            await Task.Run(() => LaForgeAppRegistry.EnforcePoliciesOnGuest(_adbExe, policy, forceNetworkUpdate: true));
            await RefreshTelemetryAsync();
        }
    }

    private void EnsureHeadlessAudioBridge()
    {
        try
        {
            if (_headlessAudioBridgeProcess != null && !_headlessAudioBridgeProcess.HasExited)
            {
                return;
            }
            if (!File.Exists(_scrcpyExe)) return;

            _headlessAudioBridgeProcess = Program.StartBackgroundProcess(
                _scrcpyExe,
                new[] { "--no-video", "--no-window", "--audio-codec=raw" },
                hidden: true);
        }
        catch
        {
            // Ignore
        }
    }

    private void StopHeadlessAudioBridge()
    {
        try
        {
            if (_headlessAudioBridgeProcess != null && !_headlessAudioBridgeProcess.HasExited)
            {
                _headlessAudioBridgeProcess.Kill();
            }
        }
        catch
        {
            // Ignore
        }
        _headlessAudioBridgeProcess = null;
    }

    /// <summary>Lets the user pick any image as this bundle's icon; it is stored as a "custom" icon that rebuilds never overwrite.</summary>
    private void ChangeAppIcon(AppPolicyConfig app, PictureBox iconBox)
    {
        if (string.IsNullOrWhiteSpace(app.BundleDir) || !Directory.Exists(app.BundleDir))
        {
            MessageBox.Show(this, "This app's bundle folder could not be found, so its icon cannot be changed.", "LaForge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dlg = new OpenFileDialog
        {
            Title = $"Choose an icon for {(string.IsNullOrWhiteSpace(app.AppTitle) ? app.PackageName : app.AppTitle)}",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.webp|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) { return; }

        try
        {
            AppIconGenerator.ApplyCustomIcon(app.BundleDir, dlg.FileName);
            LoadCardIcon(iconBox, app);
            MessageBox.Show(this, "Icon updated. A window that is already open keeps its old icon until the app is relaunched.", "LaForge", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not use that image: {ex.Message}", "LaForge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void LoadCardIcon(PictureBox box, AppPolicyConfig app)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(app.BundleDir))
            {
                string pngPath = Path.Combine(app.BundleDir, "bridge", "scrcpy.png");
                if (File.Exists(pngPath))
                {
                    using var fs = new FileStream(pngPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var loaded = Image.FromStream(fs);
                    var previous = box.Image;
                    box.Image = new Bitmap(loaded);
                    previous?.Dispose();
                    return;
                }
            }
            if (!string.IsNullOrWhiteSpace(app.IconPath) && File.Exists(app.IconPath))
            {
                using var ico = new Icon(app.IconPath, new Size(48, 48));
                box.Image = ico.ToBitmap();
            }
        }
        catch
        {
            // Ignore
        }
    }

    private static string FormatPolicySummary(AppPolicyConfig app)
    {
        var (effW, effH) = app.GetEffectiveScreenSize();
        string sizeLabel = !string.IsNullOrWhiteSpace(app.ScreenSizeOverride) ? $"{effW}x{effH}*" : $"{effW}x{effH}";
        string winMode = app.IsWindowable
            ? (app.DynamicAspectResize ? "Window-able" : "Fixed Ratio")
            : app.PreferredOrientation;

        var tags = new List<string>
        {
            $"📐 {sizeLabel} ({winMode})",
            app.VirtualDisplay ? "🖥 Virtual Display" : "🖥 Primary Display",
            app.BackgroundOnClose ? "🌙 Close->Background" : "✕ Close->Exit"
        };
        if (app.BlockNetwork) tags.Add("🚫 Net Blocked");
        if (app.MuteAudio) tags.Add("🔇 Audio Muted");
        if (app.MuteMic) tags.Add("🎙 Mic Muted");
        if (app.BlockHid) tags.Add("🔒 HID Locked");
        if (app.HostShareEnabled) tags.Add("📁 HOST Share");
        return string.Join("   |   ", tags);
    }

    private static Label CreateBadgeLabel(string text, Point location, int width, Color backColor, Color foreColor)
    {
        return new Label
        {
            Text = text,
            Location = location,
            Size = new Size(width, 24),
            BackColor = backColor,
            ForeColor = foreColor,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold)
        };
    }

    private static Button CreateButton(string text, Color backColor, Point? location = null, Size? size = null)
    {
        var btn = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(4, 0, 4, 0)
        };
        btn.FlatAppearance.BorderSize = 0;
        if (location.HasValue) btn.Location = location.Value;
        if (size.HasValue)
        {
            btn.Size = size.Value;
        }
        else
        {
            btn.AutoSize = true;
            btn.Height = 34;
            btn.Padding = new Padding(8, 2, 8, 2);
        }
        return btn;
    }

    private static void OpenFolderInExplorer(string folderPath)
    {
        Directory.CreateDirectory(folderPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{folderPath}\"",
            UseShellExecute = true
        });
    }

    private void ShowAndActivate()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ShowAndActivate);
            return;
        }
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitService()
    {
        _allowExit = true;
        Close();
    }
}
