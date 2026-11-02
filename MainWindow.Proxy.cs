using System.Collections.ObjectModel;
using EveryDns.Models;
using EveryDns.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace EveryDns;

// The Proxy tab: servers, connect / disconnect, ping, speed, data used, and the Xray core.
public sealed partial class MainWindow
{
    private readonly ProxyStore proxy = ProxyStore.Load();
    private readonly XrayCore core = new();
    private readonly ObservableCollection<ProxyRow> rows = new();
    private readonly DispatcherTimer proxyTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private bool loadingModes, connected, proxyBusy, syncingSelection, ticking, resolvingFlags, speedRunning;
    private ProxyServer? connectedServer;
    private long lastCoreDown, lastCoreUp, sessionDown, sessionUp;
    private DateTime lastTickAt;
    private int ticks;
    private string? coreVersion;

    private void InitProxy()
    {
        // A crash while connected would leave Windows pointed at a proxy that no longer runs.
        if (proxy.Backup != null)
        {
            try { SystemProxy.Restore(proxy.Backup); } catch (Exception ex) { Log.Write("Could not restore the Windows proxy", ex); }
            proxy.Backup = null;
            proxy.Save();
        }
        XrayCore.KillStrays();

        VersionText.Text = "v" + typeof(App).Assembly.GetName().Version!.ToString(3);
        loadingModes = true;
        ModeBox.SelectedIndex = proxy.Mode == "local" ? 1 : 0;
        RoutingBox.SelectedIndex = proxy.Routing == "iran" ? 1 : 0;
        loadingModes = false;

        ServerList.ItemsSource = rows;
        RebuildRows();
        RenderProxy();
        RenderTotals();

        proxyTimer.Tick += async (_, _) => await ProxyTickAsync();
        proxyTimer.Start();
        _ = RefreshCoreAsync(checkForUpdate: true);
        _ = ResolveFlagsAsync();
    }

    private void ShutdownProxy()
    {
        if (connected) Disconnect();
        proxy.Save();
    }

    // ---- list --------------------------------------------------------------------------------------------------

    private ProxyRow? Selected => ServerList.SelectedItem as ProxyRow;

    private void RebuildRows()
    {
        syncingSelection = true;
        rows.Clear();
        foreach (var server in proxy.Servers) rows.Add(new ProxyRow(server));
        var selected = rows.FirstOrDefault(r => r.Server.Id == proxy.SelectedId) ?? rows.FirstOrDefault();
        ServerList.SelectedItem = selected;
        proxy.SelectedId = selected?.Server.Id;
        syncingSelection = false;

        NoServers.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ServersHeader.Text = rows.Count == 0 ? "SERVERS" : $"SERVERS  ·  {rows.Count}";
        foreach (var row in rows) _ = ApplyFlagAsync(row);
        RenderProxy();
    }

