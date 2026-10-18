using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Xml.Linq;
using Spotlight.Core;
using Spotlight.Native;
using Spotlight.Services;

namespace Spotlight.Providers;

/// <summary>
/// Windows Settings pages and Control Panel tasks, taken from the same index the Settings search uses
/// (%windir%\ImmersiveControlPanel\Settings\AllSystemSettings_*.xml). Names and keywords are resource strings
/// resolved by Windows itself, so they always appear in the display language of the PC.
/// </summary>
internal sealed class SettingsProvider : ISearchProvider
{
    private const int CacheVersion = 3;
    private const string CanonicalNameHost = "{7E0522FC-1AC4-41CA-AFD0-3610417A9C41}";
    private const string CommandLineHost = "{12B1697E-D3A0-4DBC-B568-CCF64A3F934D}";
    private const string SecurityHost = "{6E6DDBCB-9C89-434B-A994-D5F22239523B}";

    private sealed class Dto
    {
        public string Name { get; set; } = "";
        public string[] Keywords { get; set; } = [];
        public string Kind { get; set; } = "";   // uri | control | cmd
        public string Target { get; set; } = "";
        public string? Args { get; set; }
        public string Group { get; set; } = "";  // settings | control | security
        public string? Glyph { get; set; }
        public string? Font { get; set; }
        public string? FontFile { get; set; }
        public string? Icon { get; set; }
    }

    private sealed record Entry(Dto Data, MatchTarget Name, MatchTarget[] Keywords, FontFamily? Font);

    // Pages whose index entries carry no deep link.
    private static readonly Dictionary<string, string> PageFallback = new()
    {
        ["SettingsPageAccountsUsers"] = "otherusers",
        ["SettingsPageAdvancedSystemSettings"] = "developers",
        ["SettingsPageDeviceEncryption2"] = "deviceencryption",
        ["SettingsPageEaseOfAccessNarratorNS"] = "easeofaccess-narrator",
        ["SettingsPageFlights"] = "windowsinsider",
        ["SettingsPageNetworkWorkplace"] = "workplace",
        ["SettingsPagePCSystemBluetooth"] = "bluetooth",
        ["SettingsPagePCSystemDevices"] = "connecteddevices",
        ["SettingsPagePCSystemDisplayRemote"] = "display",
        ["SettingsPagePrivacyAppDiagnostics2"] = "privacy-appdiagnostics",
        ["SettingsPageRestoreOneBackup"] = "backup",
        ["SettingsPageSharedExperiences2"] = "crossdevice",
        ["SettingsPageStart2"] = "personalization-start",
    };

    // Canonical deep link for pages that list several.
    private static readonly Dictionary<string, string> PagePreferred = new()
    {
        ["SettingsPageAudio"] = "sound",
        ["SettingsPageDevices"] = "bluetooth",
        ["SettingsPageInstalledApps"] = "installed-apps",
        ["SettingsPageNetworkAirplaneMode"] = "network-airplanemode",
        ["SettingsPageOtherUsers"] = "otherusers",
        ["SettingsPagePowerAndBattery"] = "powersleep",
        ["SettingsPageStorageSenseStorageOverview"] = "storagesense",
        ["SettingsPageTimeRegionKeyboard"] = "keyboard",
        ["SettingsPageTimeRegionLanguage"] = "regionlanguage",
    };

