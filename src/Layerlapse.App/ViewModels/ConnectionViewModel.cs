using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Layerlapse.Core.Credentials;
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
public partial class ConnectionViewModel(PrinterSetupService setup) : ViewModelBase
{
    private CancellationTokenSource? _work;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetup), nameof(IsConnecting), nameof(IsConnected), nameof(IsFailed), nameof(IsCertificateChanged), nameof(StateLabel))]
    public partial ConnectionState State { get; private set; } = ConnectionState.Setup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrinterTitle), nameof(PrinterDetail), nameof(CanCancelEdit))]
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

    public string PrinterTitle => Profile is null ? "No printer" : Profile.Serial ?? Profile.Host;

    public string PrinterDetail => Profile is null ? "" : Profile.Host;

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

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SaveAsync()
    {
        await RunAsync("Connecting…", async token =>
        {
            var result = await setup.ConnectAndSaveAsync(Host, AccessCode, token);
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
