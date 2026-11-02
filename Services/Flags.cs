using System.Net;
using System.Text.Json.Nodes;

namespace EveryDns.Services;

/// <summary>
/// Works out which country a server is in and fetches that country's flag.
/// The server's IP address is sent to ipwho.is for the country code, and flags come from flagcdn.com; both answers are
/// kept on disk, so each server and each flag is asked about once.
/// </summary>
public static class Flags
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static string CacheFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveryDns", "flags");

    /// <summary>The two-letter country code for a host name or address, or "" when it cannot be found.</summary>
    public static async Task<string> CountryOfAsync(string host)
    {
        try
        {
            var address = IPAddress.TryParse(host, out var ip) ? ip : (await Dns.GetHostAddressesAsync(host)).FirstOrDefault();
            if (address == null || IPAddress.IsLoopback(address)) return "";
            var json = JsonNode.Parse(await Http.GetStringAsync($"https://ipwho.is/{address}?fields=success,country_code"));
            string code = json?["country_code"]?.ToString().ToLowerInvariant() ?? "";
            return json?["success"]?.GetValue<bool>() == true && code.Length == 2 && code.All(char.IsAsciiLetterLower) ? code : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException) { return ""; }
    }

    /// <summary>Path of the flag image for a country code (downloaded the first time), or null when it is not available.</summary>
    public static async Task<string?> FlagFileAsync(string code)
    {
        if (code.Length != 2 || !code.All(char.IsAsciiLetterLower)) return null;
        string path = Path.Combine(CacheFolder, code + ".png");
        if (File.Exists(path)) return path;
        try
        {
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllBytes(path, await Http.GetByteArrayAsync($"https://flagcdn.com/w80/{code}.png"));
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return null; }
    }
}