    private static readonly Dictionary<string, string> DefenderPages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AccountProtection"] = "account",
        ["AppBrowser"] = "appbrowser",
        ["DeviceSecurity"] = "devicesecurity",
        ["DataEncryption"] = "devicesecurity",
        ["CoreIsolation"] = "coreisolation",
        ["Family"] = "family",
        ["Network"] = "network",
        ["PerfHealth"] = "perfhealth",
        ["RansomwareProtection"] = "ransomwareprotection",
        ["ProtectedFolders"] = "ransomwareprotection",
        ["AllowAppThroughFolder"] = "ransomwareprotection",
        ["ExploitProtection"] = "exploitprotection",
        ["None"] = "",
    };

    private static readonly Dictionary<string, FontFamily> Fonts = new();

    private volatile IReadOnlyList<Entry> _entries = [];

    public int Count => _entries.Count;

    public void LoadInBackground()
    {
        var thread = new Thread(() =>
        {
            try { Load(); }
            catch (Exception ex) { Log.Error("Reading Windows settings index failed", ex); }
        })
        { IsBackground = true, Name = "SettingsIndex", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    private void Load()
    {
        string? xml = PickIndexFile();
        if (xml == null) return;

        var info = new FileInfo(xml);
        string lang = CultureInfo.CurrentUICulture.Name;
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
            $"{CacheVersion}|{xml}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{lang}")))[..16];
        string cacheFile = Path.Combine(AppPaths.CacheDir, $"settings-{lang}-{hash}.json");

        List<Dto>? dtos = null;
        try
        {
            if (File.Exists(cacheFile)) dtos = JsonSerializer.Deserialize<List<Dto>>(File.ReadAllText(cacheFile));
        }
        catch { }

        if (dtos == null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            dtos = Parse(xml);
            Log.Info($"Settings index parsed: {dtos.Count} entries in {sw.ElapsedMilliseconds} ms");
            try
            {
                foreach (var old in Directory.GetFiles(AppPaths.CacheDir, "settings-*.json")) File.Delete(old);
                File.WriteAllText(cacheFile, JsonSerializer.Serialize(dtos));
            }
            catch (Exception ex) { Log.Error("Writing settings cache failed", ex); }
        }

        _entries = dtos.Select(d => new Entry(
            d,
            new MatchTarget(d.Name),
            d.Keywords.Select(k => new MatchTarget(k)).ToArray(),
            GetFont(d.Font, d.FontFile))).ToList();
    }

    /// <summary>Several variants ship with Windows; the one with deep links ("PolicyIds") is the desktop one.</summary>
    private static string? PickIndexFile()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ImmersiveControlPanel", "Settings");
        if (!Directory.Exists(dir)) return null;
        string? best = null;
        int bestCount = -1;
        foreach (var file in Directory.GetFiles(dir, "AllSystemSettings_*.xml"))
        {
            string text = File.ReadAllText(file);
            int count = 0, i = 0;
            while ((i = text.IndexOf("<PolicyIds>", i, StringComparison.Ordinal)) >= 0) { count++; i++; }
            if (count > bestCount) (best, bestCount) = (file, count);
        }
        return best;
    }

    private static List<Dto> Parse(string xmlFile)
    {
        var doc = XDocument.Load(xmlFile);
        var result = new List<Dto>(1400);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var content in doc.Descendants().Where(e => e.Name.LocalName == "SearchableContent"))
        {
            var identity = Child(content, "SettingIdentity");
            var appInfo = Child(content, "ApplicationInformation");
            var settingInfo = Child(content, "SettingInformation");
            if (identity == null || settingInfo == null) continue;
            if (identity.Descendants().Any(e => e.Name.LocalName == "Condition")) continue; // hardware/feature specific

            string? name = Resolve(Value(settingInfo, "Description"));
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('@')) continue;

            var path = identity.Descendants().FirstOrDefault(e => e.Name.LocalName == "Path");
            string pageId = Value(identity, "PageID")
                ?? (path != null ? Value(path, "PageID") : null)
                ?? path?.Elements().FirstOrDefault(e => e.Name.LocalName == "Node" && (string?)e.Attribute("Type") == "Page")?.Value
                ?? "";
            string? policyIds = path != null ? Value(path, "PolicyIds") : null;
            string? groupId = path != null ? Value(path, "GroupID") : null;
            string? hostId = Value(identity, "HostID");
            string? deepLink = appInfo != null ? Value(appInfo, "DeepLink") : null;
            string fileName = Value(content, "Filename") ?? "";

            var dto = new Dto { Name = name.Trim() };
            if (!Route(dto, pageId, policyIds, groupId, hostId, deepLink, fileName)) continue;
            if (!seen.Add(dto.Name + "|" + dto.Target + "|" + dto.Args)) continue;

            dto.Keywords = ResolveKeywords(Value(settingInfo, "HighKeywords"))
                .Concat(ResolveKeywords(Value(settingInfo, "Keywords")))
                .Where(k => k.Length > 1 && !k.Equals(dto.Name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToArray();

            if (appInfo != null)
            {
                dto.Glyph = Value(appInfo, "Glyph");
                dto.Font = Value(appInfo, "FontFamily");
                dto.FontFile = Value(appInfo, "FontFilePath");
                dto.Icon = Value(appInfo, "Icon");
            }
            result.Add(dto);
        }
        return result;
    }

    private static bool Route(Dto dto, string pageId, string? policyIds, string? groupId, string? hostId, string? deepLink, string fileName)
    {
        if (pageId.StartsWith("SettingsPage", StringComparison.Ordinal))
        {
            dto.Kind = "uri";
            dto.Group = "settings";
            dto.Target = "ms-settings:" + ChooseDeepLink(pageId, policyIds, fileName + groupId);
            return true;
        }
        if (string.Equals(hostId, SecurityHost, StringComparison.OrdinalIgnoreCase))
        {
            dto.Kind = "uri";
            dto.Group = "security";
            string page = deepLink != null && DefenderPages.TryGetValue(deepLink, out var p) ? p : deepLink?.ToLowerInvariant() ?? "";
            dto.Target = "windowsdefender://" + page;
            return true;
        }
        dto.Group = "control";
        if (!string.IsNullOrWhiteSpace(deepLink))
        {
            if (string.Equals(hostId, CanonicalNameHost, StringComparison.OrdinalIgnoreCase))
            {
                int slash = deepLink.IndexOf('\\');
                dto.Kind = "control";
                dto.Target = "control.exe";
                dto.Args = slash < 0 ? $"/name {deepLink}" : $"/name {deepLink[..slash]} /page {deepLink[(slash + 1)..]}";
                return true;
            }
            dto.Kind = deepLink.Contains("://") || deepLink.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? "uri" : "cmd";
            dto.Target = deepLink;
            return true;
        }
        if (pageId.StartsWith('{'))
        {
            dto.Kind = "uri";
            dto.Target = "shell:::" + pageId;
            return true;
        }
        return false;
    }

    private static string ChooseDeepLink(string pageId, string? policyIds, string context)
    {
        var ids = (policyIds ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0) return PageFallback.GetValueOrDefault(pageId, "");

        // A sub page named in the entry itself ("…NightLight" → ms-settings:nightlight) is the most precise target.
        string hay = new string(context.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        string? specific = ids
            .Where(id => id.Length >= 5 && hay.Contains(id.Replace("-", "").ToLowerInvariant()))
            .OrderByDescending(id => id.Length)
            .FirstOrDefault();
        if (specific != null) return specific;
        if (PagePreferred.TryGetValue(pageId, out var preferred) && ids.Contains(preferred)) return preferred;
        return ids.OrderBy(id => id.Count(c => c == '-')).First();
    }

    private static IEnumerable<string> ResolveKeywords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        IEnumerable<string> refs = raw.StartsWith("@@", StringComparison.Ordinal)
            ? raw[2..].Split('@', StringSplitOptions.RemoveEmptyEntries).Select(r => "@" + r)
            : [raw];
        foreach (var r in refs)
        {
            string? text = Resolve(r);
            if (string.IsNullOrEmpty(text) || text.StartsWith('@')) continue;
            foreach (var k in text.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return k;
        }
    }

    private static string? Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('@')) return value;
        var sb = new StringBuilder(1024);
        return NativeMethods.SHLoadIndirectString(value, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }

    private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);

    private static string? Value(XElement e, string name)
    {
        string? v = Child(e, name)?.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static FontFamily? GetFont(string? family, string? file)
    {
        if (string.IsNullOrEmpty(family)) return null;
        string key = family + "|" + file;
        lock (Fonts)
        {
            if (Fonts.TryGetValue(key, out var cached)) return cached;
            FontFamily font;
            string? path = file != null ? Environment.ExpandEnvironmentVariables(file) : null;
            if (path != null && File.Exists(path))
                font = new FontFamily(new Uri(Path.GetDirectoryName(path)!.TrimEnd('\\') + "\\"), "./#" + family);
            else
                font = family.Contains("MDL2") ? new FontFamily("Segoe MDL2 Assets") : Glyphs.Font;
            Fonts[key] = font;
            return font;
        }
    }

    public void Search(Query query, List<SearchResult> results, CancellationToken ct)
    {
        if (query.Norm.Length < 2) return;
        var matches = new List<(Entry Entry, int Score)>();
        foreach (var e in _entries)
        {
            int score = Math.Max(Matcher.Score(query, e.Name, allowFuzzy: false), Matcher.ScoreKeywords(query, e.Keywords));
            if (score >= 480) matches.Add((e, score));
        }

        foreach (var (e, score) in matches.OrderByDescending(m => m.Score).Take(12))
        {
            var d = e.Data;
            bool glyph = d.Glyph != null && d.Icon == null;
            results.Add(new SearchResult
            {
                Key = $"setting:{d.Target}|{d.Args}|{d.Name}",
                Title = d.Name,
                Subtitle = Loc.Get(d.Group switch { "settings" => "Sub.Settings", "security" => "Sub.WindowsSecurity", _ => "Sub.ControlPanel" }),
                Category = Category.Settings,
                Score = score,
                Glyph = glyph ? d.Glyph : d.Icon == null ? (d.Group == "security" ? Glyphs.Shield : Glyphs.Settings) : null,
                GlyphFont = glyph ? e.Font ?? Glyphs.Font : Glyphs.Font,
                IconSource = d.Icon != null ? "res:" + d.Icon : null,
                Execute = () => Launch(d),
                Launch = d.Kind == "cmd" ? new LaunchSpec("cmd", d.Target) : new LaunchSpec("open", d.Target, d.Args),
                CopyValue = d.Args != null ? $"{d.Target} {d.Args}" : d.Target,
            });
        }
    }

    private static void Launch(Dto d)
    {
        switch (d.Kind)
        {
            case "cmd": Launcher.RunCommandLine(d.Target); break;
            default: Launcher.Open(d.Target, d.Args); break;
        }
    }
}
