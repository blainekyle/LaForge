using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LaForge.Installer;

internal static class Program
{
    private static void InstallerEngine_Runtime(string installDir)
    {
        if (!RuntimeSetup.IsInstalled(installDir))
        {
            RuntimeSetup.Run(installDir, Console.WriteLine);
        }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        bool silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("/S", StringComparison.OrdinalIgnoreCase));
        bool runOnExitArg = args.Any(a => a.Equals("--run", StringComparison.OrdinalIgnoreCase));
        string defaultInstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LaForge");

        if (silent)
        {
            try
            {
                InstallerEngine.ExecuteInstall(
                    defaultInstallDir,
                    createStartMenuShortcut: true,
                    createDesktopShortcut: true,
                    registerInWindows: true,
                    Console.WriteLine);

                if (args.Any(a => a.Equals("--with-runtime", StringComparison.OrdinalIgnoreCase)))
                {
                    InstallerEngine_Runtime(defaultInstallDir);
                }

                if (runOnExitArg)
                {
                    string exePath = Path.Combine(defaultInstallDir, "LaForgeConsole.exe");
                    if (File.Exists(exePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            WorkingDirectory = defaultInstallDir,
                            UseShellExecute = true
                        });
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[LaForgeSetup Error] {ex.Message}");
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new SetupWizardForm(defaultInstallDir));
        return 0;
    }
}

public static class InstallerEngine
{
    public static Icon? LoadBrandIcon()
    {
        try
        {
            using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LaForge.Installer.laforge.ico");
            if (s != null) return new Icon(s);
        }
        catch { }
        return null;
    }

    public static Image? LoadBrandSplashImage()
    {
        try
        {
            using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LaForge.Installer.laforge_splash.png");
            if (s != null)
            {
                using var img = Image.FromStream(s);
                return new Bitmap(img);
            }
        }
        catch { }
        return null;
    }

    public static void ExecuteInstall(
        string installDir,
        bool createStartMenuShortcut,
        bool createDesktopShortcut,
        bool registerInWindows,
        Action<string>? log = null)
    {
        installDir = Path.GetFullPath(installDir);
        log?.Invoke($"Target Installation Directory: {installDir}");

        // 1. Gracefully stop everything running out of the target directory (Console, apps, Android VM, adb server)
        try
        {
            var stopFailures = LaForge.Shared.InstallProcessCleanup.StopProcessesUnder(
                new[] { installDir },
                Path.Combine(installDir, "bridge", "adb.exe"),
                log);
            foreach (string failure in stopFailures)
            {
                log?.Invoke($"Warning: could not stop {failure}");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Warning: could not stop running LaForge processes: {ex.Message}");
        }

        Directory.CreateDirectory(installDir);

        // 2. Extract embedded installer_payload.zip into installDir
        using Stream? payloadStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LaForge.Installer.installer_payload.zip");
        if (payloadStream != null)
        {
            log?.Invoke("Extracting LaForge Console, Native Launcher, Display Bridge & GMS Interop payload…");
            using var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                string destPath = Path.GetFullPath(Path.Combine(installDir, entry.FullName));
                if (!destPath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destPath);
                    continue;
                }

                string? parent = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                entry.ExtractToFile(destPath, overwrite: true);
                log?.Invoke($"  Installed: {entry.FullName}");
            }
        }
        else
        {
            throw new InvalidOperationException("Embedded installer payload archive was not found.");
        }

        // 3. The Android emulator + system image are NOT linked or bundled: they are downloaded once on first launch
        //    (RuntimeProvisioner). Earlier installer builds junctioned runtime\emulator to a local SDK, which breaks on any
        //    machine that does not have one; remove such a stale link so first-run provisioning starts clean.
        string targetRuntimeEmulator = Path.Combine(installDir, "runtime", "emulator");
        try
        {
            var emuInfo = new DirectoryInfo(targetRuntimeEmulator);
            if (emuInfo.Exists && emuInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                emuInfo.Delete(recursive: false);
                log?.Invoke("Removed stale emulator link from a previous installer version.");
            }
        }
        catch { }

