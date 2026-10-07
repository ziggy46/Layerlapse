using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Layerlapse.App.ViewModels;
using Layerlapse.App.Views;
using Layerlapse.Core.Credentials;
using Layerlapse.Core.Discovery;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Setup;

namespace Layerlapse.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var discovery = new PrinterDiscovery();
            var setup = new PrinterSetupService(
                CredentialStores.CreateDefault(),
                JsonPrinterProfileStore.CreateDefault(),
                connection => new BambuFtpsClient(connection),
                discovery);
            var main = new MainViewModel(new ConnectionViewModel(setup, discovery), setup);
            desktop.MainWindow = new MainWindow { DataContext = main };

            // Reconnect to the last printer with no typing; the view shows progress and any error.
            _ = main.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}