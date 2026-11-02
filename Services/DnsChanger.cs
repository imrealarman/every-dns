using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;

namespace EveryDns.Services;

public sealed record ChangeResult(bool Ok, string Message);

/// <summary>
/// Changing DNS needs administrator rights, but nothing else in the app does. So the window runs as the user and,
/// for a change, starts a second copy of itself elevated with <c>--apply</c> / <c>--reset</c>; that copy makes the
/// change and exits. Clearing the cache needs no rights and runs directly.
/// </summary>
public static class DnsChanger
{
    private const int ExitOk = 0, ExitBadArgs = 2, ExitNoAdapter = 3, ExitNetshFailed = 4;
    private const int ErrorCancelled = 1223; // the user said no to the UAC prompt

    public static bool IsCliRequest(string[] args) => args.Length > 0 && args[0] is "--apply" or "--reset";

    // ---- called by the window ----------------------------------------------------------------------------------

    public static Task<ChangeResult> ApplyAsync(string adapterId, IEnumerable<IPAddress> servers) =>
        RunAsync(new[] { "--apply", adapterId, string.Join(",", servers.Select(s => s.ToString())) });

    public static Task<ChangeResult> ResetAsync(string adapterId) => RunAsync(new[] { "--reset", adapterId });

    public static async Task<ChangeResult> FlushAsync()
    {
        var (code, _) = await Task.Run(() => Exec("ipconfig.exe", "/flushdns"));
        return code == 0
            ? new ChangeResult(true, "DNS cache cleared. Windows will look every name up again.")
            : new ChangeResult(false, "Windows could not clear the DNS cache.");
    }

    private static async Task<ChangeResult> RunAsync(string[] args)
    {
        if (IsAdministrator()) return Describe(Run(args));

        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = string.Join(" ", args.Select(a => '"' + a + '"')),
        };
        try
        {
            using var process = Process.Start(start);
            if (process == null) return new ChangeResult(false, "Could not start the change.");
            await process.WaitForExitAsync();
            return Describe(process.ExitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ChangeResult(false, "Cancelled. Windows needs your permission to change DNS.");
        }
        catch (Exception ex)
        {
            Log.Write("Could not start the elevated change", ex);
            return new ChangeResult(false, "Could not ask Windows for permission: " + ex.Message);
        }
    }

    private static ChangeResult Describe(int exitCode) => exitCode switch
    {
        ExitOk => new ChangeResult(true, "DNS updated."),
        ExitNoAdapter => new ChangeResult(false, "That adapter is no longer connected."),
        ExitBadArgs => new ChangeResult(false, "Those DNS addresses are not valid."),
        _ => new ChangeResult(false, $"Windows refused the change (see {Log.FilePath})."),
    };

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // ---- the elevated side -------------------------------------------------------------------------------------

    /// <summary>Runs one <c>--apply</c> / <c>--reset</c> request and returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length < 2) return ExitBadArgs;
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Id, args[1], StringComparison.OrdinalIgnoreCase));
            if (adapter == null) return ExitNoAdapter;
            if (adapter.Name.Contains('"')) return ExitBadArgs;

            if (args[0] == "--reset")
            {
                int auto4 = Netsh(adapter.Name, "ip", Array.Empty<IPAddress>());
                Netsh(adapter.Name, "ipv6", Array.Empty<IPAddress>()); // an adapter without IPv6 has nothing to reset
                return auto4 == 0 ? Flush() : ExitNetshFailed;
            }

            if (args.Length < 3) return ExitBadArgs;
            var servers = new List<IPAddress>();
            foreach (string text in args[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!DnsPresets.TryParseServer(text, out var ip)) return ExitBadArgs;
                servers.Add(ip);
            }
            if (servers.Count == 0 || servers.Count > 8) return ExitBadArgs;

            var v4 = servers.Where(s => s.AddressFamily == AddressFamily.InterNetwork).ToList();
            var v6 = servers.Where(s => s.AddressFamily == AddressFamily.InterNetworkV6).ToList();
            int done4 = Netsh(adapter.Name, "ip", v4);
            int done6 = Netsh(adapter.Name, "ipv6", v6);
            // Only an IPv6 server that was asked for must succeed; "back to automatic" is best effort.
            bool ok = done4 == 0 && (done6 == 0 || v6.Count == 0);
            return ok ? Flush() : ExitNetshFailed;
        }
        catch (Exception ex)
        {
            Log.Write("DNS change failed", ex);
            return ExitNetshFailed;
        }
    }

    /// <summary>Sets one address family's servers (the first becomes primary), or hands it back to the router when the list is empty.</summary>
    private static int Netsh(string adapterName, string family, IReadOnlyList<IPAddress> servers)
    {
        string set = servers.Count == 0
            ? $"interface {family} set dnsservers name=\"{adapterName}\" source=dhcp"
            : $"interface {family} set dnsservers name=\"{adapterName}\" source=static address={servers[0]} validate=no";
        var (code, output) = Exec("netsh.exe", set);
        if (code != 0) { Log.Write($"netsh failed ({code}): {set}\n{output}"); return code; }

        for (int i = 1; i < servers.Count; i++)
        {
            string add = $"interface {family} add dnsservers name=\"{adapterName}\" address={servers[i]} index={i + 1} validate=no";
            (code, output) = Exec("netsh.exe", add);
            if (code != 0) { Log.Write($"netsh failed ({code}): {add}\n{output}"); return code; }
        }
        return 0;
    }

    private static int Flush() => Exec("ipconfig.exe", "/flushdns").Code == 0 ? ExitOk : ExitNetshFailed;

    private static (int Code, string Output) Exec(string file, string arguments)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        string text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, text.Trim());
    }
}
