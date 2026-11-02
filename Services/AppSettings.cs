using System.Text.Json;

namespace EveryDns.Services;

/// <summary>The DNS setting an adapter had before EveryDns changed it, so the change can be undone.</summary>
public sealed class DnsSnapshot
{
    public bool Automatic { get; set; }
    public List<string> Servers { get; set; } = new();
}

public sealed class SavedServer
{
    public string Name { get; set; } = "";
    public List<string> Servers { get; set; } = new();
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "system";
    public string? AdapterId { get; set; }
    public List<SavedServer> Custom { get; set; } = new();
    public Dictionary<string, DnsSnapshot> Undo { get; set; } = new();

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveryDns", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) { Log.Write("Could not read the settings", ex); }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("Could not save the settings", ex); }
    }
}
