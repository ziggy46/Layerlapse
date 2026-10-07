using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Layerlapse.App.Player;
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
                CreateCredentialStore(),
                JsonPrinterProfileStore.CreateDefault(),
                connection => new BambuFtpsClient(connection),
                discovery);
            var player = new BuiltInVideoPlayer(() => desktop.MainWindow);
            var main = new MainViewModel(
                new ConnectionViewModel(setup, discovery), setup,
                player: player,
                pickFolder: (title, start) => PickFolderAsync(desktop.MainWindow, title, start),
                settings: JsonSettingsStore.CreateDefault(),
                revealFolder: OpenWithDefaultApp,
                openInSlicer: path => BambuStudioLauncher.Open(path, OpenWithDefaultApp));
            desktop.MainWindow = new MainWindow { DataContext = main };

            // Reconnect to the last printer with no typing; the view shows progress and any error.
            _ = main.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ICredentialStore CreateCredentialStore()
    {
#if DEBUG
        // Development builds: avoid a Keychain prompt after every rebuild (see DevEnvFileCredentialStore).
        if (File.Exists(DevEnvFileCredentialStore.DefaultPath))
        {
            return new DevEnvFileCredentialStore(DevEnvFileCredentialStore.DefaultPath);
        }
#endif
        return CredentialStores.CreateDefault();
    }

    /// <summary>Asks where to save downloads, starting at the last used folder or Downloads.</summary>
    private static async Task<string?> PickFolderAsync(TopLevel? window, string title, string? start)
    {
        if (window is null)
        {
            return null;
        }

        var storage = window.StorageProvider;
        var startPath = start is not null && Directory.Exists(start)
            ? start
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = Directory.Exists(startPath) ? await storage.TryGetFolderFromPathAsync(startPath) : null,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// Opens a folder in Finder, Explorer or the Linux file manager, or a file in its default app
    /// (a .3mf in Bambu Studio when it is installed).
    /// </summary>
    private static void OpenWithDefaultApp(string path)
    {
        var start = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open") { ArgumentList = { path } }
            : OperatingSystem.IsLinux() ? new ProcessStartInfo("xdg-open") { ArgumentList = { path } }
            : new ProcessStartInfo(path) { UseShellExecute = true };
        try
        {
            using var _ = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Nothing to open it with; the path is shown in the app.
        }
    }
}