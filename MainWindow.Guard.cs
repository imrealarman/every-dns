using System.Net;
using EveryDns.Services;
using Microsoft.UI.Xaml;

namespace EveryDns;

// The DNS kill switch ("DNS guard"): DNS can only go to the servers that are in use.
public sealed partial class MainWindow
{
    private bool loadingGuard;

    /// <summary>Shows what the firewall really says. A reset to automatic switches the guard off, and this is where that becomes visible.</summary>
    private async Task RefreshGuardAsync(bool announceCleared)
    {
        bool was = DnsGuardToggle.IsOn;
        bool active = await Task.Run(() => Firewall.IsActive("dns"));
        loadingGuard = true;
        DnsGuardToggle.IsOn = active;
        loadingGuard = false;
        settings.DnsGuard = active;
        settings.Save();
        GuardText.Text = active ? "On: DNS to any other server is blocked" : "Blocks DNS to any server except the ones in use";
        if (announceCleared && was && !active) Notify("The DNS kill switch was turned off because the DNS servers changed. Turn it on again to protect the new ones.");
    }

    private async void DnsGuard_Toggled(object sender, RoutedEventArgs e)
    {
        if (loadingGuard) return;
        if (DnsGuardToggle.IsOn)
        {
            var servers = adapters.SelectMany(a => a.Dns).Distinct().ToList();
            if (servers.Count == 0)
            {
                Notify("There are no DNS servers in use to protect.", bad: true);
                await RefreshGuardAsync(announceCleared: false);
                return;
            }
            var result = await DnsChanger.FirewallApplyAsync("dns", servers);
            Notify(result.Ok ? $"DNS kill switch on: only {string.Join(", ", servers.Take(3))}{(servers.Count > 3 ? "…" : "")} can answer DNS." : result.Message, bad: !result.Ok);
        }
        else
        {
            var result = await DnsChanger.FirewallClearAsync("dns");
            Notify(result.Ok ? "DNS kill switch off." : result.Message, bad: !result.Ok);
        }
        await RefreshGuardAsync(announceCleared: false);
    }
}
