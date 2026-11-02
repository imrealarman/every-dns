using System.Runtime.InteropServices;
using EveryDns.Models;
using EveryDns.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using WinRT.Interop;

namespace EveryDns;

/// <summary>A small card: a Proxy tab, a DNS tab and a Lookup tab. Each tab lives in its own partial file.</summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly AppSettings settings = AppSettings.Load();
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private string tab = "proxy";

    public MainWindow()
    {
        InitializeComponent();
        Title = "EveryDns";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        PlaceWindow();

        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        try
        {
            if (File.Exists(Path.Combine(assets, "AppIcon.ico"))) AppWindow.SetIcon(Path.Combine(assets, "AppIcon.ico"));
            if (File.Exists(Path.Combine(assets, "AppIcon.png"))) TitleIcon.Source = new BitmapImage(new Uri(Path.Combine(assets, "AppIcon.png")));
        }
        catch (Exception ex) { Log.Write("Could not load the icon", ex); }

        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); NoticeBar.Visibility = Visibility.Collapsed; };
        Root.ActualThemeChanged += (_, _) => ApplyThemeChrome();
        ApplyTheme();

        InitProxy();
        InitDns();
        InitLookup();
        Closed += (_, _) => { StopMonitor(); ShutdownProxy(); };
    }

    // ---- window ------------------------------------------------------------------------------------------------

    private void PlaceWindow()
    {
        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = (int)(420 * scale), h = Math.Min((int)(720 * scale), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsResizable = false; presenter.IsMaximizable = false; }
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        ApplyThemeChrome();
    }

    private void ApplyThemeChrome()
    {
        bool light = Root.ActualTheme == ElementTheme.Light;
        Palette.Apply(light);
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = light ? Colors.Black : Colors.White;
        bar.ButtonInactiveForegroundColor = Colors.Gray;
        bar.ButtonHoverBackgroundColor = light ? ColorHelper.FromArgb(0x14, 0, 0, 0) : ColorHelper.FromArgb(0x24, 255, 255, 255);
        bar.ButtonHoverForegroundColor = light ? Colors.Black : Colors.White;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        settings.Theme = settings.Theme switch { "system" => "light", "light" => "dark", _ => "system" };
        settings.Save();
        ApplyTheme();
        Notify("Theme: " + settings.Theme);
    }

    // ---- tabs and messages -------------------------------------------------------------------------------------

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        tab = (string)((Button)sender).Tag;
        PageProxy.Visibility = tab == "proxy" ? Visibility.Visible : Visibility.Collapsed;
        PageDns.Visibility = tab == "dns" ? Visibility.Visible : Visibility.Collapsed;
        PageServices.Visibility = tab == "services" ? Visibility.Visible : Visibility.Collapsed;
        PageLookup.Visibility = tab == "lookup" ? Visibility.Visible : Visibility.Collapsed;
        TabServices.Style = (Style)Application.Current.Resources[tab == "services" ? "ChipButtonOn" : "ChipButton"];
        if (tab == "services") ServicesVia.Text = connected ? "Testing through your proxy" : "Testing through your normal connection";
        TabProxy.Style = (Style)Application.Current.Resources[tab == "proxy" ? "ChipButtonOn" : "ChipButton"];
        TabDns.Style = (Style)Application.Current.Resources[tab == "dns" ? "ChipButtonOn" : "ChipButton"];
        TabLookup.Style = (Style)Application.Current.Resources[tab == "lookup" ? "ChipButtonOn" : "ChipButton"];

        if (tab == "dns") StartMonitor(); else StopMonitor();
    }

    private void Notify(string message, bool bad = false)
    {
        NoticeText.Text = message;
        NoticeText.Foreground = bad ? Tones.Bad : (Brush)Application.Current.Resources["TextBrush"];
        NoticeBar.Visibility = Visibility.Visible;
        noticeTimer.Stop();
        noticeTimer.Start();
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        dialog.XamlRoot = Root.XamlRoot;
        dialog.RequestedTheme = Root.ActualTheme;
        return await dialog.ShowAsync();
    }

    private static string FormatBytes(double bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000:0.00} GB",
        >= 1_000_000 => $"{bytes / 1_000_000:0.0} MB",
        >= 1_000 => $"{bytes / 1_000:0} KB",
        _ => $"{bytes:0} B",
    };
}
