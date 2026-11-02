using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EveryDns.Services;

/// <summary>The proxy setting Windows had before EveryDns turned its own on.</summary>
public sealed class ProxyBackup
{
    public int Enabled { get; set; }
    public string Server { get; set; } = "";
    public string Override { get; set; } = "";
}

/// <summary>Points Windows (browsers and most apps) at the local proxy, and puts the old setting back afterwards.</summary>
public static class SystemProxy
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string Bypass = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.2*;172.30.*;172.31.*;192.168.*;<local>";

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);

    public static string Address => $"127.0.0.1:{ProxyLink.HttpPort}";

    public static ProxyBackup Enable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true) ?? throw new InvalidOperationException("Windows proxy settings are not available.");
        var backup = new ProxyBackup
        {
            Enabled = key.GetValue("ProxyEnable") is int on ? on : 0,
            Server = key.GetValue("ProxyServer") as string ?? "",
            Override = key.GetValue("ProxyOverride") as string ?? "",
        };
        key.SetValue("ProxyServer", Address);
        key.SetValue("ProxyOverride", Bypass);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        Notify();
        return backup;
    }

    public static void Restore(ProxyBackup backup)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key == null) return;
        key.SetValue("ProxyEnable", backup.Enabled, RegistryValueKind.DWord);
        SetOrDelete(key, "ProxyServer", backup.Server);
        SetOrDelete(key, "ProxyOverride", backup.Override);
        Notify();
    }

    /// <summary>True while Windows is pointed at our local port (even if a crash left it that way).</summary>
    public static bool PointsAtUs()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("ProxyEnable") is 1 && key.GetValue("ProxyServer") as string == Address;
    }

    private static void SetOrDelete(RegistryKey key, string name, string value)
    {
        if (value.Length > 0) key.SetValue(name, value);
        else key.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // settings changed
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // refresh
    }
}
