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
                CredentialStores.CreateDefault(),
                JsonPrinterProfileStore.CreateDefault(),
                connection => new BambuFtpsClient(connection),
                discovery);
            var player = new BuiltInVideoPlayer(() => desktop.MainWindow);
            var main = new MainViewModel(
                new ConnectionViewModel(setup, discovery), setup,
                player: player,
                pickFolder: start => PickFolderAsync(desktop.MainWindow, start),
                settings: JsonSettingsStore.CreateDefault(),
                revealFolder: RevealFolder);
            desktop.MainWindow = new MainWindow { DataContext = main };

            // Reconnect to the last printer with no typing; the view shows progress and any error.
            _ = main.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Asks where to save downloads, starting at the last used folder or Downloads.</summary>
    private static async Task<string?> PickFolderAsync(TopLevel? window, string? start)
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
            Title = "Choose where to save timelapses",
            AllowMultiple = false,
            SuggestedStartLocation = Directory.Exists(startPath) ? await storage.TryGetFolderFromPathAsync(startPath) : null,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>Opens the folder in Finder, Explorer or the Linux file manager.</summary>
    private static void RevealFolder(string folder)
    {
        var start = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open") { ArgumentList = { folder } }
            : OperatingSystem.IsLinux() ? new ProcessStartInfo("xdg-open") { ArgumentList = { folder } }
            : new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } };
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