        // 4. Link existing dist/ bundles via directory junction if installing from a workspace with forged apps
        string targetDistDir = Path.Combine(installDir, "dist");
        string[] candidateDistDirs =
        {
            Path.Combine(AppContext.BaseDirectory, "dist"),
            @"c:\ag\APKForge\dist"
        };
        string? sourceDistDir = candidateDistDirs.FirstOrDefault(d =>
            Directory.Exists(d) && !Path.GetFullPath(d).Equals(Path.GetFullPath(targetDistDir), StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(sourceDistDir) && !Directory.Exists(targetDistDir))
        {
            CreateDirectoryJunction(targetDistDir, sourceDistDir);
            log?.Invoke($"Linked existing Forged App Bundles -> {sourceDistDir}");
        }
        else
        {
            Directory.CreateDirectory(targetDistDir);
        }

        // 5. Initialize %LOCALAPPDATA%\LaForge\config and %LOCALAPPDATA%\LaForge\HOST
        string localLaForge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaForge");
        string configDir = Path.Combine(localLaForge, "config");
        string hostDir = Path.Combine(localLaForge, "HOST");
        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(hostDir);

        string installedIco = Path.Combine(installDir, "img", "laforge.ico");
        if (File.Exists(installedIco))
        {
            File.Copy(installedIco, Path.Combine(configDir, "console.ico"), overwrite: true);
        }
        log?.Invoke($"Initialized configuration & shared HOST directories in {localLaForge}");

        // 6. Create Windows OS Shortcuts (.lnk)
        string targetExe = Path.Combine(installDir, "LaForgeConsole.exe");
        string iconPath = File.Exists(installedIco) ? installedIco : targetExe;

        if (createStartMenuShortcut)
        {
            string startMenuPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string laforgeStartFolder = Path.Combine(startMenuPrograms, "LaForge");
            Directory.CreateDirectory(laforgeStartFolder);

            string startMenuLnk1 = Path.Combine(laforgeStartFolder, "LaForge Console.lnk");
            string startMenuLnk2 = Path.Combine(startMenuPrograms, "LaForge Console.lnk");
            CreateWindowsShortcut(startMenuLnk1, targetExe, installDir, iconPath, "LaForge Console — Multi-Display Android App & Notification Manager");
            CreateWindowsShortcut(startMenuLnk2, targetExe, installDir, iconPath, "LaForge Console — Multi-Display Android App & Notification Manager");
            log?.Invoke($"Created Start Menu shortcut: {startMenuLnk1}");
        }

        if (createDesktopShortcut)
        {
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string desktopLnk = Path.Combine(desktopDir, "LaForge Console.lnk");
            CreateWindowsShortcut(desktopLnk, targetExe, installDir, iconPath, "LaForge Console — Multi-Display Android App & Notification Manager");
            log?.Invoke($"Created Desktop shortcut: {desktopLnk}");
        }

        // 7. Register in Windows Installed Apps (HKCU Uninstall)
        if (registerInWindows)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LaForgeConsole");
                if (key != null)
                {
                    key.SetValue("DisplayName", "LaForge Console");
                    key.SetValue("DisplayVersion", "1.1.2");
                    key.SetValue("Publisher", "LaForge Project");
                    key.SetValue("InstallLocation", installDir);
                    key.SetValue("DisplayIcon", iconPath);
                    key.SetValue("UninstallString", $"\"{targetExe}\" --uninstall");
                    key.SetValue("QuietUninstallString", $"\"{targetExe}\" --uninstall --silent");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
                log?.Invoke("Registered LaForge Console in Windows per-user Installed Apps.");
            }
            catch
            {
                // Non-fatal
            }
        }

        log?.Invoke("Installation completed successfully.");
    }

    private static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(4000);
        }
        catch { }
    }

    public static void CreateWindowsShortcut(
        string shortcutFilePath,
        string targetPath,
        string workingDirectory,
        string iconLocation,
        string description)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;
            dynamic shortcut = shell.CreateShortcut(shortcutFilePath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = iconLocation;
            shortcut.Description = description;
            shortcut.Save();
        }
        catch
        {
            // Fallback via PowerShell if COM registration is restricted
        }
    }
}

