using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EveryDns.Services;

/// <summary>Measurements for the proxy side: server ping, delay and speed through the proxy, live throughput, subscriptions.</summary>
public static class ProxyTests
{
    /// <summary>Time to open a TCP connection to the server, in ms (null when it cannot be reached). This is what "ping" means for a proxy: most servers ignore ICMP.</summary>
    public static async Task<long?> TcpPingAsync(string host, int port, int timeoutMs = 3000)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host); // resolving is not part of the time
            using var cts = new CancellationTokenSource(timeoutMs);
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var clock = Stopwatch.StartNew();
            await socket.ConnectAsync(addresses, port, cts.Token);
            return Math.Max(1, clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException) { return null; }
    }

    private static HttpClient ThroughProxy(TimeSpan timeout) => new(new SocketsHttpHandler
    {
        Proxy = new WebProxy($"socks5://127.0.0.1:{ProxyLink.SocksPort}"),
        UseProxy = true,
    }) { Timeout = timeout };

    /// <summary>A real request through the running proxy (to a page that answers 204 at once), in ms.</summary>
    public static async Task<long?> DelayThroughProxyAsync()
    {
        try
        {
            using var http = ThroughProxy(TimeSpan.FromSeconds(8));
            var clock = Stopwatch.StartNew();
            using var response = await http.GetAsync("http://cp.cloudflare.com/generate_204");
            return response.IsSuccessStatusCode ? Math.Max(1, clock.ElapsedMilliseconds) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    /// <summary>Downloads for a few seconds through the proxy and reports the speed in megabits per second.</summary>
    public static async Task<double?> DownloadSpeedAsync(int seconds, CancellationToken ct)
    {
        try
        {
            using var http = ThroughProxy(TimeSpan.FromSeconds(seconds + 10));
            using var response = await http.GetAsync("https://speed.cloudflare.com/__down?bytes=100000000", HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[64 * 1024];
            long total = 0;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                int read = await body.ReadAsync(buffer, ct);
                if (read == 0) break;
                total += read;
            }
            return clock.Elapsed.TotalSeconds < 0.2 ? null : total * 8 / clock.Elapsed.TotalSeconds / 1_000_000;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return null; }
    }

    /// <summary>Downloads a subscription and returns its servers. Only http(s) addresses are fetched.</summary>
    public static async Task<(List<ProxyServer> Servers, List<string> Errors)> FetchSubscriptionAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FormatException("A subscription is an http or https address.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EveryDns");
        string body = await http.GetStringAsync(uri, ct);
        return ProxyLink.ParseMany(body);
    }
}

