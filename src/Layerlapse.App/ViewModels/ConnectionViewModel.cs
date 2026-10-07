using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Layerlapse.Core.Credentials;
using Layerlapse.Core.Discovery;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Setup;

namespace Layerlapse.App.ViewModels;

public enum ConnectionState
{
    /// <summary>Showing the IP and access code form.</summary>
    Setup,
    Connecting,
    Connected,

    /// <summary>A saved printer could not be reached; offer retry or edit.</summary>
    Failed,

    /// <summary>The printer's certificate differs from the pinned one; warn and ask before trusting it.</summary>
    CertificateChanged,
}

/// <summary>
/// Printer connection: first-time setup, reconnecting to the saved printer at launch, and recovering from errors.
/// </summary>
public partial class ConnectionViewModel(PrinterSetupService setup, IPrinterDiscovery? discovery = null) : ViewModelBase
{
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _search;

    /// <summary>Printers found by the last search, in the order found.</summary>
    public ObservableCollection<DiscoveredPrinterItem> Discovered { get; } = [];

    public bool HasDiscovered => Discovered.Count > 0;

    public IReadOnlyList<string> ModelChoices => PrinterModels.All;

    public bool CanSearch => discovery is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial bool IsSearching { get; private set; }

    [ObservableProperty]
    public partial string? SearchStatus { get; private set; }

    /// <summary>Offered after a search heard no announcements: probe port 990 on this computer's local network.</summary>
    [ObservableProperty]
    public partial bool CanScan { get; private set; }

    /// <summary>Choosing a found printer fills in its address.</summary>
    [ObservableProperty]
    public partial DiscoveredPrinterItem? SelectedDiscovered { get; set; }

    /// <summary>The model picked by the user when it could not be detected.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveModelCommand))]
    public partial string? ChosenModel { get; set; }

