using System.ComponentModel;
using System.Runtime.CompilerServices;
using EveryDns.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EveryDns.Models;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Colours for speed and health. Green is fine, amber is slow, red is failing.</summary>
public static class Tones
{
    public static readonly SolidColorBrush Good = new(Color.FromArgb(255, 0x3D, 0xBE, 0x7A));
    public static readonly SolidColorBrush Warn = new(Color.FromArgb(255, 0xE5, 0xA0, 0x2B));
    public static readonly SolidColorBrush Bad = new(Color.FromArgb(255, 0xE5, 0x4B, 0x4B));

    public static Brush ForLatency(double? ms) => ms switch { null => Bad, < 80 => Good, < 200 => Warn, _ => Bad };

    public static Brush FlagBrush(string? file)
    {
        if (file == null) return new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
        return new ImageBrush { ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file)), Stretch = Stretch.UniformToFill };
    }

    public static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>One DNS server in use on the selected adapter, with its live ping and lookup time.</summary>
public sealed class ServerRow : Observable
{
    private string result = "…";
    private Brush resultBrush = Tones.Warn;

    public string Address { get; init; } = "";
    public string Label { get; init; } = "";
    public string Result { get => result; set => Set(ref result, value); }
    public Brush ResultBrush { get => resultBrush; set => Set(ref resultBrush, value); }
}

/// <summary>A server choice in the list: a built-in or saved provider, with its last test result.</summary>
public sealed class PresetRow : Observable
{
    private string result = "";
    private Brush resultBrush = Tones.Good;
    private bool fastest, inUse;

    public PresetRow(DnsPreset preset) => Preset = preset;

    public DnsPreset Preset { get; }
    public string Name => Preset.Name;
    public string Note => Preset.Note;
    public string Addresses => string.Join("   ", Preset.V4.Concat(Preset.V6.Take(1)).Take(3));
    public Visibility CanRemove => Tones.Show(Preset.Custom);

    public string Result { get => result; set => Set(ref result, value); }
    public Brush ResultBrush { get => resultBrush; set => Set(ref resultBrush, value); }
    public bool Fastest { get => fastest; set { Set(ref fastest, value); Raise(nameof(FastestVisible)); } }
    public bool InUse { get => inUse; set { Set(ref inUse, value); Raise(nameof(InUseVisible)); } }
    public Visibility FastestVisible => Tones.Show(fastest);
    public Visibility InUseVisible => Tones.Show(inUse);
}

/// <summary>One proxy server in the list, with its last ping.</summary>
public sealed class ProxyRow : Observable
{
    private string ping = "";
    private Brush pingBrush = Tones.Warn;
    private Brush flag = Tones.FlagBrush(null);

    public ProxyRow(ProxyServer server) => Server = server;

    public ProxyServer Server { get; }
    public string Name => Server.Name;
    public string Detail => $"{Server.Protocol.ToUpperInvariant()}  ·  {Server.Address}:{Server.Port}";
    public string Ping { get => ping; set => Set(ref ping, value); }
    public Brush PingBrush { get => pingBrush; set => Set(ref pingBrush, value); }

    /// <summary>The country flag, cut to a circle by the ellipse it fills; a plain grey disc until the flag is known.</summary>
    public Brush Flag { get => flag; private set => Set(ref flag, value); }

    public void SetFlag(string? file) => Flag = Tones.FlagBrush(file);
}

/// <summary>An adapter in the drop-down.</summary>
public sealed class AdapterRef
{
    public AdapterRef(AdapterInfo info) => Info = info;
    public AdapterInfo Info { get; }
    public string Display => Info.Display;
}

public sealed class AnswerRow
{
    public string Type { get; init; } = "";
    public string Data { get; init; } = "";
    public string Detail { get; init; } = "";
}

/// <summary>Where a look-up is sent: the Windows resolver, or one DNS server directly.</summary>
public sealed class ServerChoice
{
    public string Label { get; init; } = "";
    public System.Net.IPAddress? Address { get; init; }
    public override string ToString() => Label;
}

/// <summary>One site in the Services tab, with its last test result.</summary>
public sealed class ServiceRow : Observable
{
    private string result = "";
    private Brush resultBrush = Tones.Warn;
    private Brush dot = Tones.FlagBrush(null);

    public ServiceRow(ServiceSite site) => Site = site;

    public ServiceSite Site { get; }
    public string Name => Site.Name;
    public string Host => Site.Host;
    public string Result { get => result; set => Set(ref result, value); }
    public Brush ResultBrush { get => resultBrush; set => Set(ref resultBrush, value); }
    public Brush Dot { get => dot; set => Set(ref dot, value); }
}
