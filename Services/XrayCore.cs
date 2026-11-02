using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EveryDns.Services;

/// <summary>
/// Runs Xray-core as a local SOCKS/HTTP proxy for the chosen server. A copy ships with the app (the "core" folder
/// next to the exe). Updates come from the Xray-core GitHub releases, are checked against the SHA-256 published
/// next to them and go to %AppData%\EveryDns\core, which then wins over the bundled copy.
/// </summary>
public sealed class XrayCore
{
    private const string ReleasesApi = "https://api.github.com/repos/XTLS/Xray-core/releases/latest";
    private const string DownloadBase = "https://github.com/XTLS/Xray-core/releases/download/";

    private Process? process;

    private static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveryDns");
    public static string LogPath => Path.Combine(DataFolder, "core.log");
    private static string BundledExe => Path.Combine(AppContext.BaseDirectory, "core", "xray.exe");
    private static string UpdatedExe => Path.Combine(DataFolder, "core", "xray.exe");

    /// <summary>The updated core if there is one, else the one that came with the app.</summary>
    public static string ExePath => File.Exists(UpdatedExe) ? UpdatedExe : BundledExe;
    public static bool IsInstalled => File.Exists(ExePath);
    public bool IsRunning => process is { HasExited: false };

    // ---- version and updates -----------------------------------------------------------------------------------

    /// <summary>The installed core's version, like "26.10.31" (null when it is missing or will not run).</summary>
    public static async Task<string?> GetVersionAsync()
    {
        if (!IsInstalled) return null;
        try
        {
            var (_, output) = await RunAsync(ExePath, "version");
            var m = Regex.Match(output, @"Xray\s+(\d+\.\d+\.\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (Exception ex) { Log.Write("Could not read the core version", ex); return null; }
    }

    /// <summary>The newest release on GitHub, like "26.10.31".</summary>
    public static async Task<string> GetLatestVersionAsync(CancellationToken ct = default)
    {
        using var http = NewHttp();
        var json = JsonNode.Parse(await http.GetStringAsync(ReleasesApi, ct));
        return (json?["tag_name"]?.ToString() ?? throw new InvalidDataException("GitHub did not name a release.")).TrimStart('v');
    }

    public static bool IsNewer(string latest, string? installed) =>
        installed == null || (Version.TryParse(latest, out var l) && Version.TryParse(installed, out var i) && l > i);

    /// <summary>Downloads that release, checks it and installs it. The core must not be running.</summary>
    public static async Task InstallAsync(string version, IProgress<string> progress, CancellationToken ct = default)
    {
        string url = $"{DownloadBase}v{version}/Xray-windows-64.zip";
        using var http = NewHttp();

        progress.Report($"Downloading core {version}…");
        byte[] zip = await http.GetByteArrayAsync(url, ct);
        string digest = await http.GetStringAsync(url + ".dgst", ct);

        var match = Regex.Match(digest, @"SHA2-256\s*=\s*([0-9a-fA-F]{64})");
        if (!match.Success) throw new InvalidDataException("The download has no checksum, so it was not installed.");
        if (!Convert.ToHexString(SHA256.HashData(zip)).Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The download does not match its checksum, so it was not installed.");

        progress.Report("Installing…");
        string folder = Path.GetDirectoryName(UpdatedExe)!;
        Directory.CreateDirectory(folder);
        using var archive = new ZipArchive(new MemoryStream(zip));
        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length == 0) continue; // a folder entry
            // Only plain file names: nothing in the archive can choose where it is written.
            entry.ExtractToFile(Path.Combine(folder, Path.GetFileName(entry.FullName)), overwrite: true);
        }
        if (!File.Exists(UpdatedExe)) throw new FileNotFoundException("xray.exe was not in the download.");
    }

    private static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EveryDns");
        return http;
    }

    // ---- running -----------------------------------------------------------------------------------------------

    /// <summary>Starts Xray with this config, replacing a running one. Throws with Xray's own message if it cannot start.</summary>
    public async Task StartAsync(string configJson)
    {
        Stop();
        KillStrays();
        Directory.CreateDirectory(DataFolder);
        string configPath = Path.Combine(DataFolder, "config.json");
        await File.WriteAllTextAsync(configPath, configJson);
        try { File.Delete(LogPath); } catch (IOException) { /* the log is only a convenience */ } // one log per connection

        string exe = ExePath;
        var start = new ProcessStartInfo(exe, $"run -c \"{configPath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["XRAY_LOCATION_ASSET"] = Path.GetDirectoryName(exe)!; // geoip.dat / geosite.dat sit beside the exe
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the proxy core.");

        await Task.Delay(900); // a bad config or a busy port makes Xray quit straight away
        if (process.HasExited)
        {
            string output = (await process.StandardError.ReadToEndAsync() + await process.StandardOutput.ReadToEndAsync()).Trim();
            process = null;
            throw new InvalidOperationException(output.Length > 0 ? output.Split('\n').Last().Trim() : "The proxy core stopped right away.");
        }
        // Nobody reads the pipes after the start check; drain them so a chatty core can never block.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>The last lines the core wrote in this session (empty when it has said nothing, which is the good case).</summary>
    public static string ReadLog(int lines = 200)
    {
        try
        {
            if (!File.Exists(LogPath)) return "";
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return string.Join("\n", reader.ReadToEnd().Split('\n').TakeLast(lines)).Trim();
        }
        catch (IOException) { return ""; }
    }

    public void Stop()
    {
        try
        {
            if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); process.WaitForExit(2000); }
        }
        catch (Exception ex) { Log.Write("Could not stop the proxy core", ex); }
        process = null;
    }

    /// <summary>Bytes carried by the proxy since the core started (download, upload), read from Xray's stats service.</summary>
    public static async Task<(long Down, long Up)?> QueryTrafficAsync()
    {
        try
        {
            var (code, output) = await RunAsync(ExePath, $"api statsquery --server=127.0.0.1:{ProxyLink.ApiPort} -pattern \"outbound>>>proxy>>>traffic\"");
            if (code != 0) return null;
            long down = 0, up = 0;
            foreach (var stat in JsonNode.Parse(output)?["stat"]?.AsArray() ?? new JsonArray())
            {
                string name = stat?["name"]?.ToString() ?? "";
                long.TryParse(stat?["value"]?.ToString(), out long value);
                if (name.EndsWith("downlink")) down = value;
                else if (name.EndsWith("uplink")) up = value;
            }
            return (down, up);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>A core left over from a crashed session would hold the ports; ours (and only ours) is stopped.</summary>
    public static void KillStrays()
    {
        foreach (var p in Process.GetProcessesByName("xray"))
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (string.Equals(path, BundledExe, StringComparison.OrdinalIgnoreCase) || string.Equals(path, UpdatedExe, StringComparison.OrdinalIgnoreCase))
                { p.Kill(); p.WaitForExit(2000); }
            }
            catch { /* not ours, or already gone */ }
            finally { p.Dispose(); }
        }
    }

    private static async Task<(int Code, string Output)> RunAsync(string file, string arguments)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + file);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout + await stderr);
    }
}
