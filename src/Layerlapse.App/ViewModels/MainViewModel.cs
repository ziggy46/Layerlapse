using System.ComponentModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Layerlapse.Core.Setup;
using Layerlapse.Core.Timelapses;

namespace Layerlapse.App.ViewModels;

public enum AppPage
{
    Printer,
    Timelapses,
    Models,
    Settings,
}

/// <summary>
/// The window: sidebar navigation between the printer connection and its timelapses. At launch the cached
/// timelapses of the last printer show at once; they refresh when the connection succeeds.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly PrinterSetupService? _setup;
    private readonly Func<string, TimelapseCache> _cacheFactory;
    private readonly IVideoPlayer _player;
    private readonly Func<string, string?, Task<string?>>? _pickFolder;
    private readonly Action<string>? _openInSlicer;
    private readonly JsonSettingsStore? _settings;
    private readonly Action<string>? _revealFolder;
    private readonly Func<string, string, string, Task<bool>>? _confirmDelete;
    private string? _attachedKey;
    private Layerlapse.Core.Printers.PrinterSession? _session;

    public MainViewModel(
        ConnectionViewModel connection,
        PrinterSetupService? setup = null,
        Func<string, TimelapseCache>? cacheFactory = null,
        IVideoPlayer? player = null,
        Func<string, string?, Task<string?>>? pickFolder = null,
        JsonSettingsStore? settings = null,
        Action<string>? revealFolder = null,
        Action<string>? openInSlicer = null,
        SettingsViewModel? settingsPage = null,
        Func<string, string, string, Task<bool>>? confirmDelete = null)
    {
        Connection = connection;
        _setup = setup;
        _cacheFactory = cacheFactory ?? TimelapseCache.ForPrinter;
        _player = player ?? new DefaultAppVideoPlayer();
        _pickFolder = pickFolder;
        _settings = settings;
        _revealFolder = revealFolder;
        _openInSlicer = openInSlicer;
        _confirmDelete = confirmDelete;
        Settings = settingsPage ?? new SettingsViewModel(settings);
        Settings.Changed += async (_, _) =>
        {
            if (Timelapses is { } timelapses)
            {
                await timelapses.ApplySettingsAsync();
            }
        };
        Connection.PropertyChanged += OnConnectionChanged;
    }

    public SettingsViewModel Settings { get; }

    public ConnectionViewModel Connection { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentContent), nameof(NavIndex))]
    public partial AppPage CurrentPage { get; private set; } = AppPage.Printer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentContent), nameof(HasTimelapses))]
    public partial TimelapsesViewModel? Timelapses { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentContent))]
    public partial ModelsViewModel? Models { get; private set; }

    public bool HasTimelapses => Timelapses is not null;

    public ViewModelBase CurrentContent => CurrentPage switch
    {
        AppPage.Timelapses when Timelapses is not null => Timelapses,
        AppPage.Models when Models is not null => Models,
        AppPage.Settings => Settings,
        _ => Connection,
    };

    /// <summary>Sidebar list selection: 0 = Timelapses, 1 = Models; -1 when the printer page is showing.</summary>
    public int NavIndex
    {
        get => CurrentPage switch { AppPage.Timelapses => 0, AppPage.Models => 1, AppPage.Settings => 2, _ => -1 };
        set
        {
            if (value == 0 && Timelapses is not null)
            {
                CurrentPage = AppPage.Timelapses;
            }
            else if (value == 1 && Models is not null)
            {
                CurrentPage = AppPage.Models;
            }
            else if (value == 2)
            {
                CurrentPage = AppPage.Settings;
            }
            else
            {
                OnPropertyChanged();
            }
        }
    }

    [ObservableProperty]
    public partial bool IsLightTheme { get; set; }

    public async Task InitializeAsync()
    {
        await Settings.LoadAsync();
        _ = Settings.CheckForUpdatesQuietlyAsync();
        if (_setup is not null && await _setup.GetLastAsync() is { } last)
        {
            await ShowTimelapsesForAsync(last.Id);
            CurrentPage = AppPage.Timelapses;
        }

        await Connection.InitializeAsync();
    }

    [RelayCommand]
    private void ShowPrinter() => CurrentPage = AppPage.Printer;

    partial void OnIsLightThemeChanged(bool value)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = value ? ThemeVariant.Light : ThemeVariant.Dark;
        }
    }

    private async void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ConnectionViewModel.State) or nameof(ConnectionViewModel.Profile)))
        {
            return;
        }

        try
        {
            await SyncWithConnectionAsync();
        }
        catch (Exception)
        {
            // async void handler: never let an exception reach the UI thread. The printer page shows the state.
            CurrentPage = AppPage.Printer;
        }
    }

    private async Task SyncWithConnectionAsync()
    {
        var profile = Connection.Profile;
        if (profile is null)
        {
            await CloseTimelapsesAsync();
            CurrentPage = AppPage.Printer;
            return;
        }

        switch (Connection.State)
        {
            case ConnectionState.Connected:
                var wasShowingPrinterAfterSetup = Timelapses?.PrinterId != profile.Id;
                await ShowTimelapsesForAsync(profile.Id);
                if (_setup is not null && Timelapses is { } timelapses && _attachedKey != profile.Id + "@" + profile.Host)
                {
                    _attachedKey = profile.Id + "@" + profile.Host;
                    if (wasShowingPrinterAfterSetup)
                    {
                        CurrentPage = AppPage.Timelapses;
                    }

                    if (_session is not null)
                    {
                        await _session.DisposeAsync();
                    }

                    // One shared connection for both pages: operations take turns.
                    _session = await _setup.OpenSessionAsync(profile);
                    var models = Models;
                    var session = _session;
                    await timelapses.ApplySettingsAsync();
                    await timelapses.AttachAsync(session);
                    if (models is not null)
                    {
                        await models.AttachAsync(session);
                    }

                    _ = MeasureStorageAsync(session);
                }

                break;

            case ConnectionState.Failed or ConnectionState.CertificateChanged or ConnectionState.Setup:
                _attachedKey = null;
                Timelapses?.MarkOffline();
                Models?.MarkOffline();
                CurrentPage = AppPage.Printer;
                break;
        }
    }

    /// <summary>Space used per kind of file, shown on the printer page. Optional: failures are ignored.</summary>
    private async Task MeasureStorageAsync(Layerlapse.Core.Printers.PrinterSession session)
    {
        try
        {
            var usage = await Layerlapse.Core.Printers.StorageUsage.MeasureAsync(session);
            if (ReferenceEquals(session, _session))
            {
                Connection.StorageText =
                    $"Storage used: {TimelapseItemViewModel.FormatSize(usage.TotalBytes)} · timelapses {TimelapseItemViewModel.FormatSize(usage.Timelapses.Bytes)} ({usage.Timelapses.Files}) · " +
                    $"models {TimelapseItemViewModel.FormatSize(usage.Models.Bytes)} ({usage.Models.Files}) · camera recordings {TimelapseItemViewModel.FormatSize(usage.CameraRecordings.Bytes)} ({usage.CameraRecordings.Files}). " +
                    "The printer does not report free space.";
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task ShowTimelapsesForAsync(string printerId)
    {
        if (Timelapses?.PrinterId == printerId)
        {
            return;
        }

        await CloseTimelapsesAsync();
        var cache = _cacheFactory(printerId);
        Timelapses = new TimelapsesViewModel(printerId, cache, _player, _pickFolder, _settings, _revealFolder, _confirmDelete);
        Models = new ModelsViewModel(cache.Root, _pickFolder, _settings, _revealFolder, _openInSlicer);
        await Timelapses.LoadCachedAsync();
        await Models.LoadCachedAsync();
    }

    private async Task CloseTimelapsesAsync()
    {
        if (Timelapses is { } old)
        {
            Timelapses = null;
            _attachedKey = null;
            await old.DisposeAsync();
        }

        if (Models is { } oldModels)
        {
            Models = null;
            await oldModels.DisposeAsync();
        }

        if (_session is { } session)
        {
            _session = null;
            await session.DisposeAsync();
        }
    }
}
