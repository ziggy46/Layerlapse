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
#if DEBUG
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { Args: { } args } playerOnly
            && args.Contains("--play"))
        {
            var saved = Task.Run(() => JsonSettingsStore.CreateDefault().LoadAsync()).GetAwaiter().GetResult();
            Themes.ThemeManager.Apply(Themes.ThemeManager.FromName(saved.Theme));
            if (DebugPlayer(args) is { } playerWindow)
            {
                playerOnly.MainWindow = playerWindow;
                base.OnFrameworkInitializationCompleted();
                return;
            }
        }
#endif
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var discovery = new PrinterDiscovery();
            var setup = new PrinterSetupService(
                CreateCredentialStore(),
                JsonPrinterProfileStore.CreateDefault(),
                connection => new BambuFtpsClient(connection),
                discovery);
            var player = new BuiltInVideoPlayer(() => desktop.MainWindow);
            var settings = JsonSettingsStore.CreateDefault();

            // Apply the saved theme before the first window appears (no flash of the default theme).
            var saved = Task.Run(() => settings.LoadAsync()).GetAwaiter().GetResult();
            Themes.ThemeManager.Apply(Themes.ThemeManager.FromName(saved.Theme));
            Func<string, string?, Task<string?>> pickFolder = (title, start) => PickFolderAsync(desktop.MainWindow, title, start);
            Task<bool> Ask(string title, string message, string confirm, bool destructive) =>
                desktop.MainWindow is { } owner ? ConfirmDialog.AskAsync(owner, title, message, confirm, destructive) : Task.FromResult(false);
            var settingsPage = new SettingsViewModel(
                settings,
                new UpdateChecker(new System.Net.Http.HttpClient(), UpdateChecker.DefaultFeed),
                pickFolder,
                (title, message, confirm) => Ask(title, message, confirm, destructive: false),
                OpenWithDefaultApp);
            var main = new MainViewModel(
                new ConnectionViewModel(setup, discovery), setup,
                player: player,
                pickFolder: pickFolder,
                settings: settings,
                revealFolder: OpenWithDefaultApp,
                openInSlicer: path => BambuStudioLauncher.Open(path, OpenWithDefaultApp),
                settingsPage: settingsPage,
                confirmDelete: (title, message, confirm) => Ask(title, message, confirm, destructive: true));
            desktop.MainWindow = new MainWindow { DataContext = main };

            // Reconnect to the last printer with no typing; the view shows progress and any error.
            _ = main.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

#if DEBUG
    /// <summary>
    /// Development builds only: <c>--play &lt;video&gt; [--seek &lt;seconds&gt;]</c> opens just the player window
    /// (seeking 3 s after it opens), so the players can be checked on build machines without clicking.
    /// </summary>
    private static Window? DebugPlayer(string[] args)
    {
        var play = Array.IndexOf(args, "--play");
        if (play < 0 || play + 1 >= args.Length)
        {
            return null;
        }

        var window = Player.BuiltInVideoPlayer.CreateWindow(Path.GetFullPath(args[play + 1]));
        var seek = Array.IndexOf(args, "--seek");
        if (window is not null && seek >= 0 && seek + 1 < args.Length
            && double.TryParse(args[seek + 1], System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            window.Opened += (_, _) => Avalonia.Threading.DispatcherTimer.RunOnce(
                () => (window.Video as Player.IPlaybackView)?.Seek(seconds), TimeSpan.FromSeconds(3));
        }

        return window;
    }
#endif

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