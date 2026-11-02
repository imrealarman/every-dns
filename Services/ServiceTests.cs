using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace EveryDns.Services;

public sealed record ServiceSite(string Name, string Group, string Url)
{
    public string Host => new Uri(Url).Host;
}

public sealed record ServiceResult(bool Reachable, long Ms, string Text);

/// <summary>Checks whether popular sites and APIs can be reached from this PC, and says why not when they cannot.</summary>
public static class ServiceTests
{
    public static readonly IReadOnlyList<ServiceSite> Sites = new ServiceSite[]
    {
        new("ChatGPT", "AI", "https://chatgpt.com"),
        new("OpenAI API", "AI", "https://api.openai.com"),
        new("Claude", "AI", "https://claude.ai"),
        new("Anthropic API", "AI", "https://api.anthropic.com"),
        new("Gemini", "AI", "https://gemini.google.com"),
        new("Google", "Google", "https://www.google.com"),
        new("Google Developers", "Google", "https://developers.google.com"),
        new("Google Play", "Google", "https://play.google.com"),
        new("YouTube", "Google", "https://www.youtube.com"),
        new("GitHub", "Developer", "https://github.com"),
        new("npm", "Developer", "https://registry.npmjs.org"),
        new("PyPI", "Developer", "https://pypi.org"),
        new("NuGet", "Developer", "https://api.nuget.org/v3/index.json"),
        new("Docker Hub", "Developer", "https://hub.docker.com"),
        new("Stack Overflow", "Developer", "https://stackoverflow.com"),
        new("Microsoft", "Other", "https://www.microsoft.com"),
        new("Telegram", "Other", "https://web.telegram.org"),
        new("Instagram", "Other", "https://www.instagram.com"),
        new("X", "Other", "https://x.com"),
        new("Wikipedia", "Other", "https://www.wikipedia.org"),
    };

    /// <summary>
    /// One request to the site. Any HTTP answer counts as reachable (a 403 from a bot filter still means the site
    /// answered); only a failure to connect, resolve, or finish the TLS handshake counts as blocked.
    /// </summary>
    public static async Task<ServiceResult> TestAsync(ServiceSite site, bool viaLocalProxy, CancellationToken ct = default)
    {
        // A fresh handler each time, so the current Windows proxy setting is always the one used.
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) };
        if (viaLocalProxy) handler.Proxy = new WebProxy($"http://127.0.0.1:{ProxyLink.HttpPort}");
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) EveryDns");

        var clock = Stopwatch.StartNew();
        try
        {
            using var response = await http.GetAsync(site.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            long ms = Math.Max(1, clock.ElapsedMilliseconds);
            return new ServiceResult(true, ms, $"{ms} ms");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { return new ServiceResult(false, 0, "Timed out"); }
        catch (HttpRequestException ex) { return new ServiceResult(false, 0, Describe(ex)); }
    }

    private static string Describe(HttpRequestException ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData }) return "DNS failed";
            if (e is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) return "Refused";
            if (e is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted }) return "Blocked (reset)";
            if (e is SocketException { SocketErrorCode: SocketError.TimedOut }) return "Timed out";
            if (e is System.Security.Authentication.AuthenticationException) return "TLS failed";
        }
        return "Unreachable";
    }
}
