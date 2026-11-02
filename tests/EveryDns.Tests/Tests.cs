using System.Diagnostics;
using System.Net;
using System.Text;
using EveryDns.Services;

namespace EveryDns.Tests;

public class ProxyLinkTests
{
    private const string Uuid = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public void Vless_reality_link_is_parsed()
    {
        var s = ProxyLink.Parse($"vless://{Uuid}@example.com:443?type=tcp&security=reality&sni=www.microsoft.com&pbk=KEY&sid=ab&flow=xtls-rprx-vision#My%20Server");
        Assert.Equal("vless", s.Protocol);
        Assert.Equal("My Server", s.Name);
        Assert.Equal("example.com", s.Address);
        Assert.Equal(443, s.Port);
    }

    [Fact]
    public void Vmess_link_is_parsed()
    {
        string json = $"{{\"v\":\"2\",\"ps\":\"vm\",\"add\":\"1.2.3.4\",\"port\":\"8443\",\"id\":\"{Uuid}\",\"aid\":\"0\",\"net\":\"ws\",\"tls\":\"tls\",\"host\":\"h.example.com\",\"path\":\"/ws\"}}";
        var s = ProxyLink.Parse("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(("vmess", "vm", "1.2.3.4", 8443), (s.Protocol, s.Name, s.Address, s.Port));
    }

    [Fact]
    public void Trojan_link_is_parsed()
    {
        var s = ProxyLink.Parse("trojan://pa%24%24word@t.example.com:443?sni=t.example.com#tr");
        Assert.Equal(("trojan", "tr", "t.example.com", 443), (s.Protocol, s.Name, s.Address, s.Port));
    }

    [Theory]
    [InlineData("ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ@5.6.7.8:8388#sip002")]          // base64(method:password)@host:port
    [InlineData("ss://YWVzLTI1Ni1nY206cGFzc3dvcmRANS42LjcuODo4Mzg4#legacy")]      // base64(method:password@host:port)
    public void Shadowsocks_both_forms_are_parsed(string link)
    {
        var s = ProxyLink.Parse(link);
        Assert.Equal(("shadowsocks", "5.6.7.8", 8388), (s.Protocol, s.Address, s.Port));
    }

    [Fact]
    public void Unknown_scheme_is_rejected() => Assert.Throws<FormatException>(() => ProxyLink.Parse("http://example.com"));

    [Fact]
    public void ParseMany_reads_a_base64_subscription_and_reports_bad_lines()
    {
        string body = $"vless://{Uuid}@a.example.com:443#a\nnot a link\ntrojan://p@b.example.com:443#b";
        var (servers, errors) = ProxyLink.ParseMany(Convert.ToBase64String(Encoding.UTF8.GetBytes(body)));
        Assert.Equal(new[] { "a", "b" }, servers.Select(s => s.Name));
        Assert.Single(errors);
    }

    [Theory]
    [InlineData("vless://11111111-2222-3333-4444-555555555555@example.com:443?type=ws&security=tls&path=%2Fws&host=example.com#x")]
    [InlineData("trojan://secret@t.example.com:443#x")]
    [InlineData("ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ@5.6.7.8:8388#x")]
    public void Generated_config_is_accepted_by_the_real_xray(string link)
    {
        string xray = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "core", "xray.exe"));
        if (!File.Exists(xray)) return; // the core is downloaded by build-installer.ps1; without it there is nothing to check against

        string config = Path.Combine(Path.GetTempPath(), $"everydns-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, ProxyLink.BuildConfig(link));
        try
        {
            using var p = Process.Start(new ProcessStartInfo(xray, $"run -test -c \"{config}\"") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, output);
        }
        finally { File.Delete(config); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Routing_modes_produce_a_config_the_real_xray_accepts(bool bypassIran)
    {
        string xray = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "core", "xray.exe"));
        if (!File.Exists(xray)) return;

        string config = Path.Combine(Path.GetTempPath(), $"everydns-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(config, ProxyLink.BuildConfig("trojan://secret@t.example.com:443#x", bypassIran, Path.Combine(Path.GetTempPath(), "everydns-test.log")));
        try
        {
            var start = new ProcessStartInfo(xray, $"run -test -c \"{config}\"") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.Environment["XRAY_LOCATION_ASSET"] = Path.GetDirectoryName(xray)!;
            using var p = Process.Start(start)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, output);
        }
        finally { File.Delete(config); }
    }

