using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace LaForge.Launcher;

/// <summary>User consent for contacting public icon databases (stored per-user; default is "not asked yet").</summary>
public enum OnlineIconConsent
{
    Unset,
    Yes,
    No
}

/// <summary>
/// Resolves a real application icon for a forged APK. Order (first hit wins):
///   1. Public open-source app databases (F-Droid, IzzyOnDroid) - ONLY if the user has explicitly consented.
///   2. A raster launcher icon packed inside the APK (located with aapt2; PNG/JPG via GDI+, WebP via WIC).
/// If neither yields an icon the caller falls back to the generated two-letter monogram.
/// </summary>
public static class ApkIconExtractor
{
    public const int OutputSize = 256;

    private static readonly string[] DefaultSources =
    {
        "https://f-droid.org/repo/{pkg}/en-US/icon.png",
        "https://apt.izzysoft.de/fdroid/repo/{pkg}/en-US/icon.png",
        "fdroid-api"
    };

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LaForge-IconLookup/1.0");
        return client;
    }

    // ---------------------------------------------------------------------------------------------
    // Consent + source configuration (%LOCALAPPDATA%\LaForge\config\icon_sources.conf)
    // ---------------------------------------------------------------------------------------------

    private static string SettingsPath => Path.Combine(LaForgeAppRegistry.GetConfigDirectory(), "icon_sources.conf");

    public static OnlineIconConsent LoadConsent()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                foreach (string raw in File.ReadAllLines(SettingsPath))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("online_icons=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring("online_icons=".Length).Trim();
                        if (v.Equals("yes", StringComparison.OrdinalIgnoreCase)) return OnlineIconConsent.Yes;
                        if (v.Equals("no", StringComparison.OrdinalIgnoreCase)) return OnlineIconConsent.No;
                    }
                }
            }
        }
        catch
        {
            // Treat unreadable settings as "not asked".
        }

        return OnlineIconConsent.Unset;
    }

    public static void SaveConsent(OnlineIconConsent consent)
    {
        var lines = new List<string>();
        try
        {
            if (File.Exists(SettingsPath))
            {
                // Preserve user-added "source=" lines; only the consent line is rewritten.
                lines.AddRange(File.ReadAllLines(SettingsPath)
                    .Where(l => !l.Trim().StartsWith("online_icons=", StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch
        {
            // Ignore and rewrite.
        }

        lines.Insert(0, $"online_icons={(consent == OnlineIconConsent.Yes ? "yes" : consent == OnlineIconConsent.No ? "no" : "unset")}");
        File.WriteAllLines(SettingsPath, lines);
    }

    /// <summary>The exact wording shown for the mandatory confirmation (CLI now, GUI builder later).</summary>
    public const string ConsentPrompt =
        "LaForge can look up real app icons from public open-source app databases " +
        "(f-droid.org and apt.izzysoft.de). This sends the app's package name over the internet. " +
        "If you decline, icons come from inside the APK or a generated monogram.";

    private static List<string> LoadSources()
    {
        var custom = new List<string>();
        try
        {
            if (File.Exists(SettingsPath))
            {
                foreach (string raw in File.ReadAllLines(SettingsPath))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("source=", StringComparison.OrdinalIgnoreCase))
                    {
                        string src = line.Substring("source=".Length).Trim();
                        if (src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && src.Contains("{pkg}"))
                        {
                            custom.Add(src);
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore and use defaults.
        }

        return custom.Count > 0 ? custom : DefaultSources.ToList();
    }

    // ---------------------------------------------------------------------------------------------
    // Public entry point
    // ---------------------------------------------------------------------------------------------

    /// <summary>Returns a 256x256 icon bitmap and a label describing its source, or (null, "") if nothing was found.</summary>
    public static (Bitmap? Bitmap, string Source) Resolve(string packageName, string apkPath, bool allowOnline, Action<string>? log = null)
    {
        if (allowOnline)
        {
            var (online, host) = TryOnline(packageName, log);
            if (online != null)
            {
                return (online, "online:" + host);
            }
        }
        else
        {
            log?.Invoke("[Icon] Online icon lookup not enabled; skipping public databases.");
        }

        Bitmap? embedded = TryEmbeddedRaster(apkPath, log);
        if (embedded != null)
        {
            return (embedded, "embedded");
        }

        return (null, string.Empty);
    }

    // ---------------------------------------------------------------------------------------------
    // 1. Public databases
    // ---------------------------------------------------------------------------------------------

    private static (Bitmap? Bitmap, string Host) TryOnline(string packageName, Action<string>? log)
    {
        // Package names are restricted to [A-Za-z0-9._] so they are safe to place in a URL path.
        if (!Regex.IsMatch(packageName, @"^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)+$"))
        {
            return (null, string.Empty);
        }

        foreach (string source in LoadSources())
        {
            try
            {
                if (source == "fdroid-api")
                {
                    string json = Http.GetStringAsync($"https://f-droid.org/api/v1/packages/{packageName}").GetAwaiter().GetResult();
                    var m = Regex.Match(json, "\"suggestedVersionCode\"\\s*:\\s*(\\d+)");
                    if (!m.Success) continue;
                    string url = $"https://f-droid.org/repo/icons-640/{packageName}.{m.Groups[1].Value}.png";
                    Bitmap? bmp = DownloadAndDecode(url);
                    if (bmp != null)
                    {
                        log?.Invoke($"[Icon] Found icon via f-droid.org (icons-640).");
                        return (bmp, "f-droid.org");
                    }
                }
                else
                {
                    string url = source.Replace("{pkg}", packageName);
                    Bitmap? bmp = DownloadAndDecode(url);
                    if (bmp != null)
                    {
                        string host = new Uri(url).Host;
                        log?.Invoke($"[Icon] Found icon via {host}.");
                        return (bmp, host);
                    }
                }
            }
            catch
            {
                // Offline / 404 / timeout: silently try the next source.
            }
        }

        log?.Invoke("[Icon] No public database entry found.");
        return (null, string.Empty);
    }

    private static Bitmap? DownloadAndDecode(string url)
    {
        using var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode) return null;
        if (response.Content.Headers.ContentLength is long len && len > 2 * 1024 * 1024) return null;

        byte[] data = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (data.Length == 0 || data.Length > 2 * 1024 * 1024) return null;
        return DecodeImage(data, ".png");
    }

    // ---------------------------------------------------------------------------------------------
    // 2. Raster icon embedded in the APK
    // ---------------------------------------------------------------------------------------------

    private static Bitmap? TryEmbeddedRaster(string apkPath, Action<string>? log)
    {
        try
        {
            string? aapt2 = FindAapt2();
            if (aapt2 == null)
            {
                log?.Invoke("[Icon] aapt2 not found (runtime/build-tools); cannot read embedded icon.");
                return null;
            }

            string badging = RunTool(aapt2, new[] { "dump", "badging", apkPath });
            string? declared = PickDeclaredIconPath(badging);
            if (declared == null)
            {
                log?.Invoke("[Icon] APK declares no application icon.");
                return null;
            }

            string? rasterPath = IsRaster(declared) ? declared : null;
            if (rasterPath == null)
            {
                // Adaptive/XML icon: the same resource usually also carries legacy raster files at several densities.
                string resources = RunTool(aapt2, new[] { "dump", "resources", apkPath });
                rasterPath = PickBestRasterForResourceContaining(resources, declared);
            }

            if (rasterPath == null)
            {
                log?.Invoke($"[Icon] Declared icon '{declared}' is vector-only; no raster available.");
                return null;
            }

            using var zip = ZipFile.OpenRead(apkPath);
            ZipArchiveEntry? entry = zip.GetEntry(rasterPath);
            if (entry == null) return null;
            using var ms = new MemoryStream();
            using (Stream s = entry.Open()) { s.CopyTo(ms); }

            Bitmap? bmp = DecodeImage(ms.ToArray(), Path.GetExtension(rasterPath));
            if (bmp != null)
            {
                log?.Invoke($"[Icon] Using embedded icon {rasterPath}.");
            }
            else
            {
                log?.Invoke($"[Icon] Could not decode {rasterPath} (missing codec?).");
            }

            return bmp;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[Icon] Embedded icon lookup failed: {ex.Message}");
            return null;
        }
    }

    private static bool IsRaster(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".webp" or ".jpg" or ".jpeg";
    }

    /// <summary>Chooses the highest-density "application-icon-NNN" path declared in `aapt2 dump badging`.</summary>
    internal static string? PickDeclaredIconPath(string badging)
    {
        string? best = null;
        int bestDensity = -1;
        foreach (Match m in Regex.Matches(badging, @"application-icon-(?<d>\d+):'(?<p>[^']+)'"))
        {
            int density = int.Parse(m.Groups["d"].Value);
            if (density >= 65534) density = 1; // anydpi / nodpi markers rank lowest
            if (density > bestDensity)
            {
                bestDensity = density;
                best = m.Groups["p"].Value;
            }
        }

        if (best != null) return best;
        Match fallback = Regex.Match(badging, @"application: [^\r\n]*icon='(?<p>[^']+)'");
        return fallback.Success ? fallback.Groups["p"].Value : null;
    }

    /// <summary>Finds the resource block listing <paramref name="declaredPath"/> and returns its largest raster file.</summary>
    internal static string? PickBestRasterForResourceContaining(string resourcesDump, string declaredPath)
    {
        var block = new List<(string Cfg, string Path)>();
        string? best = null;
        int bestScore = -1;

        void Flush()
        {
            if (block.Any(b => b.Path.Equals(declaredPath, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var (cfg, path) in block)
                {
                    if (!IsRaster(path)) continue;
                    int score = DensityScore(cfg);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = path;
                    }
                }
            }

            block.Clear();
        }

        foreach (string line in resourcesDump.Split('\n'))
        {
            if (Regex.IsMatch(line, @"^\s*resource 0x[0-9a-fA-F]+ "))
            {
                Flush();
                continue;
            }

            Match m = Regex.Match(line, @"^\s*\((?<cfg>[^)]*)\)\s+\(file\)\s+(?<path>\S+)");
            if (m.Success)
            {
                block.Add((m.Groups["cfg"].Value, m.Groups["path"].Value));
            }
        }

        Flush();
        return best;
    }

    private static int DensityScore(string cfg)
    {
        foreach (string token in cfg.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token)
            {
                case "xxxhdpi": return 640;
                case "xxhdpi": return 480;
                case "xhdpi": return 320;
                case "hdpi": return 240;
                case "tvdpi": return 213;
                case "mdpi": return 160;
                case "ldpi": return 120;
            }
        }

        return 100; // default / unqualified
    }

    // ---------------------------------------------------------------------------------------------
    // Decode / normalize helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>Decodes PNG/JPG with GDI+, WebP with WIC (needs a WebP codec; returns null if unavailable).</summary>
    public static Bitmap? DecodeImage(byte[] data, string extension)
    {
        try
        {
            if (!extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                using var ms = new MemoryStream(data);
                using var img = new Bitmap(ms);
                return NormalizeToSquare(img);
            }

            return DecodeWithWic(data);
        }
        catch
        {
            // GDI+ may still fail on a mislabeled file; try WIC as a last resort.
            try { return DecodeWithWic(data); } catch { return null; }
        }
    }

    private static Bitmap? DecodeWithWic(byte[] data)
    {
        using var input = new MemoryStream(data);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
            input,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(decoder.Frames[0]);
        using var pngStream = new MemoryStream();
        encoder.Save(pngStream);
        pngStream.Position = 0;
        using var img = new Bitmap(pngStream);
        return NormalizeToSquare(img);
    }

    /// <summary>Scales an image to fit a transparent 256x256 canvas, preserving aspect ratio and alpha.</summary>
    public static Bitmap NormalizeToSquare(Image src)
    {
        var dest = new Bitmap(OutputSize, OutputSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dest);
        g.Clear(Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float scale = Math.Min((float)OutputSize / src.Width, (float)OutputSize / src.Height);
        float w = src.Width * scale;
        float h = src.Height * scale;
        g.DrawImage(src, (OutputSize - w) / 2f, (OutputSize - h) / 2f, w, h);
        return dest;
    }

    // ---------------------------------------------------------------------------------------------
    // Tooling
    // ---------------------------------------------------------------------------------------------

    private static string? FindAapt2()
    {
        var starts = new List<string> { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
        foreach (string start in starts)
        {
            string? dir = Path.GetFullPath(start);
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                string buildTools = Path.Combine(dir, "runtime", "build-tools");
                if (Directory.Exists(buildTools))
                {
                    string? found = Directory.GetDirectories(buildTools)
                        .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                        .Select(d => Path.Combine(d, "aapt2.exe"))
                        .FirstOrDefault(File.Exists);
                    if (found != null) return found;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }

        return null;
    }

    private static string RunTool(string exe, IEnumerable<string> args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {exe}");
        var errTask = proc.StandardError.ReadToEndAsync();
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60000);
        errTask.GetAwaiter().GetResult();
        return output;
    }
}
