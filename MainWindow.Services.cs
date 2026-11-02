using EveryDns.Models;
using EveryDns.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EveryDns;

// The Services tab: can this PC reach ChatGPT, Claude, Google, GitHub and so on?
public sealed partial class MainWindow
{
    private readonly List<ServiceRow> serviceRows = ServiceTests.Sites.Select(s => new ServiceRow(s)).ToList();

    private void InitServices()
    {
        ServiceList.ItemsSource = serviceRows;
        ServicesVia.Text = "Testing through your normal connection";
    }

    private async void TestService_Click(object sender, RoutedEventArgs e) => await TestServiceAsync((ServiceRow)((Button)sender).Tag);

    private async void TestAllServices_Click(object sender, RoutedEventArgs e)
    {
        TestAllServices.IsEnabled = false;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(serviceRows.Select(async row =>
        {
            await gate.WaitAsync();
            try { await TestServiceAsync(row); }
            finally { gate.Release(); }
        }));
        TestAllServices.IsEnabled = true;

        int reachable = serviceRows.Count(r => r.ResultBrush == Tones.Good);
        Notify($"{reachable} of {serviceRows.Count} reachable.", bad: reachable < serviceRows.Count / 2);
    }

    private async Task TestServiceAsync(ServiceRow row)
    {
        row.Result = "…";
        row.ResultBrush = Tones.Warn;
        row.Dot = Tones.Warn;
        var result = await ServiceTests.TestAsync(row.Site, viaLocalProxy: connected);
        row.Result = result.Text;
        row.ResultBrush = result.Reachable ? Tones.Good : Tones.Bad;
        row.Dot = row.ResultBrush;
    }
}