/// <summary>
/// Detects and performs the one-time Android runtime download during installation by running the freshly installed
/// LaForgeConsole.exe in its headless provisioning mode (the same code the first-launch prompt uses).
/// </summary>
public static class RuntimeSetup
{
    public static Process? Current { get; private set; }

    private static string DefaultRuntimeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaForge", "runtime");

    private static bool HasRuntimeAt(string root) =>
        File.Exists(Path.Combine(root, "emulator", "emulator.exe")) &&
        File.Exists(Path.Combine(root, "system-images", "android-34", "default", "x86_64", "system.img"));

    /// <summary>True when the emulator, system image and the LaForge VM snapshot are already on this PC.</summary>
    public static bool IsInstalled(string installDir)
    {
        try
        {
            string snapshot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".android", "avd", "LaForge_x86_64_API34.avd", "snapshots", "default_boot", "snapshot.pb");
            return File.Exists(snapshot) &&
                   (HasRuntimeAt(DefaultRuntimeRoot) || HasRuntimeAt(Path.Combine(installDir, "runtime")));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns the provisioner's exit code (0 = ready), or -1 if it could not be started / was stopped.</summary>
    public static int Run(string installDir, Action<string> log)
    {
        string exe = Path.Combine(installDir, "LaForgeConsole.exe");
        if (!File.Exists(exe))
        {
            log("LaForgeConsole.exe not found; skipping runtime download.");
            return -1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--provision-runtime",
            WorkingDirectory = installDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return -1;
        Current = proc;
        try
        {
            proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data.TrimEnd()); };
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data.TrimEnd()); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            while (!proc.WaitForExit(1000))
            {
                // poll: WaitForExit(int) does not wait for the redirected pipes to reach EOF
            }
            Thread.Sleep(300); // let the last output lines flush
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
        finally
        {
            Current = null;
        }
    }

    /// <summary>Stops an in-progress download (resumable later) and shuts down the half-provisioned VM.</summary>
    public static void Stop(string installDir)
    {
        try
        {
            Process? p = Current;
            if (p != null && !p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch { /* ignore */ }
        try
        {
            LaForge.Shared.InstallProcessCleanup.StopProcessesUnder(
                new[] { Path.Combine(installDir, "runtime") },
                Path.Combine(installDir, "bridge", "adb.exe"));
        }
        catch { /* ignore */ }
    }
}

public sealed class SetupWizardForm : Form
{
    private readonly Panel _stageWelcome;
    private readonly Panel _stageProgress;
    private readonly Panel _stageComplete;

    private readonly TextBox _txtInstallDir;
    private readonly CheckBox _chkStartMenu;
    private readonly CheckBox _chkDesktop;
    private readonly CheckBox _chkRegisterApp;
    private readonly CheckBox _chkRuntime;
    private readonly Label _lblRuntimeState;
    private bool _skipRuntimeRequested;
    private bool _runtimeStageActive;

    private readonly ProgressBar _progressBar;
    private readonly TextBox _txtLog;

    private readonly Label _lblCompleteSummary;
    private readonly CheckBox _chkRunOnExit;

    private readonly Button _btnBack;
    private readonly Button _btnNext;
    private readonly Button _btnCancel;

    private int _currentStage = 1; // 1 = Options, 2 = Installing, 3 = Complete
    private bool _installFinished;

    public SetupWizardForm(string defaultInstallDir)
    {
        Text = "LaForge Console Setup";
        Size = new Size(680, 520);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = Color.FromArgb(16, 20, 28);
        ForeColor = Color.FromArgb(235, 240, 248);
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);

        var brandIco = InstallerEngine.LoadBrandIcon();
        if (brandIco != null)
        {
            Icon = brandIco;
        }

        // Top Branded Header Banner (features the splash message + branded image on the side)
        var headerBanner = new Panel
        {
            Dock = DockStyle.Top,
            Height = 116,
            BackColor = Color.FromArgb(22, 27, 38)
        };

        var lblBannerTitle = new Label
        {
            Text = "LaForge Console Setup Wizard",
            Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(88, 166, 255),
            Location = new Point(24, 22),
            AutoSize = true
        };

        var lblBannerSub = new Label
        {
            Text = "Installs LaForge Central Console, Native APK Packager, scrcpy/ADB Bridge,\n" +
                   "and OS Shortcuts to %APPDATA%\\LaForge.",
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(165, 180, 205),
            Location = new Point(26, 56),
            Size = new Size(440, 44)
        };

        var picBannerLogo = new PictureBox
        {
            Size = new Size(156, 98),
            Location = new Point(490, 9),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Image = InstallerEngine.LoadBrandSplashImage()
        };

        headerBanner.Controls.AddRange(new Control[] { lblBannerTitle, lblBannerSub, picBannerLogo });

        // Bottom Navigation Bar
        var bottomBar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            BackColor = Color.FromArgb(22, 27, 38)
        };

        _btnBack = CreateButton("← Back", Color.FromArgb(48, 56, 72), new Point(260, 13), new Size(110, 36));
        _btnBack.Visible = false;

        _btnNext = CreateButton("Install LaForge →", Color.FromArgb(35, 134, 54), new Point(380, 13), new Size(150, 36));
        _btnCancel = CreateButton("Cancel", Color.FromArgb(68, 76, 92), new Point(540, 13), new Size(104, 36));

        _btnNext.Click += async (_, _) => await OnNextClickedAsync();
        _btnCancel.Click += (_, _) =>
        {
            if (_runtimeStageActive)
            {
                _skipRuntimeRequested = true;
                _btnCancel.Enabled = false;
                _txtLog.AppendText("Skipping the runtime download (it will resume on the first app launch)..." + Environment.NewLine);
                string dirToStop = Path.GetFullPath(_txtInstallDir.Text.Trim());
                Task.Run(() => RuntimeSetup.Stop(dirToStop));
            }
            else
            {
                Close();
            }
        };
        FormClosing += (_, _) =>
        {
            if (_runtimeStageActive)
            {
                try { RuntimeSetup.Stop(Path.GetFullPath(_txtInstallDir.Text.Trim())); } catch { /* ignore */ }
            }
        };

        bottomBar.Controls.AddRange(new Control[] { _btnBack, _btnNext, _btnCancel });

        // Stage 1: Welcome & Installation Options
        _stageWelcome = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            AutoScrollMargin = new Size(0, 16),
            Padding = new Padding(26, 18, 26, 18)
        };
        _stageWelcome.MouseEnter += (_, _) => _stageWelcome.Focus();

