using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace EveryDns.Services;

/// <summary>
/// The kill switch and the DNS guard, both made of Windows Firewall rules (which need administrator rights, so they are
/// created by the elevated copy of the app, like a DNS change).
///
/// A firewall block rule always beats an allow rule, so instead of "block everything except X" the rules block the
/// <em>complement</em> of X: every address range that is not allowed. Nothing else about the firewall is touched (no
/// default policy changes), so deleting the rules always gives the old behaviour back.
/// </summary>
public static class Firewall
{
    public const string KillName = "EveryDns Kill Switch";
    public const string DnsName = "EveryDns DNS Guard";

    private static readonly string[] LocalV4 = { "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16", "224.0.0.0/3" };
    private static readonly string[] LocalV6 = { "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8" };

    public static string NameOf(string kind) => kind == "kill" ? KillName : DnsName;

    /// <summary>True when rules of that kind ("kill" or "dns") exist. Reading rules needs no administrator rights.</summary>
    public static bool IsActive(string kind) => DnsChanger.Exec("netsh.exe", $"advfirewall firewall show rule name=\"{NameOf(kind)}\"").Code == 0;

    // ---- range maths -------------------------------------------------------------------------------------------

    /// <summary>The address ranges of one family that are NOT covered by <paramref name="allowed"/>, in order.</summary>
    public static List<(IPAddress From, IPAddress To)> Complement(AddressFamily family, IEnumerable<(IPAddress From, IPAddress To)> allowed)
    {
        int bits = family == AddressFamily.InterNetwork ? 32 : 128;
        var max = (BigInteger.One << bits) - 1;

        var sorted = allowed
            .Where(r => r.From.AddressFamily == family && r.To.AddressFamily == family)
            .Select(r => (Lo: ToNumber(r.From), Hi: ToNumber(r.To)))
            .OrderBy(r => r.Lo)
            .ToList();

        var result = new List<(IPAddress, IPAddress)>();
        var cursor = BigInteger.Zero; // the first address not yet decided
        foreach (var (lo, hi) in sorted)
        {
            if (lo > cursor) result.Add((FromNumber(cursor, family), FromNumber(lo - 1, family)));
            if (hi + 1 > cursor) cursor = hi + 1;
        }
        if (cursor <= max) result.Add((FromNumber(cursor, family), FromNumber(max, family)));
        return result;
    }

    public static (IPAddress From, IPAddress To) Cidr(string text)
    {
        var parts = text.Split('/');
        var address = IPAddress.Parse(parts[0]);
        int bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        int prefix = int.Parse(parts[1]);
        var size = BigInteger.One << (bits - prefix);
        var lo = ToNumber(address) / size * size;
        return (FromNumber(lo, address.AddressFamily), FromNumber(lo + size - 1, address.AddressFamily));
    }

    private static BigInteger ToNumber(IPAddress address) => new(address.GetAddressBytes(), isUnsigned: true, isBigEndian: true);

    private static IPAddress FromNumber(BigInteger value, AddressFamily family)
    {
        int length = family == AddressFamily.InterNetwork ? 4 : 16;
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[length];
        Array.Copy(bytes, 0, padded, length - bytes.Length, bytes.Length);
        return new IPAddress(padded);
    }

    /// <summary>The value of netsh's <c>remoteip=</c>: every address except the local ranges and the ones in <paramref name="allowedHosts"/>.</summary>
    public static string BlockedRanges(IEnumerable<IPAddress> allowedHosts)
    {
        var hosts = allowedHosts.Where(h => !h.IsIPv4MappedToIPv6).Select(h => (h, h)).ToList();
        var ranges = new List<string>();
        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            var allowed = (family == AddressFamily.InterNetwork ? LocalV4 : LocalV6).Select(Cidr).Concat(hosts);
            ranges.AddRange(Complement(family, allowed).Select(r => r.From.Equals(r.To) ? r.From.ToString() : $"{r.From}-{r.To}"));
        }
        return string.Join(",", ranges);
    }

    /// <summary>The netsh commands for one kind of rule, given the addresses that must stay reachable.</summary>
    public static List<string> RuleCommands(string kind, IEnumerable<IPAddress> allowedHosts)
    {
        string name = NameOf(kind);
        string remote = BlockedRanges(allowedHosts);
        string common = $"advfirewall firewall add rule name=\"{name}\" dir=out action=block enable=yes profile=any remoteip={remote}";
        // Kill switch: every protocol. DNS guard: only DNS (and DNS over TLS), over UDP and TCP.
        return kind == "kill"
            ? new List<string> { common }
            : new List<string> { common + " protocol=udp remoteport=53,853", common + " protocol=tcp remoteport=53,853" };
    }

    // ---- the elevated side -------------------------------------------------------------------------------------

    /// <summary>Replaces the rules of this kind. All or nothing: if a rule cannot be added, the ones already added are removed again.</summary>
    public static int Apply(string kind, IReadOnlyList<IPAddress> allowedHosts)
    {
        Clear(kind);
        foreach (string command in RuleCommands(kind, allowedHosts))
        {
            var (code, output) = DnsChanger.Exec("netsh.exe", command);
            if (code == 0) continue;
            Log.Write($"Firewall rule failed ({code}): {output}");
            Clear(kind);
            return code;
        }
        return 0;
    }

    public static void Clear(string kind) => DnsChanger.Exec("netsh.exe", $"advfirewall firewall delete rule name=\"{NameOf(kind)}\"");

    /// <summary>Parses <c>--fw-apply kind a,b,c</c>. Returns false for anything that is not a plain list of unicast addresses.</summary>
    public static bool TryParseHosts(string csv, out List<IPAddress> hosts)
    {
        hosts = new List<IPAddress>();
        foreach (string text in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IPAddress.TryParse(text, out var ip) || text.Contains('%') || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
            hosts.Add(ip);
        }
        return hosts.Count <= 64;
    }
}
