using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace EveryDns.Services;

/// <summary>One network adapter and the DNS servers Windows is using on it right now.</summary>
public sealed class AdapterInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Kind { get; init; } = "";
    public long SpeedBps { get; init; }
    public IPAddress? IPv4 { get; init; }
    public IPAddress? Gateway { get; init; }
    public IReadOnlyList<IPAddress> Dns { get; init; } = Array.Empty<IPAddress>();
    /// <summary>True when the DNS servers were typed in (or set by an app), false when they come from the router (DHCP).</summary>
    public bool StaticV4 { get; init; }
    public bool StaticV6 { get; init; }

    public bool Automatic => !StaticV4 && !StaticV6;
    public string Display => $"{Name}  ·  {Kind}";

    public string Details
    {
        get
        {
            string speed = SpeedBps <= 0 ? "" : SpeedBps >= 1_000_000_000 ? $"{SpeedBps / 1_000_000_000.0:0.#} Gbps" : $"{SpeedBps / 1_000_000} Mbps";
            return string.Join("  ·  ", new[] { Kind, speed, IPv4?.ToString() is { } ip ? "IP " + ip : "", Gateway?.ToString() is { } gw ? "Router " + gw : "" }.Where(s => s.Length > 0));
        }
    }
}

public static class Adapters
{
    /// <summary>Adapters that are connected, router-facing ones first.</summary>
    public static List<AdapterInfo> Read()
    {
        var list = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var props = ni.GetIPProperties();
            var ipv4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
            var gateway = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (ipv4 == null && props.UnicastAddresses.Count == 0) continue;

            list.Add(new AdapterInfo
            {
                Id = ni.Id,
                Name = ni.Name,
                Description = ni.Description,
                Kind = ni.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "Ethernet",
                    var other => other.ToString(),
                },
                SpeedBps = ni.Speed,
                IPv4 = ipv4,
                Gateway = gateway,
                // Windows lists a placeholder site-local address (fec0:0:0:ffff::1) when there is no IPv6 DNS; it is not a real server.
                Dns = props.DnsAddresses.Where(a => !a.IsIPv6SiteLocal).ToList(),
                StaticV4 = HasStaticDns(ni.Id, "Tcpip"),
                StaticV6 = HasStaticDns(ni.Id, "Tcpip6"),
            });
        }
        return list.OrderByDescending(a => a.Gateway != null).ThenBy(a => a.Name).ToList();
    }

    /// <summary>Static DNS is stored per adapter as a NameServer value; an empty value means the router decides.</summary>
    private static bool HasStaticDns(string id, string stack)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{stack}\Parameters\Interfaces\{id}");
            return !string.IsNullOrWhiteSpace(key?.GetValue("NameServer") as string);
        }
        catch (Exception ex) { Log.Write("Could not read the DNS setting of " + id, ex); return false; }
    }
}
