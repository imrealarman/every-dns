using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using EveryDns.Models;
using EveryDns.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace EveryDns;

// Proxy extras: modes, restart, import and export, QR codes, logs, about.
public sealed partial class MainWindow
{
    private async void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingModes) return;
        proxy.Mode = (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";
        proxy.Routing = (RoutingBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "global";
        proxy.Save();
        if (connected && !proxyBusy) await ConnectAsync(); // apply the new mode to the running connection
    }

    private async void RestartCore_Click(object sender, RoutedEventArgs e)
    {
        if (!connected || proxyBusy) return;
        Notify("Restarting the core…");
        await ConnectAsync();
        if (connected) Notify("Core restarted.");
    }

    // ---- import and export -------------------------------------------------------------------------------------

    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        try
        {
            // A file of links, or a saved subscription body; never more than a few MB.
            if (new FileInfo(file.Path).Length > 5_000_000) { Notify("That file is too big to be a list of servers.", bad: true); return; }
            await AddFromTextAsync((await File.ReadAllTextAsync(file.Path)).Trim());
        }
        catch (IOException ex) { Notify("Could not read the file: " + ex.Message, bad: true); }
    }

    private async void ImportClipboard_Click(object sender, RoutedEventArgs e)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) { Notify("The clipboard has no text.", bad: true); return; }
        await AddFromTextAsync((await content.GetTextAsync()).Trim());
    }

    private async void ExportFile_Click(object sender, RoutedEventArgs e)
    {
        if (proxy.Servers.Count == 0) { Notify("There are no servers to export.", bad: true); return; }
        var picker = new FileSavePicker { SuggestedFileName = "everydns-servers" };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            await File.WriteAllTextAsync(file.Path, string.Join("\r\n", proxy.Servers.Select(s => s.Raw)) + "\r\n", new UTF8Encoding(false));
            Notify($"Exported {proxy.Servers.Count} servers. The links contain your passwords, so keep the file private.");
        }
        catch (IOException ex) { Notify("Could not write the file: " + ex.Message, bad: true); }
    }

    private void CopyLinks_Click(object sender, RoutedEventArgs e)
    {
        if (proxy.Servers.Count == 0) { Notify("There are no servers to copy.", bad: true); return; }
        CopyText(string.Join("\n", proxy.Servers.Select(s => s.Raw)));
        Notify($"Copied {proxy.Servers.Count} links. They contain your passwords, so paste them only where you trust.");
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private async void ClearServers_Click(object sender, RoutedEventArgs e)
    {
        if (proxy.Servers.Count == 0) return;
        var ask = new ContentDialog
        {
            Title = "Remove all servers?",
            Content = "This removes every server and subscription from the list. Export them first if you want a copy.",
            PrimaryButtonText = "Remove all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(ask) != ContentDialogResult.Primary) return;
        proxy.Servers.Clear();
        proxy.Subscriptions.Clear();
        proxy.SelectedId = null;
        proxy.Save();
        RebuildRows();
    }

    // ---- QR codes ----------------------------------------------------------------------------------------------

    private async void ShowQr_Click(object sender, RoutedEventArgs e)
    {
        var row = (ProxyRow)((Button)sender).Tag;
        try
        {
            var image = new BitmapImage();
            using (var stream = new InMemoryRandomAccessStream())
            {
                await stream.WriteAsync(QrTools.ToPng(row.Server.Raw).AsBuffer());
                stream.Seek(0);
                await image.SetSourceAsync(stream);
            }

            var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            // The code is drawn on white so every scanner can read it, in light and dark themes alike.
            panel.Children.Add(new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.White), Padding = new Thickness(8), CornerRadius = new CornerRadius(8), Child = new Image { Source = image, Width = 280, Height = 280 } });
            panel.Children.Add(new TextBlock { Text = row.Detail, HorizontalAlignment = HorizontalAlignment.Center, Style = (Style)Application.Current.Resources["MutedText"] });

            var dialog = new ContentDialog { Title = row.Name, Content = panel, PrimaryButtonText = "Copy link", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close };
            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) { CopyText(row.Server.Raw); Notify("Link copied."); }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or QRCoder.Exceptions.DataTooLongException)
        {
            Notify("This link is too long for a QR code.", bad: true);
        }
    }

    private async void ImportQr_Click(object sender, RoutedEventArgs e)
    {
        Notify("Looking for QR codes on your screen…");
        AppWindow.Hide(); // so the window itself is not in the picture
        await Task.Delay(450);
        List<string> found;
        try { found = await Task.Run(QrTools.ScanScreen); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { found = new List<string>(); Log.Write("Screen scan failed", ex); }
        AppWindow.Show();
        Activate();

        var links = found.Where(t => t.Contains("://")).ToList();
        if (links.Count == 0) { Notify(found.Count == 0 ? "No QR code found on the screen." : "The QR code on the screen is not a server link.", bad: true); return; }
        await AddFromTextAsync(string.Join("\n", links));
    }

    // ---- logs and version --------------------------------------------------------------------------------------

    private async void Logs_Click(object sender, RoutedEventArgs e)
    {
        var shown = connectedServer ?? Selected?.Server;
        var text = new StringBuilder();
        text.AppendLine($"EveryDns {typeof(App).Assembly.GetName().Version!.ToString(3)}  ·  core {coreVersion ?? "missing"}");
        text.AppendLine($"Mode: {(proxy.Mode == "system" ? "system proxy" : "local port only")}  ·  route: {(proxy.Routing == "iran" ? "bypass Iran" : "everything")}");
        text.AppendLine($"Server: {shown?.Name ?? "none"}  ·  {(connected ? "connected" : "not connected")}");
        text.AppendLine();
        string core = XrayCore.ReadLog();
        text.AppendLine("— Proxy core —");
        text.AppendLine(core.Length > 0 ? core : "Nothing logged. The core only writes warnings and errors.");
        text.AppendLine();
        text.AppendLine("— App —");
        text.AppendLine(ReadTail(Log.FilePath, 60) is { Length: > 0 } app ? app : "Nothing logged.");

        var box = new TextBox
        {
            Text = text.ToString(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            Width = 340,
            Height = 380,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog { Title = "Logs", Content = box, PrimaryButtonText = "Copy", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary) { CopyText(box.Text); Notify("Logs copied."); }
    }

    private static string ReadTail(string path, int lines)
    {
        try { return File.Exists(path) ? string.Join("\n", File.ReadAllLines(path).TakeLast(lines)) : ""; }
        catch (IOException) { return ""; }
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        string version = typeof(App).Assembly.GetName().Version!.ToString(3);
        var dialog = new ContentDialog
        {
            Title = $"EveryDns {version}",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"Proxy core: Xray-core {coreVersion ?? "missing"}\n\nA DNS manager and proxy client for Windows.\nCopyright © 2026 Arman Soltani",
            },
            CloseButtonText = "Close",
        };
        await ShowDialogAsync(dialog);
    }
}