        var lblDirHeader = new Label
        {
            Text = "1. Destination Folder (%APPDATA%\\LaForge)",
            Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(121, 192, 255),
            Location = new Point(24, 16),
            AutoSize = true
        };

        _txtInstallDir = new TextBox
        {
            Text = defaultInstallDir,
            Location = new Point(26, 44),
            Size = new Size(496, 28),
            BackColor = Color.FromArgb(24, 30, 42),
            ForeColor = Color.FromArgb(255, 225, 130),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9.5F, FontStyle.Bold)
        };

        var btnBrowse = CreateButton("Browse…", Color.FromArgb(52, 62, 82), new Point(532, 42), new Size(106, 30));
        btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select installation folder for LaForge Console",
                SelectedPath = _txtInstallDir.Text
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _txtInstallDir.Text = dlg.SelectedPath;
            }
        };

        var lblShortcutsHeader = new Label
        {
            Text = "2. Operating System Shortcuts & Integration",
            Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(121, 192, 255),
            Location = new Point(24, 92),
            AutoSize = true
        };

        _chkStartMenu = new CheckBox
        {
            Text = "Create Start Menu Shortcut  (Programs → LaForge → LaForge Console)",
            Checked = true,
            Location = new Point(28, 124),
            AutoSize = true,
            ForeColor = Color.FromArgb(126, 231, 135),
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };

        _chkDesktop = new CheckBox
        {
            Text = "Create Desktop Shortcut  (LaForge Console.lnk)",
            Checked = true,
            Location = new Point(28, 156),
            AutoSize = true,
            ForeColor = Color.FromArgb(126, 231, 135),
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };

        _chkRegisterApp = new CheckBox
        {
            Text = "Register LaForge Console in Windows Installed Apps (per-user)",
            Checked = true,
            Location = new Point(28, 188),
            AutoSize = true,
            ForeColor = Color.FromArgb(200, 215, 235),
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };

        var lblRuntimeHeader = new Label
        {
            Text = "3. Android Runtime (emulator + Android 14 image)",
            Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(121, 192, 255),
            Location = new Point(24, 218),
            AutoSize = true
        };

        _chkRuntime = new CheckBox
        {
            Text = "Download and set up the Android runtime now  (about 1.2 GB, needed before the first app can run)",
            Checked = true,
            Location = new Point(28, 246),
            AutoSize = true,
            ForeColor = Color.FromArgb(126, 231, 135),
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };

        _lblRuntimeState = new Label
        {
            Text = "",
            Location = new Point(48, 270),
            Size = new Size(590, 20),
            ForeColor = Color.FromArgb(150, 168, 192),
            Font = new Font("Segoe UI", 8.75F, FontStyle.Regular)
        };

        void RefreshRuntimeDetection()
        {
            string dir;
            try { dir = Path.GetFullPath(_txtInstallDir.Text.Trim()); }
            catch { dir = _txtInstallDir.Text.Trim(); }
            bool present = RuntimeSetup.IsInstalled(dir);
            _chkRuntime.Checked = !present;
            _lblRuntimeState.Text = present
                ? "Android runtime already found on this PC — nothing to download (tick the box to re-check anyway)."
                : "Not found on this PC. Skip this and LaForge will offer to download it the first time you launch an app.";
        }
        RefreshRuntimeDetection();
        _txtInstallDir.Leave += (_, _) => RefreshRuntimeDetection();

        var lblInfoBox = new Label
        {
            Text = "Installs into your user profile (%APPDATA%\\LaForge). No administrator elevation is required.\n" +
                   "Click 'Install LaForge →' to begin.",
            Location = new Point(26, 298),
            Size = new Size(610, 40),
            ForeColor = Color.FromArgb(150, 168, 192),
            Font = new Font("Segoe UI", 9F, FontStyle.Regular)
        };

        _stageWelcome.Controls.AddRange(new Control[]
        {
            lblDirHeader, _txtInstallDir, btnBrowse,
            lblShortcutsHeader, _chkStartMenu, _chkDesktop, _chkRegisterApp,
            lblRuntimeHeader, _chkRuntime, _lblRuntimeState, lblInfoBox
        });

        // Stage 2: Installing Progress
        _stageProgress = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            AutoScrollMargin = new Size(0, 16),
            Visible = false
        };

        var lblInstallingTitle = new Label
        {
            Text = "Installing LaForge Console…",
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
            ForeColor = Color.FromArgb(121, 192, 255),
            Location = new Point(24, 16),
            AutoSize = true
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(24, 46),
            Size = new Size(614, 22),
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 25
        };

        _txtLog = new TextBox
        {
            Location = new Point(24, 80),
            Size = new Size(614, 198),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(12, 15, 22),
            ForeColor = Color.FromArgb(126, 231, 135),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.75F, FontStyle.Regular)
        };

        _stageProgress.Controls.AddRange(new Control[] { lblInstallingTitle, _progressBar, _txtLog });

        // Stage 3: Final Completion Stage (with Run on Exit option)
        _stageComplete = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            AutoScrollMargin = new Size(0, 16),
            Visible = false
        };

        var lblDoneTitle = new Label
        {
            Text = "✓ LaForge Console Setup Completed!",
            Font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold),
            ForeColor = Color.FromArgb(126, 231, 135),
            Location = new Point(24, 22),
            AutoSize = true
        };

        _lblCompleteSummary = new Label
        {
            Text = "",
            Font = new Font("Consolas", 9.25F, FontStyle.Regular),
            ForeColor = Color.FromArgb(195, 210, 230),
            Location = new Point(26, 62),
            Size = new Size(610, 120)
        };

        var pnlRunOnExitCard = new Panel
        {
            Location = new Point(24, 194),
            Size = new Size(614, 58),
            BackColor = Color.FromArgb(24, 32, 46),
            BorderStyle = BorderStyle.FixedSingle
        };

        _chkRunOnExit = new CheckBox
        {
            Text = "🚀 Launch LaForge Console when setup exits",
            Checked = true,
            Location = new Point(18, 16),
            AutoSize = true,
            ForeColor = Color.FromArgb(126, 231, 135),
            Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        pnlRunOnExitCard.Controls.Add(_chkRunOnExit);

        _stageComplete.Controls.AddRange(new Control[] { lblDoneTitle, _lblCompleteSummary, pnlRunOnExitCard });

        Controls.Add(_stageComplete);
        Controls.Add(_stageProgress);
        Controls.Add(_stageWelcome);
        Controls.Add(bottomBar);
        Controls.Add(headerBanner);
    }

    private async Task OnNextClickedAsync()
    {
        if (_currentStage == 1)
        {
            string targetDir = _txtInstallDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                MessageBox.Show(this, "Please specify a valid destination folder.", "LaForge Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _currentStage = 2;
            _stageWelcome.Visible = false;
            _stageProgress.Visible = true;
            _btnNext.Enabled = false;
            _btnCancel.Enabled = false;

            bool startMenu = _chkStartMenu.Checked;
            bool desktop = _chkDesktop.Checked;
            bool regApp = _chkRegisterApp.Checked;
            bool installRuntime = _chkRuntime.Checked;

            try
            {
                await Task.Run(() =>
                {
                    InstallerEngine.ExecuteInstall(
                        targetDir,
                        startMenu,
                        desktop,
                        regApp,
                        msg =>
                        {
                            if (!IsDisposed)
                            {
                                BeginInvoke(() => _txtLog.AppendText(msg + Environment.NewLine));
                            }
                        });
                });

                string fullInstallDir = Path.GetFullPath(targetDir);
                string runtimeOutcome = installRuntime ? "" : "Not downloaded (offered on first app launch)";
                if (installRuntime)
                {
                    _txtLog.AppendText(Environment.NewLine + "--- Downloading and setting up the Android runtime (one time) ---" + Environment.NewLine);
                    _runtimeStageActive = true;
                    _btnCancel.Text = "Skip download";
                    _btnCancel.Enabled = true;

                    int code = await Task.Run(() => RuntimeSetup.Run(fullInstallDir, msg =>
                    {
                        if (!IsDisposed)
                        {
                            BeginInvoke(() => _txtLog.AppendText(msg + Environment.NewLine));
                        }
                    }));

                    _runtimeStageActive = false;
                    _btnCancel.Enabled = false;
                    runtimeOutcome = _skipRuntimeRequested
                        ? "Skipped (offered on first app launch)"
                        : code == 0
                            ? "Ready"
                            : "Not finished (resumes on first app launch)";
                }

                _installFinished = true;
                _progressBar.Style = ProgressBarStyle.Blocks;
                _progressBar.Value = 100;

                _currentStage = 3;
                _stageProgress.Visible = false;
                _stageComplete.Visible = true;

                _lblCompleteSummary.Text =
                    $"Installed Location : {fullInstallDir}\n" +
                    $"Main Executable    : {Path.Combine(fullInstallDir, "LaForgeConsole.exe")}\n" +
                    $"Start Menu Shortcut: {(startMenu ? "Created (Programs\\LaForge\\LaForge Console.lnk)" : "Skipped")}\n" +
                    $"Desktop Shortcut   : {(desktop ? "Created (Desktop\\LaForge Console.lnk)" : "Skipped")}\n" +
                    $"Android Runtime    : {runtimeOutcome}\n" +
                    $"Config & HOST Dir  : %LOCALAPPDATA%\\LaForge";

                _btnNext.Text = "Finish";
                _btnNext.Enabled = true;
                _btnCancel.Visible = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Installation failed:\n{ex.Message}", "LaForge Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _currentStage = 1;
                _stageProgress.Visible = false;
                _stageWelcome.Visible = true;
                _btnNext.Enabled = true;
                _btnCancel.Enabled = true;
            }
        }
        else if (_currentStage == 3 && _installFinished)
        {
            if (_chkRunOnExit.Checked)
            {
                string exePath = Path.Combine(Path.GetFullPath(_txtInstallDir.Text.Trim()), "LaForgeConsole.exe");
                if (File.Exists(exePath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
            }
            Close();
        }
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
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }
}
