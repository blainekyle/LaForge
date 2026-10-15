using System.IO.Compression;
using System.Text;

namespace LaForge.Launcher;

public sealed class ApkMetadata
{
    public string ApkPath { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string VersionName { get; set; } = "";
    public int VersionCode { get; set; }
    public string LauncherActivity { get; set; } = "";
    public List<string> AllActivities { get; set; } = new();
    public List<string> MimeTypes { get; set; } = new();
    public List<string> Schemes { get; set; } = new();
    public string PreferredOrientation { get; set; } = "unspecified"; // "portrait", "landscape", or "unspecified"
    public bool IsWindowable { get; set; } = true; // true if resizeableActivity=true / supports arbitrary multi-window aspect ratios
    public string PreferredScreenSize { get; set; } = "1280x720";
    public List<string> SupportedScreenSizes { get; set; } = new();
    public List<string> NativeAbis { get; set; } = new();
    public bool IsCompatibleWithX86_64 =>
        NativeAbis.Count == 0 ||
        NativeAbis.Any(a => a.Equals("x86_64", StringComparison.OrdinalIgnoreCase) ||
                            a.Equals("x86", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Zero-dependency Android Binary XML (AXML) parser that extracts package,
/// main launcher activity, windowing/orientation capabilities, and intent-filter MIME types directly from an APK archive.
/// </summary>
public static class ApkManifestParser
{
    private const ushort RES_XML_TYPE = 0x0003;
    private const ushort RES_STRING_POOL_TYPE = 0x0001;
    private const ushort RES_XML_START_ELEMENT_TYPE = 0x0102;
    private const ushort RES_XML_END_ELEMENT_TYPE = 0x0103;
    private const uint UTF8_FLAG = 1u << 8;

    public static ApkMetadata ParseApk(string apkPath)
    {
        string fullPath = Path.GetFullPath(apkPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"APK file not found: {fullPath}");
        }

        using var archive = ZipFile.OpenRead(fullPath);
        var manifestEntry = archive.GetEntry("AndroidManifest.xml")
            ?? throw new InvalidDataException($"'{fullPath}' does not contain AndroidManifest.xml.");

        using var stream = manifestEntry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        byte[] data = ms.ToArray();

        var meta = ParseManifestBytes(data, fullPath);
        var abis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            string entryPath = entry.FullName.Replace('\\', '/');
            if (entryPath.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) &&
                entryPath.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = entryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    abis.Add(parts[1].Trim());
                }
            }
        }
        meta.NativeAbis.AddRange(abis.OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
        return meta;
    }

    private static ApkMetadata ParseManifestBytes(byte[] bytes, string apkPath)
    {
        if (bytes.Length < 8 || BitConverter.ToUInt16(bytes, 0) != RES_XML_TYPE)
        {
            throw new InvalidDataException("Invalid binary AndroidManifest.xml header.");
        }

        var meta = new ApkMetadata { ApkPath = apkPath };
        string[] strings = Array.Empty<string>();

        int offset = BitConverter.ToUInt16(bytes, 2); // Root header size (typically 8)
        int fileSize = Math.Min(bytes.Length, BitConverter.ToInt32(bytes, 4));

        string? currentActivity = null;
        bool inIntentFilter = false;
        bool hasMainAction = false;
        bool hasLauncherCategory = false;
        bool hasViewAction = false;
        var filterMimeTypes = new List<string>();
        var filterSchemes = new List<string>();
        string? firstViewActivity = null;

        bool? appResizable = null;
        var activityOrientations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var activityResizable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var activityLayoutSize = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (offset + 8 <= fileSize)
        {
            ushort chunkType = BitConverter.ToUInt16(bytes, offset);
            ushort headerSize = BitConverter.ToUInt16(bytes, offset + 2);
            int chunkSize = BitConverter.ToInt32(bytes, offset + 4);
            if (chunkSize <= 0 || offset + chunkSize > fileSize)
            {
                break;
            }

            if (chunkType == RES_STRING_POOL_TYPE)
            {
                strings = ReadStringPool(bytes, offset, headerSize, chunkSize);
            }
            else if (chunkType == RES_XML_START_ELEMENT_TYPE && offset + 36 <= fileSize)
            {
                int nameIdx = BitConverter.ToInt32(bytes, offset + 20);
                string elemName = GetString(strings, nameIdx);

                ushort attrStart = BitConverter.ToUInt16(bytes, offset + 24);
                ushort attrSize = BitConverter.ToUInt16(bytes, offset + 26);
                ushort attrCount = BitConverter.ToUInt16(bytes, offset + 28);

                var attrs = ReadAttributes(bytes, offset + 16 + attrStart, attrSize, attrCount, strings);

                if (elemName == "manifest")
                {
                    if (attrs.TryGetValue("package", out string? pkg))
                    {
                        meta.PackageName = pkg;
                    }
                    if (attrs.TryGetValue("versionName", out string? vName))
                    {
                        meta.VersionName = vName;
                    }
                    if (attrs.TryGetValue("versionCode", out string? vCode) && int.TryParse(vCode, out int vc))
                    {
                        meta.VersionCode = vc;
                    }
                }
                else if (elemName == "application")
                {
                    if (attrs.TryGetValue("resizeableActivity", out string? appRes) &&
                        bool.TryParse(appRes, out bool ar))
                    {
                        appResizable = ar;
                    }
                }
                else if (elemName == "activity" || elemName == "activity-alias")
                {
                    bool isEnabled = !attrs.TryGetValue("enabled", out string? enVal) ||
                                     !enVal.Equals("false", StringComparison.OrdinalIgnoreCase);

                    if (attrs.TryGetValue("name", out string? rawName) && !string.IsNullOrEmpty(rawName) && isEnabled)
                    {
                        currentActivity = NormalizeClassName(meta.PackageName, rawName);
                        if (!meta.AllActivities.Contains(currentActivity))
                        {
                            meta.AllActivities.Add(currentActivity);
                        }

                        if (attrs.TryGetValue("screenOrientation", out string? rawOrien))
                        {
                            activityOrientations[currentActivity] = DecodeScreenOrientation(rawOrien);
                        }
                        if (attrs.TryGetValue("resizeableActivity", out string? actRes) &&
                            bool.TryParse(actRes, out bool acr))
                        {
                            activityResizable[currentActivity] = acr;
                        }
                    }
                    else
                    {
                        currentActivity = null;
                    }
                }
                else if (elemName == "layout" && currentActivity != null)
                {
                    if (attrs.TryGetValue("defaultWidth", out string? dw) &&
                        attrs.TryGetValue("defaultHeight", out string? dh) &&
                        int.TryParse(dw, out int w) && int.TryParse(dh, out int h) &&
                        w >= 200 && h >= 200)
                    {
                        activityLayoutSize[currentActivity] = $"{w}x{h}";
                    }
                }
                else if (elemName == "intent-filter" && currentActivity != null)
                {
                    inIntentFilter = true;
                    hasMainAction = false;
                    hasLauncherCategory = false;
                    hasViewAction = false;
                    filterMimeTypes.Clear();
                    filterSchemes.Clear();
                }
                else if (inIntentFilter && elemName == "action")
                {
                    if (attrs.TryGetValue("name", out string? actionName))
                    {
                        if (actionName == "android.intent.action.MAIN")
                        {
                            hasMainAction = true;
                        }
                        else if (actionName == "android.intent.action.VIEW")
                        {
                            hasViewAction = true;
                        }
                    }
                }
                else if (inIntentFilter && elemName == "category")
                {
                    if (attrs.TryGetValue("name", out string? catName) &&
                        catName == "android.intent.action.LAUNCHER" ||
                        attrs.TryGetValue("name", out catName) &&
                        catName == "android.intent.category.LAUNCHER")
                    {
                        hasLauncherCategory = true;
                    }
                }
                else if (inIntentFilter && elemName == "data")
                {
                    if (attrs.TryGetValue("mimeType", out string? mime) && !string.IsNullOrWhiteSpace(mime))
                    {
                        filterMimeTypes.Add(mime);
                    }
                    if (attrs.TryGetValue("scheme", out string? scheme) && !string.IsNullOrWhiteSpace(scheme))
                    {
                        filterSchemes.Add(scheme);
                    }
                }
            }
            else if (chunkType == RES_XML_END_ELEMENT_TYPE && offset + 24 <= fileSize)
            {
                int nameIdx = BitConverter.ToInt32(bytes, offset + 20);
                string elemName = GetString(strings, nameIdx);

                if (elemName == "intent-filter" && inIntentFilter && currentActivity != null)
                {
                    if (hasMainAction && hasLauncherCategory && string.IsNullOrEmpty(meta.LauncherActivity))
                    {
                        meta.LauncherActivity = currentActivity;
                    }
                    if (hasViewAction)
                    {
                        firstViewActivity ??= currentActivity;
                        foreach (string m in filterMimeTypes)
                        {
                            if (!meta.MimeTypes.Contains(m))
                            {
                                meta.MimeTypes.Add(m);
                            }
                        }
                        foreach (string s in filterSchemes)
                        {
                            if (!meta.Schemes.Contains(s))
                            {
                                meta.Schemes.Add(s);
                            }
                        }
                    }
                    inIntentFilter = false;
                }
                else if (elemName == "activity" || elemName == "activity-alias")
                {
                    currentActivity = null;
                }
            }

            offset += chunkSize;
        }

        if (string.IsNullOrEmpty(meta.LauncherActivity))
        {
            meta.LauncherActivity = firstViewActivity ?? meta.AllActivities.FirstOrDefault() ?? $"{meta.PackageName}.MainActivity";
        }

        // Prefer real UI activity over noHistory trampoline aliases (e.g. SplashActivity.Green or Chrome Main trampoline)
        // which otherwise launch a new task with FLAG_ACTIVITY_NEW_TASK onto Display #0 and finish on virtual displays.
        if (meta.LauncherActivity.Contains("SplashActivity", StringComparison.OrdinalIgnoreCase))
        {
            string? mainCandidate = meta.AllActivities.FirstOrDefault(a =>
                a.EndsWith(".MainActivity", StringComparison.OrdinalIgnoreCase) &&
                !a.Contains("Splash", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(mainCandidate))
            {
                meta.LauncherActivity = mainCandidate;
            }
        }
        else if (meta.LauncherActivity.Equals("com.google.android.apps.chrome.Main", StringComparison.OrdinalIgnoreCase) &&
                 meta.AllActivities.Contains("org.chromium.chrome.browser.ChromeTabbedActivity"))
        {
            meta.LauncherActivity = "org.chromium.chrome.browser.ChromeTabbedActivity";
        }

        meta.LauncherActivity = ResolveDirectActivity(meta.PackageName, meta.LauncherActivity);

        // Determine preferred orientation, resizability (window-able), and supported screen sizes for the target UI activity
        string orientation = "unspecified";
        if (activityOrientations.TryGetValue(meta.LauncherActivity, out string? actOr))
        {
            orientation = actOr;
        }
        else
        {
            var mainMatch = activityOrientations.FirstOrDefault(kv => kv.Key.EndsWith(".MainActivity", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(mainMatch.Value))
            {
                orientation = mainMatch.Value;
            }
        }
        meta.PreferredOrientation = orientation;

        bool isWindowable;
        if (activityResizable.TryGetValue(meta.LauncherActivity, out bool explicitActRes))
        {
            isWindowable = explicitActRes;
        }
        else if (appResizable.HasValue)
        {
            isWindowable = appResizable.Value && orientation == "unspecified";
        }
        else
        {
            // Android 7.0+ default: activities without a fixed portrait/landscape lock are multi-window resizable
            isWindowable = orientation == "unspecified";
        }
        meta.IsWindowable = isWindowable;

        PopulateScreenSizeProfile(meta, activityLayoutSize.TryGetValue(meta.LauncherActivity, out string? customLayout) ? customLayout : null);
        return meta;
    }

    public static void PopulateScreenSizeProfile(ApkMetadata meta, string? manifestLayoutSize = null)
    {
        meta.SupportedScreenSizes.Clear();
        if (meta.PreferredOrientation.Equals("portrait", StringComparison.OrdinalIgnoreCase))
        {
            meta.PreferredScreenSize = !string.IsNullOrWhiteSpace(manifestLayoutSize) ? manifestLayoutSize : "540x960";
            meta.SupportedScreenSizes.AddRange(new[]
            {
                "540x960 (Preferred — Portrait qHD 9:16)",
                "480x854 (Supported — Portrait Compact 9:16)",
                "720x1280 (Supported — Portrait HD 9:16)",
                "600x960 (Supported — Portrait Tablet 10:16)",
                "1280x720 (Compatibility — Landscape HD 16:9 *)"
            });
        }
        else if (meta.PreferredOrientation.Equals("landscape", StringComparison.OrdinalIgnoreCase))
        {
            meta.PreferredScreenSize = !string.IsNullOrWhiteSpace(manifestLayoutSize) ? manifestLayoutSize : "1280x720";
            meta.SupportedScreenSizes.AddRange(new[]
            {
                "1280x720 (Preferred — Landscape HD 16:9)",
                "1366x768 (Supported — Widescreen 16:9)",
                "1600x900 (Supported — Desktop HD+ 16:9)",
                "1920x1080 (Supported — Full HD 16:9)",
                "1024x768 (Supported — Tablet Landscape 4:3)",
                "540x960 (Compatibility — Portrait 9:16 *)"
            });
        }
        else
        {
            meta.PreferredScreenSize = !string.IsNullOrWhiteSpace(manifestLayoutSize) ? manifestLayoutSize : "1280x720";
            meta.SupportedScreenSizes.AddRange(new[]
            {
                "1280x720 (Preferred — Landscape HD 16:9)",
                "1024x768 (Supported — Tablet Landscape 4:3)",
                "1366x768 (Supported — Laptop Widescreen 16:9)",
                "1600x900 (Supported — Desktop HD+ 16:9)",
                "960x640 (Supported — Compact Window 3:2)",
                "800x800 (Supported — Square Multi-Window 1:1)",
                "540x960 (Supported — Phone Portrait 9:16)",
                "720x1280 (Supported — Portrait HD 9:16)"
            });
        }
    }

    private static string DecodeScreenOrientation(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue)) return "unspecified";
        if (int.TryParse(rawValue, out int code))
        {
            return code switch
            {
                0 or 6 or 8 or 11 => "landscape",
                1 or 7 or 9 or 12 => "portrait",
                _ => "unspecified"
            };
        }
        if (rawValue.Contains("portrait", StringComparison.OrdinalIgnoreCase)) return "portrait";
        if (rawValue.Contains("landscape", StringComparison.OrdinalIgnoreCase)) return "landscape";
        return "unspecified";
    }

    public static string ResolveDirectActivity(string packageName, string configuredActivity)
    {
        if (string.IsNullOrWhiteSpace(configuredActivity))
        {
            return configuredActivity;
        }

        if (configuredActivity.Contains(".activities.SplashActivity", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(packageName))
        {
            return $"{packageName}.activities.MainActivity";
        }

        if (configuredActivity.Equals("com.google.android.apps.chrome.Main", StringComparison.OrdinalIgnoreCase))
        {
            return "org.chromium.chrome.browser.ChromeTabbedActivity";
        }

        return configuredActivity;
    }

    private static string NormalizeClassName(string packageName, string className)
    {
        if (className.StartsWith('.'))
        {
            return packageName + className;
        }
        if (!className.Contains('.'))
        {
            return $"{packageName}.{className}";
        }
        return className;
    }

    private static Dictionary<string, string> ReadAttributes(
        byte[] bytes,
        int attrOffset,
        int attrSize,
        int attrCount,
        string[] strings)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (attrSize <= 0) attrSize = 20;

        for (int i = 0; i < attrCount; i++)
        {
            int pos = attrOffset + i * attrSize;
            if (pos + 20 > bytes.Length) break;

            int nameIdx = BitConverter.ToInt32(bytes, pos + 4);
            int rawValueIdx = BitConverter.ToInt32(bytes, pos + 8);
            byte dataType = bytes[pos + 15];
            int dataValue = BitConverter.ToInt32(bytes, pos + 16);

            string name = GetString(strings, nameIdx);
            if (string.IsNullOrEmpty(name)) continue;

            string val;
            if (rawValueIdx >= 0 && rawValueIdx < strings.Length)
            {
                val = strings[rawValueIdx];
            }
            else if (dataType == 0x03 && dataValue >= 0 && dataValue < strings.Length) // TYPE_STRING
            {
                val = strings[dataValue];
            }
            else if (dataType == 0x12) // TYPE_INT_BOOLEAN
            {
                val = dataValue != 0 ? "true" : "false";
            }
            else
            {
                val = dataValue.ToString();
            }

            dict[name] = val;
        }

        return dict;
    }

    private static string[] ReadStringPool(byte[] bytes, int chunkOffset, int headerSize, int chunkSize)
    {
        int stringCount = BitConverter.ToInt32(bytes, chunkOffset + 8);
        uint flags = BitConverter.ToUInt32(bytes, chunkOffset + 16);
        int stringsStart = BitConverter.ToInt32(bytes, chunkOffset + 20);
        bool isUtf8 = (flags & UTF8_FLAG) != 0;

        if (stringCount <= 0) return Array.Empty<string>();

        var result = new string[stringCount];
        int offsetsBase = chunkOffset + headerSize;
        int dataBase = chunkOffset + stringsStart;

        for (int i = 0; i < stringCount; i++)
        {
            int offsetPos = offsetsBase + i * 4;
            if (offsetPos + 4 > chunkOffset + chunkSize) break;

            int strOffset = BitConverter.ToInt32(bytes, offsetPos);
            int pos = dataBase + strOffset;
            if (pos < 0 || pos >= chunkOffset + chunkSize)
            {
                result[i] = "";
                continue;
            }

            if (isUtf8)
            {
                // Skip UTF-16 char length (1 or 2 bytes)
                if ((bytes[pos] & 0x80) != 0) pos += 2;
                else pos += 1;

                // Read UTF-8 byte length (1 or 2 bytes)
                int byteLen;
                if ((bytes[pos] & 0x80) != 0)
                {
                    byteLen = ((bytes[pos] & 0x7F) << 8) | bytes[pos + 1];
                    pos += 2;
                }
                else
                {
                    byteLen = bytes[pos];
                    pos += 1;
                }

                if (pos + byteLen <= bytes.Length)
                {
                    result[i] = Encoding.UTF8.GetString(bytes, pos, byteLen);
                }
                else
                {
                    result[i] = "";
                }
            }
            else
            {
                // UTF-16LE length (2 or 4 bytes)
                int charLen = BitConverter.ToUInt16(bytes, pos);
                if ((charLen & 0x8000) != 0)
                {
                    charLen = ((charLen & 0x7FFF) << 16) | BitConverter.ToUInt16(bytes, pos + 2);
                    pos += 4;
                }
                else
                {
                    pos += 2;
                }

                int byteLen = charLen * 2;
                if (pos + byteLen <= bytes.Length)
                {
                    result[i] = Encoding.Unicode.GetString(bytes, pos, byteLen);
                }
                else
                {
                    result[i] = "";
                }
            }
        }

        return result;
    }

    private static string GetString(string[] pool, int index)
    {
        return index >= 0 && index < pool.Length ? pool[index] : "";
    }
}
