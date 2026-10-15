using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace LaForge.Launcher;

/// <summary>
/// Generates bespoke 256x256 PNG and Windows .ICO icons for forged APK bundles
/// so both the LaForge tray/console and the scrcpy SDL3 window display a distinct app icon.
/// </summary>
public static class AppIconGenerator
{
    /// <summary>
    /// Produces app.ico + bridge/scrcpy.png for a bundle. Source order: cached/custom icon already in the bundle,
    /// public database (only if <paramref name="allowOnlineIcons"/>), raster icon inside the APK, then the monogram.
    /// A marker file (bridge/icon_source.txt) records the source so rebuilds never re-fetch and never clobber a custom icon.
    /// </summary>
    public static (string IcoPath, string PngPath) GenerateBundleIcons(
        string packageName,
        string appSlug,
        string bundleDir,
        string? apkPath = null,
        bool allowOnlineIcons = false,
        Action<string>? log = null)
    {
        string icoPath = Path.Combine(bundleDir, "app.ico");
        string pngPath = Path.Combine(bundleDir, "bridge", "scrcpy.png");
        string markerPath = Path.Combine(bundleDir, "bridge", IconSourceMarkerFile);

        string existing = File.Exists(markerPath) ? File.ReadAllText(markerPath).Trim() : string.Empty;
        bool realIconCached = existing == "custom" || existing == "embedded" || existing.StartsWith("online:", StringComparison.Ordinal);
        if (realIconCached && File.Exists(icoPath) && File.Exists(pngPath))
        {
            log?.Invoke($"[Icon] Reusing cached icon ({existing}).");
            return (icoPath, pngPath);
        }

        Bitmap? resolved = null;
        string source = "monogram";
        if (!string.IsNullOrWhiteSpace(apkPath) && File.Exists(apkPath))
        {
            var result = ApkIconExtractor.Resolve(packageName, apkPath, allowOnlineIcons, log);
            resolved = result.Bitmap;
            if (resolved != null) source = result.Source;
        }

        if (resolved != null)
        {
            using (resolved)
            {
                SaveIconFiles(resolved, icoPath, pngPath);
            }
        }
        else
        {
            log?.Invoke("[Icon] Falling back to generated monogram icon.");
            EnsureAppIcons(packageName, appSlug, icoPath, pngPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, source);
        return (icoPath, pngPath);
    }

    /// <summary>Replaces a bundle's icon with a user-chosen image (any format GDI+/WIC can read) and locks it against rebuild overwrites.</summary>
    public static (string IcoPath, string PngPath) ApplyCustomIcon(string bundleDir, string imageFilePath)
    {
        byte[] data = File.ReadAllBytes(imageFilePath);
        using Bitmap? bmp = ApkIconExtractor.DecodeImage(data, Path.GetExtension(imageFilePath))
            ?? throw new InvalidOperationException("That file could not be read as an image.");

        string icoPath = Path.Combine(bundleDir, "app.ico");
        string pngPath = Path.Combine(bundleDir, "bridge", "scrcpy.png");
        SaveIconFiles(bmp, icoPath, pngPath);
        Directory.CreateDirectory(Path.Combine(bundleDir, "bridge"));
        File.WriteAllText(Path.Combine(bundleDir, "bridge", IconSourceMarkerFile), "custom");
        return (icoPath, pngPath);
    }

    public const string IconSourceMarkerFile = "icon_source.txt";

    public static void SaveIconFiles(Bitmap bmp, string icoPath, string pngPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
        bmp.Save(pngPath, ImageFormat.Png);
        Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
        SaveBitmapAsIco(bmp, icoPath);
    }
    public static void EnsureAppIcons(string packageName, string appSlug, string icoPath, string pngPath)
    {
        using var bmp = RenderAppIconBitmap(packageName, appSlug, 256);

        // 1. Save PNG icon
        Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
        bmp.Save(pngPath, ImageFormat.Png);

        // 2. Save Windows Vista+ PNG-encoded .ICO
        Directory.CreateDirectory(Path.GetDirectoryName(icoPath)!);
        SaveBitmapAsIco(bmp, icoPath);
    }

    private static Bitmap RenderAppIconBitmap(string packageName, string appSlug, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var (primary, secondary) = PickThemeColors(packageName);

        // Draw rounded squircle background
        var rect = new Rectangle(12, 12, size - 24, size - 24);
        using (var path = CreateRoundedRectPath(rect, 56))
        using (var brush = new LinearGradientBrush(rect, primary, secondary, LinearGradientMode.ForwardDiagonal))
        using (var borderPen = new Pen(Color.FromArgb(90, 255, 255, 255), 4f))
        {
            g.FillPath(brush, path);
            g.DrawPath(borderPen, path);
        }

        // Draw monogram initials
        string initials = DeriveInitials(packageName, appSlug);
        using var font = new Font("Segoe UI", size * 0.36f, FontStyle.Bold, GraphicsUnit.Pixel);
        var strFormat = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        using var shadowBrush = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
        g.DrawString(initials, font, shadowBrush, new RectangleF(4, 10, size, size), strFormat);
        g.DrawString(initials, font, Brushes.White, new RectangleF(0, 6, size, size), strFormat);

        return bmp;
    }

    private static (Color Primary, Color Secondary) PickThemeColors(string packageName)
    {
        if (packageName.Contains("cromite", StringComparison.OrdinalIgnoreCase) ||
            packageName.Contains("chrome", StringComparison.OrdinalIgnoreCase))
        {
            return (Color.FromArgb(0, 150, 214), Color.FromArgb(14, 78, 150));
        }
        if (packageName.Contains("camera", StringComparison.OrdinalIgnoreCase))
        {
            return (Color.FromArgb(20, 165, 155), Color.FromArgb(12, 95, 92));
        }
        if (packageName.Contains("filemanager", StringComparison.OrdinalIgnoreCase))
        {
            return (Color.FromArgb(46, 160, 67), Color.FromArgb(26, 105, 42));
        }
        if (packageName.Contains("gallery", StringComparison.OrdinalIgnoreCase))
        {
            return (Color.FromArgb(31, 111, 235), Color.FromArgb(17, 68, 150));
        }
        if (packageName.Contains("music", StringComparison.OrdinalIgnoreCase))
        {
            return (Color.FromArgb(210, 105, 30), Color.FromArgb(145, 56, 14));
        }
        if (packageName.Contains("voice", StringComparison.OrdinalIgnoreCase) ||
            packageName.Contains("recorder", StringComparison.OrdinalIgnoreCase))
        {
            return (Color.FromArgb(190, 45, 75), Color.FromArgb(120, 20, 45));
        }
        return (Color.FromArgb(110, 64, 201), Color.FromArgb(65, 35, 130));
    }

    private static string DeriveInitials(string packageName, string appSlug)
    {
        if (packageName.Contains("cromite", StringComparison.OrdinalIgnoreCase)) return "CR";
        if (packageName.Contains("camera", StringComparison.OrdinalIgnoreCase)) return "CA";
        if (packageName.Contains("filemanager", StringComparison.OrdinalIgnoreCase)) return "FM";
        if (packageName.Contains("gallery", StringComparison.OrdinalIgnoreCase)) return "GA";
        if (packageName.Contains("music", StringComparison.OrdinalIgnoreCase)) return "MP";
        if (packageName.Contains("voice", StringComparison.OrdinalIgnoreCase)) return "VR";

        string[] words = appSlug.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
        {
            return $"{char.ToUpperInvariant(words[^2][0])}{char.ToUpperInvariant(words[^1][0])}";
        }
        return appSlug.Length >= 2 ? appSlug[..2].ToUpperInvariant() : "LF";
    }

    private static GraphicsPath CreateRoundedRectPath(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void SaveBitmapAsIco(Bitmap bmp, string icoPath)
    {
        using var pngStream = new MemoryStream();
        bmp.Save(pngStream, ImageFormat.Png);
        byte[] pngBytes = pngStream.ToArray();

        using var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // ICONDIR header (6 bytes)
        bw.Write((ushort)0); // Reserved
        bw.Write((ushort)1); // Type: 1 = ICO
        bw.Write((ushort)1); // Count: 1 image

        // ICONDIRENTRY (16 bytes)
        bw.Write((byte)(bmp.Width >= 256 ? 0 : bmp.Width));
        bw.Write((byte)(bmp.Height >= 256 ? 0 : bmp.Height));
        bw.Write((byte)0);   // Color palette count
        bw.Write((byte)0);   // Reserved
        bw.Write((ushort)1); // Color planes
        bw.Write((ushort)32);// Bits per pixel
        bw.Write((uint)pngBytes.Length);
        bw.Write((uint)22);  // Offset (6 + 16)

        bw.Write(pngBytes);
    }
}
