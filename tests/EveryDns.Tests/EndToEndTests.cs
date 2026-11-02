using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using EveryDns.Services;

namespace EveryDns.Tests;

/// <summary>Runs two real Xray processes (a Shadowsocks server and the client config EveryDns generates) on this machine.</summary>
public class EndToEndTests
{
    private static readonly string Xray = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "core", "xray.exe"));

    [Fact]
    public async Task A_download_through_the_proxy_is_counted_by_the_stats_service()
    {
        if (!File.Exists(Xray)) return; // needs the downloaded core

        const int serverPort = 18388;
        string dir = Path.Combine(Path.GetTempPath(), "everydns-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Process? server = null, client = null;
        try
        {
            string serverConfig = new JsonObject
            {
                ["log"] = new JsonObject { ["loglevel"] = "warning" },
                ["inbounds"] = new JsonArray(new JsonObject
                {
                    ["listen"] = "127.0.0.1", ["port"] = serverPort, ["protocol"] = "shadowsocks",
                    ["settings"] = new JsonObject { ["method"] = "aes-256-gcm", ["password"] = "pw", ["network"] = "tcp,udp" },
                }),
                ["outbounds"] = new JsonArray(new JsonObject { ["protocol"] = "freedom" }),
            }.ToJsonString();
            File.WriteAllText(Path.Combine(dir, "server.json"), serverConfig);
            File.WriteAllText(Path.Combine(dir, "client.json"), ProxyLink.BuildConfig($"ss://YWVzLTI1Ni1nY206cHc@127.0.0.1:{serverPort}#local"));

            server = Start(Path.Combine(dir, "server.json"));
            client = Start(Path.Combine(dir, "client.json"));
            await Task.Delay(1500);
            Assert.False(server.HasExited || client.HasExited, "Xray did not stay up");

            // Xray's outbound refuses loopback and LAN targets, so this uses a real page; without internet there is nothing to check.
            using var http = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{ProxyLink.SocksPort}") }) { Timeout = TimeSpan.FromSeconds(15) };
            byte[] body;
            try { body = await http.GetByteArrayAsync("http://example.com/"); }
            catch (HttpRequestException) { return; }
            Assert.True(body.Length > 100);

            using var query = Process.Start(new ProcessStartInfo(Xray, $"api statsquery --server=127.0.0.1:{ProxyLink.ApiPort} -pattern \"outbound>>>proxy>>>traffic\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
            string json = query.StandardOutput.ReadToEnd();
            query.WaitForExit();

            long down = 0;
            foreach (var stat in JsonNode.Parse(json)!["stat"]!.AsArray())
                if (stat!["name"]!.ToString().EndsWith("downlink")) down = long.Parse(stat["value"]!.ToString());
            Assert.True(down >= body.Length, $"downlink counted {down} bytes for a {body.Length} byte page: {json}");
        }
        finally
        {
            foreach (var p in new[] { server, client }) { try { p?.Kill(true); } catch { } p?.Dispose(); }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static Process Start(string config) => Process.Start(new ProcessStartInfo(Xray, $"run -c \"{config}\"")
    {
        CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        Environment = { ["XRAY_LOCATION_ASSET"] = Path.GetDirectoryName(Xray)! },
    })!;
}