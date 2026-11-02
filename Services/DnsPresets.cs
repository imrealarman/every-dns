using System.Net;

namespace EveryDns.Services;

public sealed record DnsPreset(string Name, string Group, string[] V4, string[] V6, string Note, bool Custom = false)
{
    public IEnumerable<string> All => V4.Concat(V6);

    /// <summary>The server used for timing and look-ups: the first IPv4 address, or the first IPv6 one if there is no IPv4.</summary>
    public IPAddress Primary => IPAddress.Parse(V4.Length > 0 ? V4[0] : V6[0]);

    /// <summary>True when every IPv4 server of this preset (or every IPv6 one, if it has no IPv4) is among the servers in use.</summary>
    public bool Matches(IEnumerable<IPAddress> inUse)
    {
        var have = inUse.ToHashSet();
        return (V4.Length > 0 ? V4 : V6).Select(IPAddress.Parse).All(have.Contains);
    }
}

public static class DnsPresets
{
    public static readonly IReadOnlyList<DnsPreset> Builtin = new DnsPreset[]
    {
        new("Cloudflare", "Fast and private", new[] { "1.1.1.1", "1.0.0.1" }, new[] { "2606:4700:4700::1111", "2606:4700:4700::1001" }, "One of the fastest public resolvers; does not keep your IP address."),
        new("Google", "Fast and private", new[] { "8.8.8.8", "8.8.4.4" }, new[] { "2001:4860:4860::8888", "2001:4860:4860::8844" }, "Very reliable and fast almost everywhere."),
        new("Quad9", "Security", new[] { "9.9.9.9", "149.112.112.112" }, new[] { "2620:fe::fe", "2620:fe::9" }, "Blocks known malicious sites. Non-profit."),
        new("Cloudflare (malware blocking)", "Security", new[] { "1.1.1.2", "1.0.0.2" }, new[] { "2606:4700:4700::1112", "2606:4700:4700::1002" }, "Cloudflare, plus blocking of known malware domains."),
        new("OpenDNS", "Security", new[] { "208.67.222.222", "208.67.220.220" }, new[] { "2620:119:35::35", "2620:119:53::53" }, "Cisco's resolver with phishing protection."),
        new("AdGuard DNS", "Security", new[] { "94.140.14.14", "94.140.15.15" }, new[] { "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff" }, "Blocks ads and trackers for every app on the PC."),
        new("Cloudflare (family)", "Family", new[] { "1.1.1.3", "1.0.0.3" }, new[] { "2606:4700:4700::1113", "2606:4700:4700::1003" }, "Blocks malware and adult content."),
        new("CleanBrowsing (family)", "Family", new[] { "185.228.168.168", "185.228.169.168" }, new[] { "2a0d:2a00:1::", "2a0d:2a00:2::" }, "Blocks adult content and forces safe search."),
        new("Shecan", "Iran", new[] { "178.22.122.100", "185.51.200.2" }, Array.Empty<string>(), "Helps reach sites that are blocked from Iran."),
        new("Electro", "Iran", new[] { "78.157.42.100", "78.157.42.101" }, Array.Empty<string>(), "Helps reach sites that are blocked from Iran, and online games."),
        new("403.online", "Iran", new[] { "10.202.10.202", "10.202.10.102" }, Array.Empty<string>(), "Helps with sanctions blocks. Only answers from inside Iran."),
        new("Radar Game", "Iran", new[] { "10.202.10.10", "10.202.10.11" }, Array.Empty<string>(), "Lower ping for online games. Only answers from inside Iran."),
    };

    /// <summary>Accepts a single IPv4 or IPv6 address; rejects anything that is not a real, unicast server address.</summary>
    public static bool TryParseServer(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        if (!IPAddress.TryParse(text?.Trim(), out var parsed)) return false;
        if (text!.Contains('%') || text.Contains('/')) return false;
        if (parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any) || parsed.Equals(IPAddress.Broadcast)) return false;
        if (IPAddress.IsLoopback(parsed) || parsed.IsIPv6Multicast || parsed.IsIPv6LinkLocal) return false;
        if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && parsed.GetAddressBytes()[0] >= 224) return false;
        address = parsed;
        return true;
    }
}
