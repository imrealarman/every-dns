using System.Text.Json;

namespace EveryDns.Services;

public sealed class Subscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public DateTime? Updated { get; set; }
}

/// <summary>Servers, subscriptions and the saved Windows proxy setting, kept in %AppData%\EveryDns\proxy.json.</summary>
public sealed class ProxyStore
{
    public List<ProxyServer> Servers { get; set; } = new();
    public List<Subscription> Subscriptions { get; set; } = new();
    public string? SelectedId { get; set; }
    /// <summary>"system": turn on the Windows proxy while connected. "local": only open the SOCKS and HTTP ports.</summary>
    public string Mode { get; set; } = "system";
    /// <summary>"global": everything through the server. "iran": Iranian sites go direct.</summary>
    public string Routing { get; set; } = "global";
    /// <summary>Block all traffic except to the proxy server while connected, so nothing leaks if the proxy drops.</summary>
    public bool KillSwitch { get; set; }
    /// <summary>Set while EveryDns has turned the Windows proxy on, so a crash can be cleaned up on the next start.</summary>
    public ProxyBackup? Backup { get; set; }
    /// <summary>Bytes carried through the proxy over all sessions.</summary>
    public long TotalDown { get; set; }
    public long TotalUp { get; set; }

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveryDns", "proxy.json");

    public static ProxyStore Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<ProxyStore>(File.ReadAllText(FilePath)) ?? new ProxyStore();
        }
        catch (Exception ex) { Log.Write("Could not read the proxy list", ex); }
        return new ProxyStore();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("Could not save the proxy list", ex); }
    }

    /// <summary>Adds servers that are not already listed (same link). Returns how many were new.</summary>
    public int Add(IEnumerable<ProxyServer> servers, string subscriptionId = "")
    {
        var known = Servers.Select(s => s.Raw).ToHashSet();
        int added = 0;
        foreach (var s in servers)
        {
            if (!known.Add(s.Raw)) continue;
            s.Subscription = subscriptionId;
            Servers.Add(s);
            added++;
        }
        return added;
    }

    /// <summary>Swaps the servers of one subscription for a fresh list, keeping the selection when its server is still there.</summary>
    public void Replace(string subscriptionId, IReadOnlyList<ProxyServer> fresh)
    {
        string? selectedRaw = Servers.FirstOrDefault(s => s.Id == SelectedId)?.Raw;
        Servers.RemoveAll(s => s.Subscription == subscriptionId);
        Add(fresh, subscriptionId);
        if (selectedRaw != null) SelectedId = Servers.FirstOrDefault(s => s.Raw == selectedRaw)?.Id ?? SelectedId;
    }
}
