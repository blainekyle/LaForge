using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LaForge.Launcher;

/// <summary>
/// Lightweight, borderless native splash overlay displayed while a LaForge app is
/// bootstrapping the headless Android container ("Preparing <App> for initial launch...")
/// or allocating an isolated virtual display ("Loading <App>...").
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
        Width = 460;
        Height = 132;
        BackColor = Color.FromArgb(18, 22, 32);
        ForeColor = Color.FromArgb(235, 240, 248);
        DoubleBuffered = true;

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
            Size = new Size(56, 56),
            Location = new Point(22, 26),
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
                    picIcon.Image = Image.FromFile(iconPath);
                }
            }
        }
        catch
        {
            // Fallback to default icon
        }

        _lblHeadline = new Label
        {
            Text = headlineText,
            Font = new Font("Segoe UI Semibold", 12.0F, FontStyle.Bold),
            ForeColor = Color.FromArgb(240, 246, 255),
            Location = new Point(92, 24),
            Size = new Size(348, 30),
            AutoEllipsis = true
        };

        _lblSubtext = new Label
        {
            Text = subText,
            Font = new Font("Segoe UI", 9.25F, FontStyle.Regular),
            ForeColor = Color.FromArgb(145, 165, 195),
            Location = new Point(92, 56),
            Size = new Size(348, 24),
            AutoEllipsis = true
        };

        _progressTrack = new Panel
        {
            Location = new Point(22, 98),
            Size = new Size(416, 6),
            BackColor = Color.FromArgb(32, 39, 54)
        };

        _progressBar = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(140, 6),
            BackColor = Color.FromArgb(88, 166, 255)
        };
        _progressTrack.Controls.Add(_progressBar);

        Controls.Add(picIcon);
        Controls.Add(_lblHeadline);
        Controls.Add(_lblSubtext);
        Controls.Add(_progressTrack);

        _animTimer = new System.Windows.Forms.Timer { Interval = 25 };
        _animTimer.Tick += (_, _) =>
        {
            _animOffset = (_animOffset + 9) % (_progressTrack.Width + _progressBar.Width);
            _progressBar.Left = _animOffset - _progressBar.Width;
        };

        Shown += (_, _) => _animTimer.Start();
        FormClosed += (_, _) =>
        {
            _animTimer.Stop();
            _animTimer.Dispose();
        };
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
