using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spotlight.Core;

/// <summary>User-editable settings in %LOCALAPPDATA%\Spotlight\config.json.</summary>
internal sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(AppPaths.DataDir, "config.json");

    /// <summary>Start automatically when Windows starts (registered in HKCU\...\Run).</summary>
    public bool Autostart { get; set; } = true;

    /// <summary>Web search URL; {0} is replaced by the escaped query.</summary>
    public string WebSearchUrl { get; set; } = "https://www.google.com/search?q={0}";

    /// <summary>Also find documents by their content (Windows Search index).</summary>
    public bool SearchFileContents { get; set; } = true;

    /// <summary>The one-time "Spotlight is running" notification was shown.</summary>
    public bool WelcomeShown { get; set; }

    /// <summary>Additional folders to index besides Desktop, Documents, Downloads, Pictures, Music, Videos and OneDrive.</summary>
    public List<string> ExtraFolders { get; set; } = [];

    /// <summary>Folder names that are never indexed (build output, package caches, ...).</summary>
    public List<string> ExcludedFolderNames { get; set; } =
    [
        "node_modules", "bower_components", "__pycache__", "site-packages", "venv", ".venv", "obj",
        "packages", "vendor", "target", "Library", "Temp", "tmp", "cache", "Cache", "Caches",
    ];

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Error("config.json unreadable, using defaults", ex);
            return new AppConfig();
        }
        var fresh = new AppConfig();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions)); }
        catch (Exception ex) { Log.Error("Saving config.json failed", ex); }
    }
}
