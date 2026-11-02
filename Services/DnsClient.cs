using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EveryDns.Services;

public enum RecordType : ushort { A = 1, NS = 2, CNAME = 5, MX = 15, TXT = 16, AAAA = 28 }

public sealed record DnsAnswer(string Name, string Type, uint Ttl, string Data);

/// <summary>What a DNS server said, and how long it took. <see cref="Ok"/> is false for a timeout, a bad reply or an error code.</summary>
public sealed record DnsReply(bool Ok, TimeSpan Time, string Status, IReadOnlyList<DnsAnswer> Answers)
{
    public static DnsReply Failed(TimeSpan time, string status) => new(false, time, status, Array.Empty<DnsAnswer>());
}

/// <summary>A tiny UDP DNS client: enough to time a lookup against one specific server and read the records back.</summary>
public static class DnsClient
{
    private static readonly string[] RcodeNames = { "No error", "Format error", "Server failure", "Name does not exist", "Not implemented", "Refused" };

    public static byte[] BuildQuery(ushort id, string name, RecordType type)
    {
        var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        U16(id);
        U16(0x0100); // standard query, recursion desired
        U16(1); U16(0); U16(0); U16(0);

        string ascii = new IdnMapping().GetAscii(name.Trim().TrimEnd('.'));
        if (ascii.Length == 0 || ascii.Length > 253) throw new ArgumentException("Enter a valid domain name.");
        foreach (string label in ascii.Split('.'))
        {
            if (label.Length is 0 or > 63) throw new ArgumentException("Enter a valid domain name.");
            ms.WriteByte((byte)label.Length);
            ms.Write(Encoding.ASCII.GetBytes(label));
        }
        ms.WriteByte(0);
        U16((ushort)type);
        U16(1); // class IN
        return ms.ToArray();
    }

    /// <summary>Reads a reply. Returns null when the bytes are not a response to the query with this id.</summary>
    public static DnsReply? ParseResponse(byte[] d, ushort expectedId, TimeSpan time)
    {
        if (d.Length < 12 || ((d[0] << 8) | d[1]) != expectedId || (d[2] & 0x80) == 0) return null;

        int rcode = d[3] & 0x0F;
        int qd = (d[4] << 8) | d[5], an = (d[6] << 8) | d[7];
        string status = rcode < RcodeNames.Length ? RcodeNames[rcode] : $"Error {rcode}";
        var answers = new List<DnsAnswer>();
        try
        {
            int pos = 12;
            for (int i = 0; i < qd; i++) { ReadName(d, ref pos); pos += 4; }
            for (int i = 0; i < an; i++)
            {
                string name = ReadName(d, ref pos);
                int type = (d[pos] << 8) | d[pos + 1];
                uint ttl = (uint)((d[pos + 4] << 24) | (d[pos + 5] << 16) | (d[pos + 6] << 8) | d[pos + 7]);
                int len = (d[pos + 8] << 8) | d[pos + 9];
                pos += 10;
                if (pos + len > d.Length) break;
                answers.Add(new DnsAnswer(name, TypeName(type), ttl, ReadData(d, type, pos, len)));
                pos += len;
            }
        }
        catch (IndexOutOfRangeException) { /* a cut-off reply: keep what was read */ }

        return new DnsReply(rcode == 0, time, status, answers);
    }

    public static async Task<DnsReply> QueryAsync(IPAddress server, string name, RecordType type, int timeoutMs, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        ushort id = (ushort)Random.Shared.Next(0, 65536);
        byte[] query;
        try { query = BuildQuery(id, name, type); }
        catch (ArgumentException ex) { return DnsReply.Failed(TimeSpan.Zero, ex.Message); }

        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            udp.Connect(server, 53);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            await udp.SendAsync(query, timeout.Token);
            while (true)
            {
                var received = await udp.ReceiveAsync(timeout.Token);
                var reply = ParseResponse(received.Buffer, id, clock.Elapsed);
                if (reply != null) return reply; // anything else was a stray packet: keep waiting for ours
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return DnsReply.Failed(clock.Elapsed, "No answer"); }
        catch (SocketException ex) { return DnsReply.Failed(clock.Elapsed, ex.SocketErrorCode == SocketError.ConnectionRefused ? "Refused" : "Network error"); }
    }

    private static string TypeName(int type) => Enum.IsDefined(typeof(RecordType), (ushort)type) ? ((RecordType)type).ToString() : $"TYPE{type}";

    private static string ReadData(byte[] d, int type, int pos, int len)
    {
        switch ((RecordType)type)
        {
            case RecordType.A when len == 4: return new IPAddress(d.AsSpan(pos, 4)).ToString();
            case RecordType.AAAA when len == 16: return new IPAddress(d.AsSpan(pos, 16)).ToString();
            case RecordType.CNAME:
            case RecordType.NS:
            { int p = pos; return ReadName(d, ref p); }
            case RecordType.MX when len > 2:
            { int p = pos + 2; return $"{(d[pos] << 8) | d[pos + 1]} {ReadName(d, ref p)}"; }
            case RecordType.TXT:
            {
                var sb = new StringBuilder();
                for (int p = pos; p < pos + len;)
                {
                    int n = d[p++];
                    sb.Append(Encoding.UTF8.GetString(d, p, Math.Min(n, pos + len - p)));
                    p += n;
                }
                return sb.ToString();
            }
            default: return $"{len} bytes";
        }
    }

    /// <summary>Reads a possibly compressed name and moves <paramref name="pos"/> past it.</summary>
    private static string ReadName(byte[] d, ref int pos)
    {
        var sb = new StringBuilder();
        int p = pos, jumps = 0;
        bool jumped = false;
        while (true)
        {
            int n = d[p];
            if (n == 0) { p++; break; }
            if ((n & 0xC0) == 0xC0)
            {
                if (++jumps > 20) throw new IndexOutOfRangeException(); // a pointer loop
                int target = ((n & 0x3F) << 8) | d[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = target;
                continue;
            }
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(d, p + 1, n));
            p += n + 1;
        }
        if (!jumped) pos = p;
        return sb.ToString();
    }
}
