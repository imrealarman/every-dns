using System.Net;
using System.Net.NetworkInformation;

namespace EveryDns.Services;

/// <summary>Round-trip numbers over a run of pings. Times are in milliseconds.</summary>
public sealed record PingStats(int Sent, int Received, double Min, double Avg, double Max, double Jitter)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;

    public static PingStats From(IReadOnlyList<long?> samples)
    {
        var ok = samples.Where(s => s.HasValue).Select(s => (double)s!.Value).ToList();
        if (ok.Count == 0) return new PingStats(samples.Count, 0, 0, 0, 0, 0);
        // Jitter: how much the time moves from one reply to the next.
        double jitter = ok.Count < 2 ? 0 : Enumerable.Range(1, ok.Count - 1).Average(i => Math.Abs(ok[i] - ok[i - 1]));
        return new PingStats(samples.Count, ok.Count, ok.Min(), ok.Average(), ok.Max(), jitter);
    }
}

public static class Probe
{
    /// <summary>One echo request. Returns the round trip in ms, or null when nothing came back.</summary>
    public static async Task<long?> PingAsync(IPAddress address, int timeoutMs = 1500)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeoutMs);
            return reply.Status == IPStatus.Success ? Math.Max(1, reply.RoundtripTime) : null;
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException or System.Net.Sockets.SocketException) { return null; }
    }

    public static async Task<PingStats> PingManyAsync(IPAddress address, int count, int timeoutMs = 1500)
    {
        var samples = new List<long?>();
        for (int i = 0; i < count; i++) samples.Add(await PingAsync(address, timeoutMs));
        return PingStats.From(samples);
    }
}
