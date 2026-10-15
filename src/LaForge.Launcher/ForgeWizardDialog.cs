using System.Drawing;
using System.Text.Json;

namespace LaForge.Launcher;

/// <summary>
/// "+ Forge New APK..." wizard: pick (or drop) an APK, preview the parsed manifest, choose initial isolation / share toggles,
/// confirm online icon lookup (mandatory confirmation, same wording as the CLI), then forge a standalone bundle.
/// </summary>
public sealed class ForgeWizardDialog : Form
{
    private readonly string _baseDir;
    private ApkMetadata? _meta;
    private string _apkPath = "";
    private bool _busy;

    private readonly TextBox _txtApk = new();
    private readonly TextBox _txtOut = new();
    private readonly Label _lblStatus = new();
    private readonly Label _lblPreview = new();
    private readonly TextBox _log = new();
    private readonly PictureBox _icon = new();
    private readonly Button _btnForge = new();
    private readonly Button _btnOpen = new();
    private readonly CheckBox _chkHost;
    private readonly CheckBox _chkVd;
    private readonly CheckBox _chkBg;
    private readonly CheckBox _chkNet;
    private readonly CheckBox _chkAudio;
    private readonly CheckBox _chkMic;
    private readonly CheckBox _chkHid;
    private readonly CheckBox _chkOnline;
    private string _lastBundleDir = "";

    /// <summary>True once at least one bundle was forged during this dialog's lifetime (caller refreshes the app list).</summary>
    public bool Forged { get; private set; }