    private async void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingSelection) return;
        proxy.SelectedId = Selected?.Server.Id;
        proxy.Save();
        RenderProxy();
        // Picking another server while connected moves the connection over.
        if (connected && Selected != null && Selected.Server != connectedServer) await ConnectAsync();
    }

    private void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        var row = (ProxyRow)((Button)sender).Tag;
        proxy.Servers.Remove(row.Server);
        if (proxy.SelectedId == row.Server.Id) proxy.SelectedId = null;
        proxy.Save();
        RebuildRows();
    }

    // ---- flags -------------------------------------------------------------------------------------------------

    private async Task ApplyFlagAsync(ProxyRow row)
    {
        if (row.Server.Country.Length == 0) return;
        row.SetFlag(await Flags.FlagFileAsync(row.Server.Country));
        if (row.Server == (connectedServer ?? Selected?.Server)) RenderProxy();
    }

    /// <summary>Looks up the country of every server that has none yet. One at a time, so the lookup service is not hammered.</summary>
    private async Task ResolveFlagsAsync()
    {
        if (resolvingFlags) return;
        resolvingFlags = true;
        try
        {
            foreach (var row in rows.ToList())
            {
                if (row.Server.Country.Length > 0) continue;
                row.Server.Country = await Flags.CountryOfAsync(row.Server.Address);
                await ApplyFlagAsync(row);
            }
            proxy.Save();
        }
        finally { resolvingFlags = false; }
    }

    // ---- connect -----------------------------------------------------------------------------------------------

    private async void Power_Click(object sender, RoutedEventArgs e)
    {
        if (proxyBusy) return;
        if (connected) Disconnect(); else await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        var row = Selected;
        if (row == null) { Notify("Add a server and pick it first.", bad: true); return; }
        if (proxyBusy) return;
        proxyBusy = true;
        PowerButton.IsEnabled = false;
        ProxyStatus.Text = "Connecting…";
        try
        {
            if (!XrayCore.IsInstalled)
            {
                // The core ships with the app, so this only happens if someone removed it.
                string latest = await XrayCore.GetLatestVersionAsync();
                await XrayCore.InstallAsync(latest, new Progress<string>(m => Notify(m)));
                await RefreshCoreAsync(checkForUpdate: false);
            }

            await core.StartAsync(ProxyLink.BuildConfig(row.Server.Raw, bypassIran: proxy.Routing == "iran", logFile: XrayCore.LogPath));
            lastCoreDown = lastCoreUp = 0; // a new core counts from zero
            lastTickAt = DateTime.UtcNow;

            if (!connected) sessionDown = sessionUp = 0;
            // "System proxy" points Windows at the local port; "Local port only" leaves Windows alone (switching modes while connected undoes it).
            if (proxy.Mode == "system" && proxy.Backup == null) proxy.Backup = SystemProxy.Enable();
            else if (proxy.Mode != "system" && proxy.Backup != null) { SystemProxy.Restore(proxy.Backup); proxy.Backup = null; }
            proxy.Save();
            connected = true;
            connectedServer = row.Server;
            DelayText.Text = "…";
            ticks = 0;
        }
        catch (Exception ex)
        {
            core.Stop();
            Log.Write("Connect failed", ex);
            Notify(ex is HttpRequestException ? "Could not download the proxy core. Check your connection." : ex.Message, bad: true);
        }
        finally
        {
            proxyBusy = false;
            PowerButton.IsEnabled = true;
            RenderProxy();
        }
    }

    private void Disconnect()
    {
        connected = false;
        connectedServer = null;
        core.Stop();
        if (proxy.Backup != null)
        {
            try { SystemProxy.Restore(proxy.Backup); } catch (Exception ex) { Log.Write("Could not restore the Windows proxy", ex); }
            proxy.Backup = null;
        }
        proxy.Save();
        DownText.Text = UpText.Text = DelayText.Text = "–";
        DelayText.Foreground = (Brush)Application.Current.Resources["TextBrush"];
        RenderProxy();
    }

    private void RenderProxy()
    {
        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        PowerButton.Background = connected ? Tones.Good : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        PowerButton.BorderBrush = connected ? Tones.Good : accent;
        PowerIcon.Foreground = connected ? new SolidColorBrush(Microsoft.UI.Colors.White) : accent;

        var shown = rows.FirstOrDefault(r => r.Server == (connectedServer ?? Selected?.Server));
        ProxyStatus.Text = connected ? "Connected" : "Not connected";
        ProxySub.Text = connected && proxy.Mode != "system" ? $"SOCKS 127.0.0.1:{ProxyLink.SocksPort}  ·  HTTP 127.0.0.1:{ProxyLink.HttpPort}"
            : shown != null ? shown.Name : "Pick a server and press the button";
        RestartButton.IsEnabled = connected;
        HeroFlag.Visibility = shown?.Server.Country.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown != null) HeroFlag.Fill = shown.Flag;
        if (tab == "services") ServicesVia.Text = connected ? "Testing through your proxy" : "Testing through your normal connection";
    }

    private void RenderTotals()
    {
        UsedText.Text = $"This session   ↓ {FormatBytes(sessionDown)}    ↑ {FormatBytes(sessionUp)}";
        TotalText.Text = $"All time   ↓ {FormatBytes(proxy.TotalDown)}    ↑ {FormatBytes(proxy.TotalUp)}";
    }

    // ---- live numbers ------------------------------------------------------------------------------------------

    private async Task ProxyTickAsync()
    {
        if (!connected || ticking || proxyBusy) return;
        ticking = true;
        try
        {
            if (!core.IsRunning)
            {
                Disconnect();
                Notify("The proxy core stopped, so EveryDns disconnected.", bad: true);
                return;
            }

            var traffic = await XrayCore.QueryTrafficAsync();
            if (traffic is { } t && connected)
            {
                var now = DateTime.UtcNow;
                double seconds = Math.Max(0.5, (now - lastTickAt).TotalSeconds);
                long down = Math.Max(0, t.Down - lastCoreDown), up = Math.Max(0, t.Up - lastCoreUp);
                (lastCoreDown, lastCoreUp, lastTickAt) = (t.Down, t.Up, now);

                sessionDown += down; sessionUp += up;
                proxy.TotalDown += down; proxy.TotalUp += up;
                DownText.Text = FormatBytes(down / seconds) + "/s";
                UpText.Text = FormatBytes(up / seconds) + "/s";
                RenderTotals();
                if (ticks % 5 == 0) proxy.Save();
            }

            if (ticks++ % 3 == 0 && connected)
            {
                long? delay = await ProxyTests.DelayThroughProxyAsync();
                if (connected)
                {
                    DelayText.Text = delay == null ? "no reply" : delay + " ms";
                    DelayText.Foreground = Tones.ForLatency(delay);
                }
            }
        }
        finally { ticking = false; }
    }

    // ---- ping and speed ----------------------------------------------------------------------------------------

    private async void PingAll_Click(object sender, RoutedEventArgs e)
    {
        if (rows.Count == 0) return;
        foreach (var row in rows) { row.Ping = "…"; row.PingBrush = Tones.Warn; }

        var results = new Dictionary<string, long?>();
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(rows.ToList().Select(async row =>
        {
            await gate.WaitAsync();
            try
            {
                long? ms = await ProxyTests.TcpPingAsync(row.Server.Address, row.Server.Port);
                results[row.Server.Id] = ms;
                row.Ping = ms == null ? "timeout" : ms + " ms";
                row.PingBrush = Tones.ForLatency(ms);
            }
            finally { gate.Release(); }
        }));

        // Fastest first.
        var order = rows.OrderBy(r => results.GetValueOrDefault(r.Server.Id) ?? long.MaxValue).ToList();
        string? keep = Selected?.Server.Id;
        syncingSelection = true;
        rows.Clear();
        foreach (var r in order) rows.Add(r);
        ServerList.SelectedItem = rows.FirstOrDefault(r => r.Server.Id == keep);
        syncingSelection = false;
        proxy.Servers = order.Select(r => r.Server).ToList();
        proxy.Save();
        _ = ResolveFlagsAsync();
    }

    private async void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (!connected) { Notify("Connect to a server first, then run the speed test.", bad: true); return; }
        if (speedRunning) return;
        speedRunning = true;
        Notify("Testing download speed through the proxy…");
        double? mbps = await ProxyTests.DownloadSpeedAsync(6, CancellationToken.None);
        speedRunning = false;
        if (mbps == null) Notify("The speed test failed. The server may be slow or blocked.", bad: true);
        else Notify($"Download speed: {mbps:0.0} Mbps ({mbps / 8:0.0} MB/s)");
    }

    // ---- adding servers and subscriptions ----------------------------------------------------------------------

    private async void AddServers_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 150,
            MinWidth = 320,
            PlaceholderText = "vless://…  vmess://…  trojan://…  ss://…\nor a subscription address (https://…)",
        };
        var dialog = new ContentDialog { Title = "Add servers", Content = box, PrimaryButtonText = "Add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        await AddFromTextAsync(box.Text.Trim());
    }

    private async Task AddFromTextAsync(string text)
    {
        if (text.Length == 0) return;
        try
        {
            List<ProxyServer> servers;
            List<string> errors;
            int added;
            if (!text.Contains('\n') && text.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                Notify("Fetching the subscription…");
                (servers, errors) = await ProxyTests.FetchSubscriptionAsync(text);
                var sub = proxy.Subscriptions.FirstOrDefault(s => s.Url == text);
                if (sub == null) proxy.Subscriptions.Add(sub = new Subscription { Url = text });
                sub.Updated = DateTime.Now;
                int before = proxy.Servers.Count;
                proxy.Replace(sub.Id, servers);
                added = proxy.Servers.Count - before;
            }
            else
            {
                (servers, errors) = ProxyLink.ParseMany(text);
                added = proxy.Add(servers);
            }

            proxy.Save();
            RebuildRows();
            _ = ResolveFlagsAsync();
            if (servers.Count == 0) Notify(errors.Count > 0 ? errors[0] : "No servers found in that text.", bad: true);
            else Notify($"Added {added} server{(added == 1 ? "" : "s")}" + (errors.Count > 0 ? $", skipped {errors.Count}." : "."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException)
        {
            Notify(ex is TaskCanceledException ? "The subscription took too long to answer." : ex.Message, bad: true);
        }
    }

    private async void UpdateSubs_Click(object sender, RoutedEventArgs e)
    {
        if (proxy.Subscriptions.Count == 0) { Notify("No subscriptions yet. Press Add and paste a subscription address."); return; }
        Notify("Updating subscriptions…");
        int total = 0, failed = 0;
        foreach (var sub in proxy.Subscriptions.ToList())
        {
            try
            {
                var (servers, _) = await ProxyTests.FetchSubscriptionAsync(sub.Url);
                if (servers.Count == 0) { failed++; continue; } // an empty answer must not wipe a working list
                proxy.Replace(sub.Id, servers);
                sub.Updated = DateTime.Now;
                total += servers.Count;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException) { failed++; }
        }
        proxy.Save();
        RebuildRows();
        _ = ResolveFlagsAsync();
        Notify(failed == 0 ? $"Subscriptions updated: {total} servers." : $"{total} servers updated, {failed} subscription(s) failed.", bad: failed > 0);
    }

    // ---- the Xray core -----------------------------------------------------------------------------------------

    private async Task RefreshCoreAsync(bool checkForUpdate)
    {
        coreVersion = await XrayCore.GetVersionAsync();
        CoreButton.Content = coreVersion == null ? "Core missing" : $"Core v{coreVersion}";
        if (!checkForUpdate || coreVersion == null) return;
        try
        {
            string latest = await XrayCore.GetLatestVersionAsync();
            if (XrayCore.IsNewer(latest, coreVersion)) CoreButton.Content = $"Core v{coreVersion}  ·  update";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { /* offline: nothing to say */ }
    }

    private async void Core_Click(object sender, RoutedEventArgs e)
    {
        if (proxyBusy) return;
        try
        {
            Notify("Checking GitHub for a newer core…");
            string latest = await XrayCore.GetLatestVersionAsync();
            if (!XrayCore.IsNewer(latest, coreVersion)) { Notify($"Core v{coreVersion} is the latest."); return; }

            var ask = new ContentDialog
            {
                Title = "Update the core?",
                Content = $"Xray-core {latest} is available. You have {coreVersion ?? "none"}.",
                PrimaryButtonText = "Update",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await ShowDialogAsync(ask) != ContentDialogResult.Primary) return;

            proxyBusy = true;
            bool wasConnected = connected;
            var server = connectedServer;
            if (wasConnected) Disconnect(); // the running core's file cannot be replaced
            await XrayCore.InstallAsync(latest, new Progress<string>(m => Notify(m)));
            proxyBusy = false;
            await RefreshCoreAsync(checkForUpdate: false);
            Notify($"Core updated to v{coreVersion}.");
            if (wasConnected && server != null) await ConnectAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or System.Text.Json.JsonException)
        {
            Log.Write("Core update failed", ex);
            Notify("Core update failed: " + ex.Message, bad: true);
        }
        finally { proxyBusy = false; }
    }
}
