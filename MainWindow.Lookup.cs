using System.Net;
using EveryDns.Models;
using EveryDns.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using Windows.UI.Core;

namespace EveryDns;

// The Lookup tab: ask Windows, or one DNS server directly, what a name points to.
public sealed partial class MainWindow
{
    private static readonly RecordType[] LookupTypes = { RecordType.A, RecordType.AAAA, RecordType.CNAME, RecordType.MX, RecordType.NS, RecordType.TXT };
    private readonly List<AnswerRow> answers = new();

    private void InitLookup()
    {
        InitServices();
        TypeBox.ItemsSource = LookupTypes.Select(t => t.ToString()).ToList();
        TypeBox.SelectedIndex = 0;
        BuildLookupChoices();
    }

    private void BuildLookupChoices()
    {
        var choices = new List<ServerChoice> { new() { Label = "Windows (as the PC resolves it)" } };
        foreach (var ip in adapter?.Dns ?? new List<IPAddress>()) choices.Add(new ServerChoice { Label = "Your DNS  " + ip, Address = ip });
        foreach (var row in presetRows) choices.Add(new ServerChoice { Label = row.Name, Address = row.Preset.Primary });

        ViaBox.ItemsSource = choices;
        ViaBox.SelectedIndex = 0;
    }

    private void DomainBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) Lookup_Click(sender, e);
    }

    private async void Lookup_Click(object sender, RoutedEventArgs e)
    {
        string domain = DomainBox.Text.Trim();
        if (domain.Length == 0 || ViaBox.SelectedItem is not ServerChoice via) { Notify("Type a domain name first.", bad: true); return; }
        var type = LookupTypes[Math.Max(0, TypeBox.SelectedIndex)];

        LookupSummary.Text = "Looking up…";
        AnswerList.ItemsSource = null;
        try
        {
            if (via.Address == null) await LookupViaWindowsAsync(domain, type);
            else await LookupViaServerAsync(via, domain, type);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Net.Sockets.SocketException)
        {
            LookupSummary.Text = ex is System.Net.Sockets.SocketException ? "That name could not be resolved." : ex.Message;
        }
    }

    private async Task LookupViaServerAsync(ServerChoice via, string domain, RecordType type)
    {
        var reply = await DnsClient.QueryAsync(via.Address!, domain, type, 3000);
        LookupSummary.Text = $"{reply.Status}  ·  {reply.Time.TotalMilliseconds:0} ms  ·  {reply.Answers.Count} answer{(reply.Answers.Count == 1 ? "" : "s")}  ·  {via.Address}";
        AnswerList.ItemsSource = reply.Answers.Select(a => new AnswerRow { Type = a.Type, Data = a.Data, Detail = $"{a.Name}  ·  TTL {a.Ttl}s" }).ToList();
    }

    /// <summary>The Windows resolver only returns addresses, so it can answer A and AAAA.</summary>
    private async Task LookupViaWindowsAsync(string domain, RecordType type)
    {
        if (type is not (RecordType.A or RecordType.AAAA)) { LookupSummary.Text = "Windows can only look up A and AAAA records here. Pick a DNS server for other types."; return; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var family = type == RecordType.A ? System.Net.Sockets.AddressFamily.InterNetwork : System.Net.Sockets.AddressFamily.InterNetworkV6;
        var found = (await Dns.GetHostAddressesAsync(domain)).Where(a => a.AddressFamily == family).ToList();
        LookupSummary.Text = $"{(found.Count > 0 ? "No error" : "No records of this type")}  ·  {clock.ElapsedMilliseconds} ms  ·  {found.Count} answer{(found.Count == 1 ? "" : "s")}  ·  Windows";
        AnswerList.ItemsSource = found.Select(a => new AnswerRow { Type = type.ToString(), Data = a.ToString(), Detail = domain }).ToList();
    }
}