    public ForgeWizardDialog(string baseDir)
    {
        _baseDir = baseDir;

        Text = "➕ Forge New APK";
        Size = new Size(780, 800);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(20, 24, 33);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);
        var brandIco = LaForgeSplashOverlay.LoadBrandIcon();
        if (brandIco != null) Icon = brandIco;
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.Copy; }
        };
        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                await LoadApkAsync(files[0]);
            }
        };

        // ---- Step 1: APK
        AddHeading("1. Choose an APK  (browse, or drag a .apk anywhere onto this window)", 14);
        StyleTextBox(_txtApk, new Rectangle(20, 40, 590, 26));
        _txtApk.ReadOnly = true;
        var btnBrowse = MakeButton("Browse…", new Rectangle(620, 38, 120, 30), Color.FromArgb(45, 95, 155));
        btnBrowse.Click += async (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select an Android APK to forge",
                Filter = "Android packages (*.apk)|*.apk|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) { await LoadApkAsync(dlg.FileName); }
        };

        _lblStatus.Location = new Point(20, 72);
        _lblStatus.Size = new Size(720, 20);
        _lblStatus.ForeColor = Color.FromArgb(160, 175, 195);
        _lblStatus.Text = "No APK selected.";

        // ---- Step 2: Preview
        AddHeading("2. Detected app", 100);
        _icon.Location = new Point(20, 128);
        _icon.Size = new Size(72, 72);
        _icon.SizeMode = PictureBoxSizeMode.Zoom;
        _icon.BackColor = Color.FromArgb(28, 33, 44);

        _lblPreview.Location = new Point(104, 126);
        _lblPreview.Size = new Size(640, 100);
        _lblPreview.Font = new Font("Consolas", 9F, FontStyle.Regular);
        _lblPreview.ForeColor = Color.FromArgb(190, 205, 225);
        _lblPreview.Text = "(Select an APK to preview its package, version, launcher activity and window behavior.)";

        // ---- Step 3: Options
        AddHeading("3. Initial isolation & share settings  (editable later with ⚙ on the app card)", 236);
        CheckBox Toggle(string label, bool on, int y, Color color)
        {
            var c = new CheckBox
            {
                Text = label,
                Checked = on,
                Location = new Point(24, y),
                AutoSize = true,
                ForeColor = color,
                Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Regular),
                Cursor = Cursors.Hand
            };
            Controls.Add(c);
            return c;
        }

        _chkHost = Toggle("Enable shared HOST folder (%LOCALAPPDATA%\\LaForge\\HOST ↔ /sdcard/HOST)", true, 264, Color.FromArgb(105, 210, 255));
        _chkVd = Toggle("Launch on isolated virtual display", true, 292, Color.FromArgb(126, 231, 135));
        _chkBg = Toggle("Keep running in background when the window closes", false, 320, Color.FromArgb(255, 205, 95));
        _chkNet = Toggle("Block network access", false, 348, Color.FromArgb(255, 140, 140));
        _chkAudio = Toggle("Mute audio output", false, 376, Color.FromArgb(255, 166, 87));
        _chkMic = Toggle("Mute microphone input", false, 404, Color.FromArgb(255, 166, 87));
        _chkHid = Toggle("Block HID (mouse/keyboard/sensors)", false, 432, Color.FromArgb(210, 168, 255));

        // ---- Step 4: Icon + output
        AddHeading("4. Icon & output", 466);
        _chkOnline = Toggle("Look up the app icon in public open-source app databases (f-droid.org, apt.izzysoft.de)", ApkIconExtractor.LoadConsent() == OnlineIconConsent.Yes, 494, Color.FromArgb(200, 220, 255));
        var lblOnlineNote = new Label
        {
            Text = "Sends only the package name. You will be asked to confirm before anything is contacted. Otherwise the icon comes from inside the APK, or a generated monogram.",
            Location = new Point(44, 518),
            Size = new Size(700, 34),
            ForeColor = Color.FromArgb(140, 155, 175),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular)
        };
        Controls.Add(lblOnlineNote);

        var lblOut = new Label { Text = "Output folder:", Location = new Point(24, 562), AutoSize = true };
        Controls.Add(lblOut);
        StyleTextBox(_txtOut, new Rectangle(120, 558, 490, 26));
        _txtOut.Text = ResolveDefaultOutputDir(baseDir);
        var btnOut = MakeButton("Change…", new Rectangle(620, 556, 120, 30), Color.FromArgb(62, 70, 86));
        btnOut.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Choose where the forged bundle folder is created", SelectedPath = _txtOut.Text };
            if (dlg.ShowDialog(this) == DialogResult.OK) { _txtOut.Text = dlg.SelectedPath; }
        };

        // ---- Forge + log
        _btnForge.Text = "⚒  Forge Bundle";
        StyleButton(_btnForge, new Rectangle(20, 600, 160, 38), Color.FromArgb(35, 134, 54));
        _btnForge.Enabled = false;
        _btnForge.Click += async (_, _) => await ForgeAsync();

        _btnOpen.Text = "📂 Open bundle folder";
        StyleButton(_btnOpen, new Rectangle(190, 600, 170, 38), Color.FromArgb(45, 55, 72));
        _btnOpen.Enabled = false;
        _btnOpen.Click += (_, _) =>
        {
            if (Directory.Exists(_lastBundleDir))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = _lastBundleDir, UseShellExecute = true });
            }
        };

        var btnClose = MakeButton("Close", new Rectangle(620, 600, 120, 38), Color.FromArgb(62, 70, 86));
        btnClose.Click += (_, _) => Close();

        _log.Location = new Point(20, 650);
        _log.Size = new Size(720, 100);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.FromArgb(14, 17, 24);
        _log.ForeColor = Color.FromArgb(170, 220, 180);
        _log.Font = new Font("Consolas", 8.5F, FontStyle.Regular);
        _log.BorderStyle = BorderStyle.FixedSingle;

        Controls.AddRange(new Control[] { _txtApk, btnBrowse, _lblStatus, _icon, _lblPreview, _txtOut, btnOut, _btnForge, _btnOpen, btnClose, _log });
    }

    // ---------------------------------------------------------------------------------------------

    private void AddHeading(string text, int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            Location = new Point(20, y),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(88, 166, 255)
        });
    }

    private static void StyleTextBox(TextBox tb, Rectangle bounds)
    {
        tb.Location = bounds.Location;
        tb.Size = bounds.Size;
        tb.BackColor = Color.FromArgb(28, 33, 44);
        tb.ForeColor = Color.White;
        tb.BorderStyle = BorderStyle.FixedSingle;
    }

    private static void StyleButton(Button b, Rectangle bounds, Color back)
    {
        b.Location = bounds.Location;
        b.Size = bounds.Size;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = back;
        b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Regular);
        b.Cursor = Cursors.Hand;
    }

    private static Button MakeButton(string text, Rectangle bounds, Color back)
    {
        var b = new Button { Text = text };
        StyleButton(b, bounds, back);
        return b;
    }

    private static string ResolveDefaultOutputDir(string baseDir)
    {
        string trimmed = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetFileName(trimmed).Equals("dist", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }
        string candidate = Path.Combine(baseDir, "dist");
        if (Directory.Exists(candidate)) { return candidate; }
        string cwdDist = Path.Combine(Directory.GetCurrentDirectory(), "dist");
        return Directory.Exists(cwdDist) ? cwdDist : candidate;
    }

    private void AppendLog(string message)
    {
        if (IsDisposed) { return; }
        _log.AppendText(message + Environment.NewLine);
    }

    // ---------------------------------------------------------------------------------------------

    private async Task LoadApkAsync(string path)
    {
        if (_busy) { return; }
        if (!path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            _lblStatus.Text = "That is not an .apk file.";
            _lblStatus.ForeColor = Color.FromArgb(255, 123, 114);
            return;
        }

        _busy = true;
        _btnForge.Enabled = false;
        _lblStatus.ForeColor = Color.FromArgb(160, 175, 195);
        _lblStatus.Text = "Reading manifest…";
        try
        {
            ApkMetadata meta = await Task.Run(() => ApkManifestParser.ParseApk(path));
            _meta = meta;
            _apkPath = path;
            _txtApk.Text = path;

            string types = meta.MimeTypes.Count == 0 ? "(none declared)" : string.Join(", ", meta.MimeTypes.Take(6)) + (meta.MimeTypes.Count > 6 ? ", …" : "");
            string abiSummary = meta.NativeAbis.Count == 0
                ? "Pure JVM / Kotlin (Universal — x86_64 Ready)"
                : (meta.IsCompatibleWithX86_64
                    ? $"{string.Join(", ", meta.NativeAbis)} (x86_64 Ready)"
                    : $"{string.Join(", ", meta.NativeAbis)} (⚠ ARM-ONLY — no x86_64 slice)");

            _lblPreview.Text =
                $"Package   : {meta.PackageName}   (v{meta.VersionName}, code {meta.VersionCode})\n" +
                $"Launcher  : {meta.LauncherActivity}\n" +
                $"Window    : {meta.PreferredOrientation}, {(meta.IsWindowable ? "resizable" : "fixed")}, {meta.PreferredScreenSize}\n" +
                $"Native ABI: {abiSummary}\n" +
                $"Opens     : {types}\n" +
                $"Size      : {new FileInfo(path).Length / (1024 * 1024.0):0.0} MB";

            if (meta.IsCompatibleWithX86_64)
            {
                _lblStatus.Text = "Ready to forge.";
                _lblStatus.ForeColor = Color.FromArgb(126, 231, 135);
            }
            else
            {
                _lblStatus.Text = $"⚠ Warning: APK has ARM-only native libraries ({string.Join(", ", meta.NativeAbis)}) and will fail with INSTALL_FAILED_NO_MATCHING_ABIS on x86_64.";
                _lblStatus.ForeColor = Color.FromArgb(255, 180, 80);
            }
            _btnForge.Enabled = true;
        }
        catch (Exception ex)
        {
            _meta = null;
            _lblStatus.Text = $"Could not read that APK: {ex.Message}";
            _lblStatus.ForeColor = Color.FromArgb(255, 123, 114);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ForgeAsync()
    {
        if (_meta == null || _busy) { return; }
        string outDir = _txtOut.Text.Trim();
        if (string.IsNullOrWhiteSpace(outDir))
        {
            MessageBox.Show(this, "Choose an output folder first.", "LaForge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!_meta.IsCompatibleWithX86_64)
        {
            var abiConfirm = MessageBox.Show(
                this,
                $"This APK only contains ARM native libraries ({string.Join(", ", _meta.NativeAbis)}) and does not include an x86_64 or x86 slice.\n\n" +
                "Because the guest Android runtime is x86_64 without ARM binary translation (Major Update 1 roadmap), Android will reject installation with INSTALL_FAILED_NO_MATCHING_ABIS.\n\n" +
                "Forge this bundle anyway?",
                "ARM-Only Native Libraries Detected",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (abiConfirm != DialogResult.Yes)
            {
                return;
            }
        }

        // Mandatory confirmation before any online icon lookup (mirrors the CLI prompt and wording).
        bool allowOnline = _chkOnline.Checked;
        if (allowOnline && ApkIconExtractor.LoadConsent() != OnlineIconConsent.Yes)
        {
            var answer = MessageBox.Show(
                this,
                ApkIconExtractor.ConsentPrompt + "\n\nAllow online icon lookups?",
                "LaForge - Online icon lookup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            allowOnline = answer == DialogResult.Yes;
            ApkIconExtractor.SaveConsent(allowOnline ? OnlineIconConsent.Yes : OnlineIconConsent.No);
            _chkOnline.Checked = allowOnline;
        }
        else if (!allowOnline && ApkIconExtractor.LoadConsent() == OnlineIconConsent.Yes)
        {
            ApkIconExtractor.SaveConsent(OnlineIconConsent.No);
        }

        string existingBundle = Path.Combine(Path.GetFullPath(outDir), _meta.PackageName);
        if (Directory.Exists(existingBundle))
        {
            var replace = MessageBox.Show(
                this,
                $"A bundle for {_meta.PackageName} already exists:\n{existingBundle}\n\nRe-forging refreshes its launcher, payload and config. A custom or previously resolved icon is kept. Continue?",
                "LaForge - Bundle exists",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (replace != DialogResult.Yes) { return; }
        }

        _busy = true;
        _btnForge.Enabled = false;
        _lblStatus.ForeColor = Color.FromArgb(160, 175, 195);
        _lblStatus.Text = "Forging…";
        _log.Clear();
        AppendLog($"Forging {_meta.PackageName} → {outDir}");

        bool enableHost = _chkHost.Checked;
        string apk = _apkPath;
        try
        {
            string bundleDir = await Task.Run(() => ApkPackager.ForgeBundle(
                apk,
                outDir,
                enableHost,
                allowOnline,
                msg =>
                {
                    if (!IsDisposed) { BeginInvoke(new Action(() => AppendLog("  " + msg))); }
                }));

            ApplyInitialPolicy(bundleDir);
            _lastBundleDir = bundleDir;
            Forged = true;
            ShowBundleIcon(bundleDir);
            AppendLog($"Done. Bundle: {bundleDir}");
            _lblStatus.Text = "Forged successfully. It now appears in the Console app list.";
            _lblStatus.ForeColor = Color.FromArgb(126, 231, 135);
            _btnOpen.Enabled = true;
        }
        catch (Exception ex)
        {
            AppendLog("FAILED: " + ex.Message);
            _lblStatus.Text = "Forge failed: " + ex.Message;
            _lblStatus.ForeColor = Color.FromArgb(255, 123, 114);
        }
        finally
        {
            _busy = false;
            _btnForge.Enabled = _meta != null;
        }
    }

    /// <summary>Applies the wizard's toggles to the bundle's per-app policy store (same store the ⚙ modal edits).</summary>
    private void ApplyInitialPolicy(string bundleDir)
    {
        string configPath = Path.Combine(bundleDir, "config.json");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        var config = JsonSerializer.Deserialize<LaForgeConfig>(File.ReadAllText(configPath), options)
                     ?? throw new InvalidOperationException("Forged config.json could not be read.");

        AppPolicyConfig policy = LaForgeAppRegistry.LoadOrCreate(config, bundleDir);
        policy.HostShareEnabled = _chkHost.Checked;
        policy.VirtualDisplay = _chkVd.Checked;
        policy.BackgroundOnClose = _chkBg.Checked;
        policy.BlockNetwork = _chkNet.Checked;
        policy.MuteAudio = _chkAudio.Checked;
        policy.MuteMic = _chkMic.Checked;
        policy.BlockHid = _chkHid.Checked;
        LaForgeAppRegistry.Save(policy);
        AppendLog("  Applied initial isolation settings.");
    }

    private void ShowBundleIcon(string bundleDir)
    {
        try
        {
            string png = Path.Combine(bundleDir, "bridge", "scrcpy.png");
            if (!File.Exists(png)) { return; }
            using var fs = new FileStream(png, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var loaded = Image.FromStream(fs);
            var previous = _icon.Image;
            _icon.Image = new Bitmap(loaded);
            previous?.Dispose();
        }
        catch
        {
            // Preview only.
        }
    }
}