    [Fact]
    public void Store_does_not_add_the_same_link_twice()
    {
        var store = new ProxyStore();
        Assert.Equal(1, store.Add(new[] { ProxyLink.Parse("trojan://p@b.example.com:443#b") }));
        Assert.Equal(0, store.Add(new[] { ProxyLink.Parse("trojan://p@b.example.com:443#b") }));
    }
}

public class QrTests
{
    [Fact]
    public void A_server_link_survives_a_trip_through_a_qr_code()
    {
        string link = "vless://11111111-2222-3333-4444-555555555555@example.com:443?type=ws&security=tls&path=%2Fws&host=example.com&sni=example.com#Test%20Server";
        using var image = new System.Drawing.Bitmap(new MemoryStream(QrTools.ToPng(link)));
        Assert.Equal(new[] { link }, QrTools.Decode(image));
    }
}

public class DnsTests
{
    [Fact]
    public void Query_has_the_expected_wire_format()
    {
        byte[] q = DnsClient.BuildQuery(0x1234, "example.com", RecordType.A);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 }, q[..12]);
        Assert.Equal(new byte[] { 7, 101, 120, 97, 109, 112, 108, 101, 3, 99, 111, 109, 0, 0, 1, 0, 1 }, q[12..]);
    }

    [Fact]
    public void Response_with_a_compressed_name_is_parsed()
    {
        var r = DnsClient.BuildQuery(7, "example.com", RecordType.A).ToList();
        r[2] = 0x81; r[3] = 0x80; r[7] = 1; // a response with one answer
        r.AddRange(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 93, 184, 216, 34 });
        var reply = DnsClient.ParseResponse(r.ToArray(), 7, TimeSpan.FromMilliseconds(12));
        Assert.NotNull(reply);
        Assert.True(reply!.Ok);
        Assert.Equal("93.184.216.34", reply.Answers.Single().Data);
        Assert.Equal("example.com", reply.Answers.Single().Name);
    }

    [Fact]
    public void Response_with_another_id_is_ignored() =>
        Assert.Null(DnsClient.ParseResponse(new byte[] { 0, 9, 0x81, 0x80, 0, 0, 0, 0, 0, 0, 0, 0 }, 7, TimeSpan.Zero));

    [Fact]
    public void Nxdomain_is_reported_as_failed()
    {
        var reply = DnsClient.ParseResponse(new byte[] { 0, 7, 0x81, 0x83, 0, 0, 0, 0, 0, 0, 0, 0 }, 7, TimeSpan.Zero);
        Assert.False(reply!.Ok);
        Assert.Equal("Name does not exist", reply.Status);
    }

    [Fact]
    public void Pointer_loop_does_not_hang()
    {
        byte[] loop = { 0, 7, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 0x0C, 0, 1, 0, 1 };
        DnsClient.ParseResponse(loop, 7, TimeSpan.Zero); // must return, not spin
    }

    [Theory]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("1.1.1.1; calc", false)]
    [InlineData("", false)]
    public void Server_address_validation(string text, bool valid) => Assert.Equal(valid, DnsPresets.TryParseServer(text, out _));

    [Fact]
    public void Every_builtin_preset_has_valid_addresses()
    {
        foreach (var preset in DnsPresets.Builtin)
            foreach (string address in preset.All) Assert.True(IPAddress.TryParse(address, out _), $"{preset.Name}: {address}");
    }

    [Fact]
    public void Preset_matches_the_servers_in_use() =>
        Assert.True(DnsPresets.Builtin[0].Matches(new[] { IPAddress.Parse("1.0.0.1"), IPAddress.Parse("1.1.1.1") }));
}
