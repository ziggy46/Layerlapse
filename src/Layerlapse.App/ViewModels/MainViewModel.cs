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
    private string? _attachedHost;

    public MainViewModel(ConnectionViewModel connection, PrinterSetupService? setup = null, Func<string, TimelapseCache>? cacheFactory = null, IVideoPlayer? player = null)
    {
        Connection = connection;
        _setup = setup;
        _cacheFactory = cacheFactory ?? TimelapseCache.ForPrinter;
        _player = player ?? new DefaultAppVideoPlayer();
        Connection.PropertyChanged += OnConnectionChanged;
    }

    public ConnectionViewModel Connection { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentContent), nameof(NavIndex))]
    public partial AppPage CurrentPage { get; private set; } = AppPage.Printer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentContent), nameof(HasTimelapses))]
    public partial TimelapsesViewModel? Timelapses { get; private set; }

    public bool HasTimelapses => Timelapses is not null;

    public ViewModelBase CurrentContent => CurrentPage == AppPage.Timelapses && Timelapses is not null ? Timelapses : Connection;

    /// <summary>Sidebar list selection: 0 = Timelapses; -1 when the printer page is showing.</summary>
    public int NavIndex
    {
        get => CurrentPage == AppPage.Timelapses ? 0 : -1;
        set
        {
            if (value == 0 && Timelapses is not null)
            {
                CurrentPage = AppPage.Timelapses;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AccessCodeMissingException)
        {
            if (Timelapses is not null)
            {
                CurrentPage = AppPage.Printer;
            }
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
                if (_setup is not null && Timelapses is { } timelapses && _attachedHost != profile.Host)
                {
                    _attachedHost = profile.Host;
                    if (wasShowingPrinterAfterSetup)
                    {
                        CurrentPage = AppPage.Timelapses;
                    }

                    await timelapses.AttachAsync(await _setup.OpenSessionAsync(profile));
                }

                break;

            case ConnectionState.Failed or ConnectionState.CertificateChanged or ConnectionState.Setup:
                _attachedHost = null;
                CurrentPage = AppPage.Printer;
                break;
        }
    }

    private async Task ShowTimelapsesForAsync(string printerId)
    {
        if (Timelapses?.PrinterId == printerId)
        {
            return;
        }

        await CloseTimelapsesAsync();
        Timelapses = new TimelapsesViewModel(printerId, _cacheFactory(printerId), _player);
        await Timelapses.LoadCachedAsync();
    }

    private async Task CloseTimelapsesAsync()
    {
        if (Timelapses is { } old)
        {
            Timelapses = null;
            _attachedHost = null;
            await old.DisposeAsync();
        }
    }
}
