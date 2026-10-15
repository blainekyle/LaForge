using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace LaForge.Launcher;

/// <summary>
/// Lightweight, borderless native splash overlay displayed while a LaForge app is
/// bootstrapping the headless Android container ("Preparing <App> for initial launch...")
/// or allocating an isolated virtual display ("Loading <App>...").
/// Displays the target app icon on the left, textual status in the center, and the
/// branded LaForge logo (/img/laforge.0.png) to the side of the textual content.
/// </summary>
public sealed class LaForgeSplashOverlay : Form
{
    private readonly Label _lblHeadline;
    private readonly Label _lblSubtext;
    private readonly Panel _progressTrack;
    private readonly Panel _progressBar;
    private readonly System.Windows.Forms.Timer _animTimer;
    private int _animOffset;

    public LaForgeSplashOverlay(string headlineText, string subText, string? iconPath, WindowConfig? targetWindow = null)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 710;
        Height = 166;
        BackColor = Color.FromArgb(18, 22, 32);
        ForeColor = Color.FromArgb(235, 240, 248);
        DoubleBuffered = true;

        var brandIcon = LoadBrandIcon();
        if (brandIcon != null)
        {
            Icon = brandIcon;
        }

        if (targetWindow != null && targetWindow.Width >= 200 && targetWindow.Height >= 200)
        {
            StartPosition = FormStartPosition.Manual;
            int centerX = targetWindow.X + (targetWindow.Width - Width) / 2;
            int centerY = targetWindow.Y + (targetWindow.Height - Height) / 2;
            Location = new Point(Math.Max(20, centerX), Math.Max(20, centerY));
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }

        var picIcon = new PictureBox
        {
            Size = new Size(64, 64),
            Location = new Point(24, 30),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
            {
                if (iconPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    using var ico = new Icon(iconPath, new Size(64, 64));
                    picIcon.Image = ico.ToBitmap();
                }
                else
                {
                    using var fs = new FileStream(iconPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var raw = Image.FromStream(fs);
                    picIcon.Image = new Bitmap(raw);
                }
            }
            else if (brandIcon != null)
            {
                picIcon.Image = brandIcon.ToBitmap();
            }
        }
        catch
        {
            // Fallback to default icon
        }

        _lblHeadline = new Label
        {
            Text = headlineText,
            Font = new Font("Segoe UI Semibold", 13.0F, FontStyle.Bold),
            ForeColor = Color.FromArgb(240, 246, 255),
            Location = new Point(104, 24),
            Size = new Size(432, 34),
            AutoEllipsis = true
        };

        _lblSubtext = new Label
        {
            Text = subText,
            Font = new Font("Segoe UI", 9.75F, FontStyle.Regular),
            ForeColor = Color.FromArgb(145, 165, 195),
            Location = new Point(104, 60),
            Size = new Size(432, 46),
            AutoEllipsis = true
        };

        // Branded LaForge logo positioned to the right side of the textual content
        var picBrand = new PictureBox
        {
            Size = new Size(142, 104),
            Location = new Point(544, 14),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Image = LoadBrandSplashImage()
        };

        _progressTrack = new Panel
        {
            Location = new Point(24, 130),
            Size = new Size(662, 7),
            BackColor = Color.FromArgb(32, 39, 54)
        };

        _progressBar = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(190, 7),
            BackColor = Color.FromArgb(88, 166, 255)
        };
        _progressTrack.Controls.Add(_progressBar);

        Controls.Add(picIcon);
        Controls.Add(_lblHeadline);
        Controls.Add(_lblSubtext);
        Controls.Add(picBrand);
        Controls.Add(_progressTrack);

        _animTimer = new System.Windows.Forms.Timer { Interval = 25 };
        _animTimer.Tick += (_, _) =>
        {
            _animOffset = (_animOffset + 10) % (_progressTrack.Width + _progressBar.Width);
            _progressBar.Left = _animOffset - _progressBar.Width;
        };

        Shown += (_, _) => _animTimer.Start();
        FormClosed += (_, _) =>
        {
            _animTimer.Stop();
            _animTimer.Dispose();
        };
    }

    public static Icon? LoadBrandIcon()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "img", "laforge.ico"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "img", "laforge.ico")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "img", "laforge.ico")),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaForge", "img", "laforge.ico"),
                @"c:\ag\APKForge\img\laforge.ico"
            };

            foreach (string path in candidates)
            {
                if (File.Exists(path))
                {
                    return new Icon(path);
                }
            }

            using Stream? resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LaForge.Launcher.laforge.ico");
            if (resStream != null)
            {
                return new Icon(resStream);
            }

            if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
            {
                return Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            }
        }
        catch
        {
            // Ignore icon load errors
        }
        return null;
    }

    public static Image? LoadBrandSplashImage()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "img", "laforge_splash.png"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "img", "laforge_splash.png")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "img", "laforge_splash.png")),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaForge", "img", "laforge_splash.png"),
                Path.Combine(baseDir, "img", "laforge.0.png"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "img", "laforge.0.png")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "img", "laforge.0.png")),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaForge", "img", "laforge.0.png"),
                @"c:\ag\APKForge\img\laforge_splash.png",
                @"c:\ag\APKForge\img\laforge.0.png"
            };

            foreach (string path in candidates)
            {
                if (File.Exists(path))
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var raw = Image.FromStream(fs);
                    return new Bitmap(raw);
                }
            }

            using Stream? resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LaForge.Launcher.laforge_splash.png");
            if (resStream != null)
            {
                using var raw = Image.FromStream(resStream);
                return new Bitmap(raw);
            }
        }
        catch
        {
            // Ignore splash image load errors
        }
        return null;
    }

    public static void SyncBrandIconToPath(string targetIcoPath)
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "img", "laforge.ico"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "img", "laforge.ico")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "img", "laforge.ico")),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LaForge", "img", "laforge.ico"),
                @"c:\ag\APKForge\img\laforge.ico"
            };

            foreach (string src in candidates)
            {
                if (File.Exists(src))
                {
                    File.Copy(src, targetIcoPath, overwrite: true);
                    return;
                }
            }

            using Stream? resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LaForge.Launcher.laforge.ico");
            if (resStream != null)
            {
                using var fs = File.Create(targetIcoPath);
                resStream.CopyTo(fs);
            }
        }
        catch
        {
            // Ignore
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var borderPen = new Pen(Color.FromArgb(65, 125, 215), 2f);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawRectangle(borderPen, 1, 1, Width - 2, Height - 2);
    }

    public void UpdateStatus(string headlineText, string? subText = null)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateStatus(headlineText, subText));
            return;
        }
        _lblHeadline.Text = headlineText;
        if (!string.IsNullOrWhiteSpace(subText))
        {
            _lblSubtext.Text = subText;
        }
        Refresh();
    }

    public void DismissSafe()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(DismissSafe);
            return;
        }
        try
        {
            _animTimer.Stop();
            Hide();
            Close();
        }
        catch
        {
            // Ignore
        }
    }
}
