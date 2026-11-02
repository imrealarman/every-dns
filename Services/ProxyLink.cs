using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace EveryDns.Services;

/// <summary>A server parsed from a share link. <see cref="Outbound"/> is the Xray outbound that reaches it.</summary>
public sealed class ProxyServer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string Raw { get; set; } = "";
    /// <summary>Id of the subscription this came from; empty for servers added by hand.</summary>
    public string Subscription { get; set; } = "";
    /// <summary>Two-letter country code of the server (from its IP address), or empty until looked up.</summary>
    public string Country { get; set; } = "";
}

/// <summary>Share links (vless://, vmess://, trojan://, ss://) to servers and Xray configs. The same rules as the Every Proxy extension.</summary>
public static class ProxyLink
{
    public const int SocksPort = 10808;
    public const int HttpPort = 10809;
    public const int ApiPort = 10810;

    public static ProxyServer Parse(string raw)
    {
        string link = raw.Trim();
        var (server, _) = ParseWithOutbound(link);
        return server;
    }

    public static (ProxyServer Server, JsonObject Outbound) ParseWithOutbound(string link)
    {
        link = link.Trim();
        (string name, string protocol, string address, int port, JsonObject outbound) r;
        if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) r = Vmess(link);
        else if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) r = UrlStyle(link, "vless");
        else if (link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) r = UrlStyle(link, "trojan");
        else if (link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) r = Shadowsocks(link);
        else throw new FormatException("Use a vless://, vmess://, trojan:// or ss:// link.");

        if (string.IsNullOrWhiteSpace(r.address) || r.port is < 1 or > 65535) throw new FormatException("The link has no valid host or port.");
        var server = new ProxyServer { Name = r.name, Protocol = r.protocol, Address = r.address, Port = r.port, Raw = link };
        return (server, r.outbound);
    }

    /// <summary>Splits pasted text, or a subscription body (links one per line, optionally base64), into servers. Bad lines are reported, not fatal.</summary>
    public static (List<ProxyServer> Servers, List<string> Errors) ParseMany(string text)
    {
        string body = text.Trim();
        if (!body.Contains("://"))
        {
            try { body = B64(body); } catch (FormatException) { /* not base64: fall through and report it */ }
        }
        var servers = new List<ProxyServer>();
        var errors = new List<string>();
        foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { servers.Add(Parse(line)); }
            catch (Exception ex) when (ex is FormatException or UriFormatException or System.Text.Json.JsonException or ArgumentException)
            {
                errors.Add($"{(line.Length > 40 ? line[..40] + "…" : line)}: {ex.Message}");
            }
        }
        return (servers, errors);
    }

    /// <summary>A full Xray config: a local SOCKS and HTTP inbound, and the chosen server as the way out.</summary>
    public static string BuildConfig(string rawLink, bool bypassIran = false, string? logFile = null, int socksPort = SocksPort, int httpPort = HttpPort)
    {
        var (_, outbound) = ParseWithOutbound(rawLink);
        outbound["tag"] = "proxy";
        var config = new JsonObject
        {
            ["log"] = LogSettings(logFile),
            // The stats service counts the bytes through the "proxy" outbound; EveryDns reads it for the data-used numbers.
            ["stats"] = new JsonObject(),
            ["api"] = new JsonObject { ["tag"] = "api", ["services"] = new JsonArray("StatsService") },
            ["policy"] = new JsonObject { ["system"] = new JsonObject { ["statsOutboundUplink"] = true, ["statsOutboundDownlink"] = true } },
            ["routing"] = RoutingRules(bypassIran),
            ["inbounds"] = new JsonArray
            {
                Inbound("socks-in", socksPort, "socks", new JsonObject { ["udp"] = true, ["auth"] = "noauth" }),
                Inbound("http-in", httpPort, "http", new JsonObject()),
                Inbound("api-in", ApiPort, "dokodemo-door", new JsonObject { ["address"] = "127.0.0.1" }, sniff: false),
            },
            ["outbounds"] = new JsonArray { outbound, new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" } },
        };
        return config.ToJsonString();
    }


    private static JsonObject LogSettings(string? file)
    {
        var log = new JsonObject { ["loglevel"] = "warning" };
        if (!string.IsNullOrEmpty(file)) log["error"] = file; // read back by the Logs window
        return log;
    }

    /// <summary>
    /// Addresses on the local network always go direct (the server would refuse them anyway). With <paramref name="bypassIran"/>,
    /// Iranian sites and .ir names go direct too, so they stay fast and keep working.
    /// </summary>
    private static JsonObject RoutingRules(bool bypassIran)
    {
        var rules = new JsonArray
        {
            new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("api-in"), ["outboundTag"] = "api" },
            new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("geoip:private"), ["outboundTag"] = "direct" },
        };
        if (bypassIran)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("regexp:\\.ir$", "geosite:category-ir"), ["outboundTag"] = "direct" });
            rules.Add(new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("geoip:ir"), ["outboundTag"] = "direct" });
        }
        return new JsonObject { ["domainStrategy"] = bypassIran ? "IPIfNonMatch" : "AsIs", ["rules"] = rules };
    }
    private static JsonObject Inbound(string tag, int port, string protocol, JsonObject settings, bool sniff = true)
    {
        var inbound = new JsonObject
        {
            ["tag"] = tag,
            ["listen"] = "127.0.0.1",
            ["port"] = port,
            ["protocol"] = protocol,
            ["settings"] = settings,
        };
        if (sniff) inbound["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls") };
        return inbound;
    }

    // ---- per protocol ------------------------------------------------------------------------------------------

    private static (string, string, string, int, JsonObject) Vmess(string link)
    {
        var j = JsonNode.Parse(B64(link["vmess://".Length..]))!.AsObject();
        string address = Str(j["add"]);
        int port = int.TryParse(Str(j["port"]), out int p) ? p : 0;
        var stream = Stream(new StreamOptions
        {
            Net = Str(j["net"]), Security = Str(j["tls"]) == "tls" ? "tls" : "none", Host = Str(j["host"]), Path = Str(j["path"]),
            Sni = Str(j["sni"]), Alpn = Str(j["alpn"]), Fp = Str(j["fp"]), HeaderType = Str(j["type"]),
            ServiceName = Str(j["path"]), Mode = Str(j["type"]) == "multi" ? "multi" : "", Address = address,
        });
        var outbound = new JsonObject
        {
            ["protocol"] = "vmess",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = address,
                    ["port"] = port,
                    ["users"] = new JsonArray(new JsonObject
                    {
                        ["id"] = Str(j["id"]),
                        ["alterId"] = int.TryParse(Str(j["aid"]), out int aid) ? aid : 0,
                        ["security"] = Str(j["scy"]) is { Length: > 0 } scy ? scy : "auto",
                    }),
                }),
            },
            ["streamSettings"] = stream,
        };
        string name = Str(j["ps"]);
        return (name.Length > 0 ? name : $"{address}:{port}", "vmess", address, port, outbound);
    }

    private static (string, string, string, int, JsonObject) UrlStyle(string link, string protocol)
    {
        var u = new Uri(link);
        var q = HttpUtility.ParseQueryString(u.Query);
        string address = u.Host.Trim('[', ']');
        int port = u.Port > 0 ? u.Port : 443;
        string secret = Uri.UnescapeDataString(u.UserInfo);
        string name = Uri.UnescapeDataString(u.Fragment.TrimStart('#'));
        if (name.Length == 0) name = $"{address}:{port}";

        var stream = Stream(new StreamOptions
        {
            Net = q["type"] ?? "tcp",
            Security = q["security"] ?? (protocol == "trojan" ? "tls" : "none"),
            Host = q["host"] ?? "", Path = q["path"] ?? "", Sni = q["sni"] ?? q["peer"] ?? "", Alpn = q["alpn"] ?? "", Fp = q["fp"] ?? "",
            AllowInsecure = q["allowInsecure"] is "1" or "true" || q["insecure"] is "1" or "true",
            ServiceName = q["serviceName"] ?? "", Mode = q["mode"] ?? "", HeaderType = q["headerType"] ?? "",
            Pbk = q["pbk"] ?? "", Sid = q["sid"] ?? "", Spx = q["spx"] ?? "", Address = address,
        });

        JsonObject outbound;
        if (protocol == "vless")
        {
            var user = new JsonObject { ["id"] = secret, ["encryption"] = q["encryption"] ?? "none" };
            if (!string.IsNullOrEmpty(q["flow"])) user["flow"] = q["flow"];
            outbound = new JsonObject
            {
                ["protocol"] = "vless",
                ["settings"] = new JsonObject { ["vnext"] = new JsonArray(new JsonObject { ["address"] = address, ["port"] = port, ["users"] = new JsonArray(user) }) },
                ["streamSettings"] = stream,
            };
        }
        else
        {
            outbound = new JsonObject
            {
                ["protocol"] = "trojan",
                ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = address, ["port"] = port, ["password"] = secret }) },
                ["streamSettings"] = stream,
            };
        }
        return (name, protocol, address, port, outbound);
    }

    private static (string, string, string, int, JsonObject) Shadowsocks(string link)
    {
        string rest = link["ss://".Length..];
        string name = "";
        int hash = rest.IndexOf('#');
        if (hash >= 0) { name = Uri.UnescapeDataString(rest[(hash + 1)..]); rest = rest[..hash]; }
        int query = rest.IndexOf('?');
        if (query >= 0) rest = rest[..query];

        string userInfo, hostPort;
        int at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            // SIP002: base64(method:password)@host:port, or the plain form
            userInfo = rest[..at];
            hostPort = rest[(at + 1)..];
            userInfo = userInfo.Contains(':') ? Uri.UnescapeDataString(userInfo) : B64(userInfo);
        }
        else
        {
            // legacy: base64(method:password@host:port)
            string decoded = B64(rest);
            int dat = decoded.LastIndexOf('@');
            if (dat < 0) throw new FormatException("Not a valid ss:// link.");
            userInfo = decoded[..dat];
            hostPort = decoded[(dat + 1)..];
        }
        int colon = userInfo.IndexOf(':');
        int portAt = hostPort.LastIndexOf(':');
        if (colon < 0 || portAt < 0) throw new FormatException("Not a valid ss:// link.");

        string method = userInfo[..colon], password = userInfo[(colon + 1)..];
        string address = hostPort[..portAt].Trim('[', ']');
        int port = int.TryParse(hostPort[(portAt + 1)..], out int p) ? p : 0;
        var outbound = new JsonObject
        {
            ["protocol"] = "shadowsocks",
            ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = address, ["port"] = port, ["method"] = method, ["password"] = password }) },
        };
        return (name.Length > 0 ? name : $"{address}:{port}", "shadowsocks", address, port, outbound);
    }

    // ---- transport and security --------------------------------------------------------------------------------

    private sealed class StreamOptions
    {
        public string Net = "tcp", Security = "none", Host = "", Path = "", Sni = "", Alpn = "", Fp = "", HeaderType = "";
        public string ServiceName = "", Mode = "", Pbk = "", Sid = "", Spx = "", Address = "";
        public bool AllowInsecure;
    }

    private static JsonObject Stream(StreamOptions o)
    {
        string net = (o.Net.Length > 0 ? o.Net : "tcp").ToLowerInvariant();
        string security = (o.Security.Length > 0 ? o.Security : "none").ToLowerInvariant();
        string path = o.Path.Length > 0 ? o.Path : "/";
        var s = new JsonObject { ["network"] = net == "http" ? "h2" : net, ["security"] = security };

        switch (net)
        {
            case "ws":
                s["wsSettings"] = new JsonObject { ["path"] = path, ["headers"] = o.Host.Length > 0 ? new JsonObject { ["Host"] = o.Host } : new JsonObject() };
                break;
            case "grpc":
                s["grpcSettings"] = new JsonObject { ["serviceName"] = o.ServiceName, ["multiMode"] = o.Mode == "multi" };
                break;
            case "h2" or "http":
                s["httpSettings"] = new JsonObject { ["path"] = path, ["host"] = new JsonArray(o.Host.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(h => (JsonNode)h).ToArray()) };
                break;
            case "httpupgrade":
                s["httpupgradeSettings"] = new JsonObject { ["path"] = path, ["host"] = o.Host };
                break;
            case "xhttp" or "splithttp":
                s["network"] = "xhttp";
                s["xhttpSettings"] = new JsonObject { ["path"] = path, ["host"] = o.Host, ["mode"] = o.Mode.Length > 0 ? o.Mode : "auto" };
                break;
            case "tcp" when o.HeaderType == "http":
                s["tcpSettings"] = new JsonObject
                {
                    ["header"] = new JsonObject
                    {
                        ["type"] = "http",
                        ["request"] = new JsonObject
                        {
                            ["path"] = new JsonArray(path.Split(',').Select(x => (JsonNode)x).ToArray()),
                            ["headers"] = o.Host.Length > 0 ? new JsonObject { ["Host"] = new JsonArray(o.Host.Split(',').Select(x => (JsonNode)x).ToArray()) } : new JsonObject(),
                        },
                    },
                };
                break;
        }

        if (security == "tls")
        {
            var tls = new JsonObject
            {
                ["serverName"] = o.Sni.Length > 0 ? o.Sni : o.Host.Length > 0 ? o.Host : o.Address,
                ["allowInsecure"] = o.AllowInsecure,
            };
            if (o.Fp.Length > 0) tls["fingerprint"] = o.Fp;
            if (o.Alpn.Length > 0) tls["alpn"] = new JsonArray(o.Alpn.Split(',').Select(x => (JsonNode)x).ToArray());
            s["tlsSettings"] = tls;
        }
        else if (security == "reality")
        {
            s["realitySettings"] = new JsonObject
            {
                ["serverName"] = o.Sni, ["fingerprint"] = o.Fp.Length > 0 ? o.Fp : "chrome",
                ["publicKey"] = o.Pbk, ["shortId"] = o.Sid, ["spiderX"] = o.Spx,
            };
        }
        return s;
    }

    private static string Str(JsonNode? node) => node?.ToString() ?? "";

    private static string B64(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/');
        while (s.Length % 4 != 0) s += '=';
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}
