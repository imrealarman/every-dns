using EveryDns.Services;
using Microsoft.UI.Xaml;

namespace EveryDns;

public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        // The elevated copy started for a DNS change has no window: it does the change and leaves.
        string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (DnsChanger.IsCliRequest(args)) Environment.Exit(DnsChanger.Run(args));

        UnhandledException += (_, e) => { Log.Write("Unhandled UI exception", e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("Unobserved task exception", e.Exception); e.SetObserved(); };

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs launchArgs)
    {
        window = new MainWindow();
        window.Activate();
    }
}