    partial void OnSelectedDiscoveredChanged(DiscoveredPrinterItem? value)
    {
        if (value is not null)
        {
            Host = value.Printer.Host;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetup), nameof(IsConnecting), nameof(IsConnected), nameof(IsFailed), nameof(IsCertificateChanged), nameof(StateLabel))]
    public partial ConnectionState State { get; private set; } = ConnectionState.Setup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrinterTitle), nameof(PrinterDetail), nameof(CanCancelEdit), nameof(ModelText), nameof(ShowModelLine), nameof(NeedsModelChoice), nameof(SerialText), nameof(FirmwareText), nameof(StateLabel))]
    public partial PrinterProfile? Profile { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(SaveCommand))]
    public partial string Host { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(SaveCommand))]
    public partial string AccessCode { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(SaveCommand), nameof(RetryCommand), nameof(TrustCertificateCommand), nameof(ForgetCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Neutral progress or success text.</summary>
    [ObservableProperty]
    public partial string? Status { get; private set; }

    /// <summary>Error text, shown in the danger colour.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Non-fatal problem, such as the code not being saved.</summary>
    [ObservableProperty]
    public partial string? Warning { get; private set; }

    [ObservableProperty]
    public partial string? ExpectedFingerprint { get; private set; }

    [ObservableProperty]
    public partial string? ActualFingerprint { get; private set; }

    public bool IsSetup => State == ConnectionState.Setup;

    public bool IsConnecting => State == ConnectionState.Connecting;

    public bool IsConnected => State == ConnectionState.Connected;

    public bool IsFailed => State == ConnectionState.Failed;

    public bool IsCertificateChanged => State == ConnectionState.CertificateChanged;

    /// <summary>Editing an existing printer can be abandoned; first-time setup cannot.</summary>
    public bool CanCancelEdit => Profile is not null;

    public string PrinterTitle => Profile?.DisplayName ?? "No printer";

    public string PrinterDetail => Profile is null ? "" : Profile.Host;

    public string ModelText => Profile?.Model ?? (Profile?.ModelCode is { } code ? $"Unknown model ({code})" : "Unknown model");

    /// <summary>Hide the model line when the name already says it (the default name is the model).</summary>
    public bool ShowModelLine => !string.Equals(PrinterTitle, ModelText, StringComparison.Ordinal);

    public string? SerialText => Profile?.Serial is { } serial ? $"Serial {serial}" : null;

    public string? FirmwareText => Profile?.Firmware is { } firmware ? $"Firmware {firmware}" : null;

    /// <summary>Connected, but the model could not be detected: ask the user.</summary>
    public bool NeedsModelChoice => Profile is not null && Profile.Model is null;

    public string CredentialStoreName => setup.Credentials.Name;

    /// <summary>One or two words for the sidebar.</summary>
    public string StateLabel => State switch
    {
        ConnectionState.Connected => "Connected",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.CertificateChanged => "Certificate changed",
        ConnectionState.Failed => "Not connected",
        _ => Profile is null ? "Not set up" : "Not connected",
    };

    /// <summary>At launch: reconnect to the last printer with no typing, or show setup.</summary>
    public async Task InitializeAsync()
    {
        Profile = await setup.GetLastAsync();
        if (Profile is null)
        {
            ShowSetup();
            return;
        }

        await ReconnectAsync();
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task TestAsync()
    {
        await RunAsync("Testing the connection…", async token =>
        {
            var result = await setup.TestAsync(Host, AccessCode, cancellationToken: token);
            Status = result.Serial is null
                ? "Connection works."
                : $"Connection works. Printer serial {result.Serial}.";
        });
    }

    /// <summary>Listens for printer announcements (a few seconds, receive only).</summary>
    [RelayCommand(CanExecute = nameof(CanStartSearch))]
    private Task SearchAsync() => DiscoverAsync(new DiscoveryOptions { ScanIfNothingAnnounced = false }, "Listening for printers… this takes about 8 seconds.");

    /// <summary>Fallback when nothing announced itself: try port 990 on every address of the local network.</summary>
    [RelayCommand(CanExecute = nameof(CanStartSearch))]
    private Task ScanAsync() => DiscoverAsync(
        new DiscoveryOptions { ListenDuration = TimeSpan.FromSeconds(1), ScanIfNothingAnnounced = true },
        "Scanning the local network… this takes up to about 15 seconds.");

    private async Task DiscoverAsync(DiscoveryOptions options, string progress)
    {
        if (discovery is null)
        {
            return;
        }

        _search?.Cancel();
        _search = new CancellationTokenSource();
        var token = _search.Token;
        Discovered.Clear();
        OnPropertyChanged(nameof(HasDiscovered));
        SelectedDiscovered = null;
        IsSearching = true;
        CanScan = false;
        SearchStatus = progress;
        try
        {
            await foreach (var printer in discovery.DiscoverAsync(options, token))
            {
                Discovered.Add(new DiscoveredPrinterItem(printer));
                OnPropertyChanged(nameof(HasDiscovered));
            }

            SearchStatus = Discovered.Count switch
            {
                0 when !options.ScanIfNothingAnnounced => "No printer announced itself. Scan the network, or enter the IP address below.",
                0 => "No printers found. Check that the printer is on and on the same network, or enter its IP address below.",
                1 => "Found 1 printer.",
                var n => $"Found {n} printers. Choose yours.",
            };
            if (Discovered.Count == 1)
            {
                SelectedDiscovered = Discovered[0];
            }

            CanScan = Discovered.Count == 0 && !options.ScanIfNothingAnnounced;
        }
        catch (OperationCanceledException)
        {
            SearchStatus = null;
        }
        catch (DiscoveryException e)
        {
            SearchStatus = e.Message + " Enter the IP address below instead.";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveModel))]
    private async Task SaveModelAsync()
    {
        if (Profile is { } profile && ChosenModel is { } model)
        {
            Profile = await setup.SetModelAsync(profile, model);
            ChosenModel = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SaveAsync()
    {
        _search?.Cancel();
        var picked = SelectedDiscovered?.Printer is { } found && found.Host == Host.Trim() ? found : null;
        await RunAsync("Connecting…", async token =>
        {
            var result = await setup.ConnectAndSaveAsync(Host, AccessCode, picked, null, token);
            AccessCode = "";
            Profile = result.Profile;
            Warning = result.Warning;
            Status = result.Warning is null ? $"The access code is saved in {setup.Credentials.Name}." : null;

            State = ConnectionState.Connected;
        });
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task RetryAsync() => ReconnectAsync();

    [RelayCommand]
    private void Edit()
    {
        Host = Profile?.Host ?? Host;
        AccessCode = "";
        ShowSetup();
    }

    [RelayCommand]
    private Task CancelEditAsync() => Profile is null ? Task.CompletedTask : ReconnectAsync();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task TrustCertificateAsync()
    {
        if (Profile is not { } profile)
        {
            return;
        }

        await RunAsync("Trusting the new certificate…", async token =>
        {
            Profile = await setup.TrustNewCertificateAsync(profile, AccessCode, token);
            AccessCode = "";
            ExpectedFingerprint = ActualFingerprint = null;
            Status = "The new certificate is now trusted.";
            State = ConnectionState.Connected;
        });
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ForgetAsync()
    {
        if (Profile is { } profile)
        {
            try
            {
                await setup.ForgetAsync(profile);
            }
            catch (CredentialStoreException e)
            {
                Warning = e.Message;
            }
        }

        Profile = null;
        Host = "";
        AccessCode = "";
        ShowSetup();
    }

    private bool IsIdle() => !IsBusy;

    private bool CanStartSearch() => discovery is not null && !IsSearching;

    partial void OnIsSearchingChanged(bool value) => ScanCommand.NotifyCanExecuteChanged();

    private bool CanSaveModel() => ChosenModel is not null;

    private bool CanSubmit() => !IsBusy && Host.Trim().Length > 0 && AccessCode.Trim().Length > 0;

    private async Task ReconnectAsync()
    {
        if (Profile is not { } profile)
        {
            ShowSetup();
            return;
        }

        State = ConnectionState.Connecting;
        await RunAsync($"Connecting to {PrinterTitle} at {profile.Host}…", async token =>
        {
            Profile = await setup.ReconnectAsync(profile, token);
            Status = null;
            State = ConnectionState.Connected;
        });

        if (IsConnected && Profile is { } connected)
        {
            _ = RefreshDetailsAsync(connected);
        }
    }

    /// <summary>Picks up the printer's current name and firmware from its announcement, in the background.</summary>
    private async Task RefreshDetailsAsync(PrinterProfile connected)
    {
        try
        {
            var refreshed = await setup.RefreshDetailsAsync(connected);
            if (Profile?.Id == refreshed.Id && refreshed != connected)
            {
                Profile = Profile with { Name = refreshed.Name, Firmware = refreshed.Firmware, ModelCode = refreshed.ModelCode, Model = Profile.Model ?? refreshed.Model };
            }
        }
        catch (Exception)
        {
            // Details are optional and this runs in the background; the connection already works.
        }
    }

    private void ShowSetup()
    {
        State = ConnectionState.Setup;
        Status = null;
    }

    /// <summary>Runs one connection attempt and maps every failure to a state and a message.</summary>
    private async Task RunAsync(string progress, Func<CancellationToken, Task> action)
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        IsBusy = true;
        Error = null;
        Warning = null;
        Status = progress;
        try
        {
            await action(_work.Token);
        }
        catch (PrinterCertificateMismatchException e)
        {
            ExpectedFingerprint = FormatFingerprint(e.ExpectedFingerprint);
            ActualFingerprint = FormatFingerprint(e.ActualFingerprint);
            Fail(e.Message, Profile is null ? ConnectionState.Setup : ConnectionState.CertificateChanged);
            if (State == ConnectionState.CertificateChanged)
            {
                Error = null; // the warning panel explains it, with both fingerprints
            }
        }
        catch (PrinterAuthenticationException e)
        {
            // A saved code that no longer works means the printer was probably reset: ask for the new one.
            Host = Profile?.Host ?? Host;
            AccessCode = "";
            Fail(e.Message, ConnectionState.Setup);
        }
        catch (AccessCodeMissingException e)
        {
            Host = Profile?.Host ?? Host;
            Fail(e.Message, ConnectionState.Setup);
        }
        catch (PrinterConnectionException e)
        {
            Fail(e.Message, State == ConnectionState.Setup ? ConnectionState.Setup : ConnectionState.Failed);
        }
        catch (OperationCanceledException)
        {
            Status = null;
        }
        catch (Exception e)
        {
            // Safety net: never leave the spinner running. Expected failures are handled above.
            Fail("Something went wrong: " + e.Message, Profile is null ? ConnectionState.Setup : ConnectionState.Failed);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fail(string message, ConnectionState state)
    {
        Status = null;
        Error = message;
        State = state;
    }

    /// <summary>"145C6BBE…" as "145C 6BBE …" so two fingerprints are easy to compare by eye.</summary>
    public static string FormatFingerprint(string hex) =>
        string.Join(' ', Enumerable.Range(0, (hex.Length + 3) / 4).Select(i => hex.Substring(i * 4, Math.Min(4, hex.Length - (i * 4)))));
}
