using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using EveryDns.Models;
using EveryDns.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace EveryDns;

// The DNS tab: live connection status, the DNS in use, clearing the cache, and switching DNS.
public sealed partial class MainWindow
{
    private const string ProbeName = "www.msftconnecttest.com";
    private static readonly IPAddress[] InternetTargets = { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("9.9.9.9") };

    private readonly ObservableCollection<ServerRow> current = new();
    private readonly ObservableCollection<PresetRow> presetRows = new();
    private readonly List<double?> netSamples = new(), lookupSamples = new();
    private readonly DispatcherTimer networkChanged = new() { Interval = TimeSpan.FromSeconds(1) };

    private List<AdapterInfo> adapters = new();
    private AdapterInfo? adapter;
    private CancellationTokenSource? monitor;
    private bool loadingAdapters, dnsBusy;

    private void InitDns()
    {
        CurrentList.ItemsSource = current;
        PresetList.ItemsSource = presetRows;
        BuildPresetRows();
        LoadAdapters();

        // Windows raises this several times for one change; wait for it to settle.
        networkChanged.Tick += (_, _) => { networkChanged.Stop(); LoadAdapters(); };
        NetworkChange.NetworkAddressChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { networkChanged.Stop(); networkChanged.Start(); });
    }

    // ---- adapters and the servers in use -----------------------------------------------------------------------

    private void LoadAdapters()
    {
        adapters = Adapters.Read();
        loadingAdapters = true;
        AdapterBox.ItemsSource = adapters.Select(a => new AdapterRef(a)).ToList();
        int index = Math.Max(0, adapters.FindIndex(a => a.Id == (adapter?.Id ?? settings.AdapterId)));
        AdapterBox.SelectedIndex = adapters.Count == 0 ? -1 : index;
        loadingAdapters = false;
        SelectAdapter(adapters.Count == 0 ? null : adapters[index]);
    }

    private void AdapterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingAdapters || AdapterBox.SelectedItem is not AdapterRef picked) return;
        SelectAdapter(picked.Info);
    }

    private void SelectAdapter(AdapterInfo? info)
    {
        adapter = info;
        current.Clear();
        netSamples.Clear(); lookupSamples.Clear();
        DrawGraph();

        if (info == null)
        {
            AdapterDetails.Text = "No connected network adapter.";
            ModeBadge.Text = "";
            UndoButton.Visibility = Visibility.Collapsed;
            SetVerdict(Tones.Bad, "No network", "Connect to Wi-Fi or plug in a cable.");
            RefreshPresetMarks();
            BuildLookupChoices();
            return;
        }

        settings.AdapterId = info.Id;
        settings.Save();
        AdapterDetails.Text = info.Details;
        ModeBadge.Text = info.Automatic ? "Automatic (from your router)" : "Set manually";
        foreach (var ip in info.Dns)
            current.Add(new ServerRow { Address = ip.ToString(), Label = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4" });
        UndoButton.Visibility = settings.Undo.ContainsKey(info.Id) ? Visibility.Visible : Visibility.Collapsed;

        RefreshPresetMarks();
        BuildLookupChoices();
        _ = PingCurrentAsync();
    }

    private async Task PingCurrentAsync()
    {
        var snapshot = adapter;
        foreach (var row in current.ToList())
        {
            var ip = IPAddress.Parse(row.Address);
            var ping = Probe.PingAsync(ip);
            var reply = await DnsClient.QueryAsync(ip, ProbeName, RecordType.A, 2000);
            long? ms = await ping;
            if (adapter != snapshot) return; // the user moved to another adapter meanwhile
            if (!reply.Ok) { row.Result = "no answer"; row.ResultBrush = Tones.Bad; continue; }
            row.Result = (ms == null ? "" : $"ping {ms} ms  ·  ") + $"lookup {reply.Time.TotalMilliseconds:0} ms";
            row.ResultBrush = Tones.ForLatency(reply.Time.TotalMilliseconds);
        }
    }

    // ---- live status -------------------------------------------------------------------------------------------

    private void StartMonitor()
    {
        if (monitor != null) return;
        monitor = new CancellationTokenSource();
        _ = MonitorAsync(monitor.Token);
    }

    private void StopMonitor()
    {
        monitor?.Cancel();
        monitor = null;
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var a = adapter;
                var dns = a?.Dns.FirstOrDefault();
                var router = a?.Gateway == null ? Task.FromResult<long?>(null) : Probe.PingAsync(a.Gateway);
                var internet = PingInternetAsync();
                var dnsPing = dns == null ? Task.FromResult<long?>(null) : Probe.PingAsync(dns);
                var lookup = dns == null ? Task.FromResult<DnsReply?>(null) : LookupAsync(dns, ct);
                await Task.WhenAll(router, internet, dnsPing, lookup);
                if (ct.IsCancellationRequested || a != adapter) break;

                ShowStatus(a, router.Result, internet.Result, dnsPing.Result, lookup.Result);
                await Task.Delay(1500, ct);
            }
        }
        catch (OperationCanceledException) { /* left the tab */ }
    }

    private static async Task<DnsReply?> LookupAsync(IPAddress server, CancellationToken ct) => await DnsClient.QueryAsync(server, ProbeName, RecordType.A, 2000, ct);

    private static async Task<long?> PingInternetAsync()
    {
        foreach (var target in InternetTargets)
            if (await Probe.PingAsync(target, 1200) is { } ms) return ms;
        return null;
    }

    private void ShowStatus(AdapterInfo? a, long? router, long? internet, long? dnsPing, DnsReply? lookup)
    {
        Fill(GwValue, router, a?.Gateway == null ? "none" : "no reply");
        Fill(NetValue, internet, "offline");
        Fill(DnsValue, dnsPing, a?.Dns.Count > 0 ? "no reply" : "none");
        double? lookupMs = lookup is { Ok: true } ? lookup.Time.TotalMilliseconds : null;
        Fill(LkValue, lookupMs == null ? null : (long)lookupMs, lookup == null ? "none" : "failed");

        netSamples.Add(internet); lookupSamples.Add(lookupMs);
        if (netSamples.Count > 40) { netSamples.RemoveAt(0); lookupSamples.RemoveAt(0); }
        DrawGraph();

        var sent = netSamples.Count;
        var stats = PingStats.From(netSamples.Select(s => s.HasValue ? (long?)s.Value : null).ToList());
        StatsText.Text = sent == 0 ? "" : $"Internet  avg {stats.Avg:0} ms  ·  jitter {stats.Jitter:0} ms  ·  loss {stats.LossPercent:0}%";

        bool internetUp = internet != null, dnsWorks = lookup is { Ok: true };
        if (a == null) return;
        if (internetUp && (dnsWorks || a.Dns.Count == 0)) SetVerdict(Tones.Good, "Connected", dnsWorks ? "Internet and DNS are working." : "Internet is working.");
        else if (internetUp) SetVerdict(Tones.Warn, "DNS is not answering", "The internet works but your DNS server does not. Try another one below.");
        else if (router != null) SetVerdict(Tones.Bad, "No internet", "Your router answers but the internet does not.");
        else SetVerdict(Tones.Bad, "Not connected", a.Gateway == null ? "This adapter has no router." : "Your router does not answer.");
    }

    private static void Fill(TextBlock target, long? ms, string failedText)
    {
        target.Text = ms == null ? failedText : ms + " ms";
        target.Foreground = ms == null ? Tones.Bad : Tones.ForLatency(ms);
    }

    private void SetVerdict(Brush tone, string title, string sub)
    {
        VerdictDot.Fill = tone;
        VerdictTitle.Text = title;
        VerdictSub.Text = sub;
    }

    private void Graph_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGraph();

    private void DrawGraph()
    {
        double w = Graph.ActualWidth, h = Graph.Height;
        NetLine.Points.Clear(); LkLine.Points.Clear();
        if (w <= 0 || netSamples.Count < 2) return;

        double top = Math.Max(100, netSamples.Concat(lookupSamples).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max()) * 1.1;
        double step = w / 39;
        void Plot(Polyline line, List<double?> values)
        {
            for (int i = 0; i < values.Count; i++)
                if (values[i] is { } v) line.Points.Add(new Point(i * step, h - Math.Min(v, top) / top * (h - 4) - 2));
        }
        Plot(NetLine, netSamples);
        Plot(LkLine, lookupSamples);
    }

    // ---- clear cache and change DNS ----------------------------------------------------------------------------

    private async void Flush_Click(object sender, RoutedEventArgs e)
    {
        var result = await DnsChanger.FlushAsync();
        Notify(result.Message, bad: !result.Ok);
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (adapter == null || dnsBusy) return;
        await ChangeAsync(() => DnsChanger.ResetAsync(adapter.Id), "DNS is automatic again.");
    }

    private async void UsePreset_Click(object sender, RoutedEventArgs e)
    {
        var row = (PresetRow)((Button)sender).Tag;
        await ApplyServersAsync(row.Preset.All.Select(IPAddress.Parse).ToList(), row.Name);
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (adapter == null || !settings.Undo.TryGetValue(adapter.Id, out var before)) return;
        string id = adapter.Id;
        if (before.Automatic) await ChangeAsync(() => DnsChanger.ResetAsync(id), "Back to the DNS you had before.", remember: false);
        else await ChangeAsync(() => DnsChanger.ApplyAsync(id, before.Servers.Select(IPAddress.Parse)), "Back to the DNS you had before.", remember: false);
        settings.Undo.Remove(id);
        settings.Save();
        UndoButton.Visibility = Visibility.Collapsed;
    }

    private Task ApplyServersAsync(IReadOnlyList<IPAddress> servers, string label) =>
        adapter == null || dnsBusy ? Task.CompletedTask : ChangeAsync(() => DnsChanger.ApplyAsync(adapter.Id, servers), $"{label} is now your DNS.");

    /// <summary>Runs a DNS change, remembers what was there before so it can be undone, and reloads the screen.</summary>
    private async Task ChangeAsync(Func<Task<ChangeResult>> change, string success, bool remember = true)
    {
        var a = adapter;
        if (a == null) return;
        dnsBusy = true;
        Notify("Changing DNS… Windows may ask for permission.");
        try
        {
            var result = await change();
            if (result.Ok && remember)
            {
                settings.Undo[a.Id] = new DnsSnapshot { Automatic = a.Automatic, Servers = a.Dns.Select(d => d.ToString()).ToList() };
                settings.Save();
            }
            Notify(result.Ok ? success : result.Message, bad: !result.Ok);
            if (result.Ok) { await Task.Delay(400); LoadAdapters(); }
        }
        finally { dnsBusy = false; }
    }

    // ---- the list of providers ---------------------------------------------------------------------------------

    private void BuildPresetRows()
    {
        presetRows.Clear();
        foreach (var preset in DnsPresets.Builtin) presetRows.Add(new PresetRow(preset));
        foreach (var saved in settings.Custom)
        {
            var v4 = saved.Servers.Where(s => IPAddress.Parse(s).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToArray();
            var v6 = saved.Servers.Except(v4).ToArray();
            presetRows.Add(new PresetRow(new DnsPreset(saved.Name, "Saved", v4, v6, "Saved by you", Custom: true)));
        }
        RefreshPresetMarks();
    }

    private void RefreshPresetMarks()
    {
        var inUse = adapter?.Dns ?? new List<IPAddress>();
        foreach (var row in presetRows) row.InUse = inUse.Count > 0 && row.Preset.Matches(inUse);
    }

    private async void TestAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in presetRows) { row.Result = "…"; row.ResultBrush = Tones.Warn; row.Fastest = false; }
        string[] names = { "www.google.com", "www.microsoft.com", "www.wikipedia.org" };
        var times = new Dictionary<PresetRow, double?>();

        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(presetRows.ToList().Select(async row =>
        {
            await gate.WaitAsync();
            try
            {
                var ok = new List<double>();
                foreach (string name in names)
                {
                    var reply = await DnsClient.QueryAsync(row.Preset.Primary, name, RecordType.A, 2000);
                    if (reply.Ok) ok.Add(reply.Time.TotalMilliseconds);
                }
                double? median = ok.Count == 0 ? null : ok.OrderBy(v => v).ElementAt(ok.Count / 2);
                times[row] = median;
                row.Result = median == null ? "no answer" : $"{median:0} ms";
                row.ResultBrush = Tones.ForLatency(median);
            }
            finally { gate.Release(); }
        }));

        var best = times.Where(t => t.Value != null).OrderBy(t => t.Value).FirstOrDefault().Key;
        if (best != null) best.Fastest = true;
    }

    private async void Custom_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { Header = "Name (to save it)", PlaceholderText = "My DNS" };
        var primary = new TextBox { Header = "Primary", PlaceholderText = "1.1.1.1" };
        var secondary = new TextBox { Header = "Secondary (optional)", PlaceholderText = "1.0.0.1" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(name); panel.Children.Add(primary); panel.Children.Add(secondary);

        var dialog = new ContentDialog
        {
            Title = "Custom DNS",
            Content = panel,
            PrimaryButtonText = "Use",
            SecondaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var choice = await ShowDialogAsync(dialog);
        if (choice == ContentDialogResult.None) return;

        var servers = new List<IPAddress>();
        foreach (string text in new[] { primary.Text, secondary.Text }.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            if (!DnsPresets.TryParseServer(text, out var ip)) { Notify($"\"{text.Trim()}\" is not a valid DNS address.", bad: true); return; }
            servers.Add(ip);
        }
        if (servers.Count == 0) { Notify("Enter at least a primary DNS address.", bad: true); return; }

        if (choice == ContentDialogResult.Primary) { await ApplyServersAsync(servers, "Your DNS"); return; }

        string label = name.Text.Trim().Length > 0 ? name.Text.Trim() : "Custom " + (settings.Custom.Count + 1);
        settings.Custom.Add(new SavedServer { Name = label, Servers = servers.Select(s => s.ToString()).ToList() });
        settings.Save();
        BuildPresetRows();
        BuildLookupChoices();
        Notify($"Saved \"{label}\" at the end of the list.");
    }

    private void RemovePreset_Click(object sender, RoutedEventArgs e)
    {
        var row = (PresetRow)((Button)sender).Tag;
        settings.Custom.RemoveAll(c => c.Name == row.Name);
        settings.Save();
        BuildPresetRows();
        BuildLookupChoices();
    }
}
