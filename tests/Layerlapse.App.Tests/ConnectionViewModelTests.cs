using Layerlapse.App.ViewModels;
using Layerlapse.Core.Credentials;
using Layerlapse.Core.Setup;
using Layerlapse.Core.Tests;

namespace Layerlapse.App.Tests;

public sealed class ConnectionViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "layerlapse-app-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakePrinter _printer = new();
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly JsonPrinterProfileStore _profiles;
    private readonly FakeDiscovery _discovery = new();

    public ConnectionViewModelTests()
    {
        _profiles = new JsonPrinterProfileStore(Path.Combine(_folder, "printers.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>A fresh view model over the same stores, like relaunching the app.</summary>
    private ConnectionViewModel Launch() =>
        new(new PrinterSetupService(_credentials, _profiles, _printer.Create, _discovery), _discovery);

    private async Task<ConnectionViewModel> SetUpOnceAsync()
    {
        var vm = Launch();
        await vm.InitializeAsync();
        vm.Host = "192.168.1.50";
        vm.AccessCode = _printer.AccessCode;
        await vm.SaveCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task First_launch_shows_setup()
    {
        var vm = Launch();

        await vm.InitializeAsync();

        Assert.True(vm.IsSetup);
        Assert.False(vm.CanCancelEdit);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_connects_and_clears_the_code_field()
    {
        var vm = await SetUpOnceAsync();

        Assert.True(vm.IsConnected);
        Assert.Equal("", vm.AccessCode);
        Assert.Null(vm.Error);
        Assert.Equal("X1 Carbon", vm.PrinterTitle); // no name announced, so the model (from the serial prefix)
        Assert.Equal("Serial 00M000000000001", vm.SerialText);
    }

    [Fact]
    public async Task Restart_connects_with_no_typing()
    {
        await SetUpOnceAsync();

        var relaunched = Launch();
        await relaunched.InitializeAsync();

        Assert.True(relaunched.IsConnected);
        Assert.Equal("Connected", relaunched.StateLabel);
        Assert.Equal("", relaunched.AccessCode);
    }

    [Fact]
    public async Task Wrong_code_shows_a_clear_error_and_stays_on_setup()
    {
        var vm = Launch();
        await vm.InitializeAsync();
        vm.Host = "192.168.1.50";
        vm.AccessCode = "00000000";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsSetup);
        Assert.Contains("rejected the access code", vm.Error);
        Assert.Null(await _profiles.GetLastAsync());
    }

    [Fact]
    public async Task Changed_code_after_restart_asks_for_the_new_code()
    {
        await SetUpOnceAsync();
        _printer.AccessCode = "87654321"; // factory reset

        var vm = Launch();
        await vm.InitializeAsync();

        Assert.True(vm.IsSetup);
        Assert.True(vm.CanCancelEdit);
        Assert.Equal("192.168.1.50", vm.Host);
        Assert.Contains("rejected the access code", vm.Error);
    }

    [Fact]
    public async Task Unreachable_printer_offers_retry()
    {
        await SetUpOnceAsync();
        _printer.Reachable = false;

        var vm = Launch();
        await vm.InitializeAsync();
        Assert.True(vm.IsFailed);
        Assert.Contains("No printer answered", vm.Error);

        _printer.Reachable = true;
        await vm.RetryCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        Assert.Null(vm.Error);
    }

    [Fact]
    public async Task Changed_certificate_warns_and_waits_for_the_user()
    {
        await SetUpOnceAsync();
        _printer.Fingerprint = new string('B', 64);

        var vm = Launch();
        await vm.InitializeAsync();

        Assert.True(vm.IsCertificateChanged);
        Assert.StartsWith("AAAA AAAA", vm.ExpectedFingerprint);
        Assert.StartsWith("BBBB BBBB", vm.ActualFingerprint);
        Assert.Equal(new string('A', 64), (await _profiles.GetLastAsync())!.PinnedFingerprint);

        await vm.TrustCertificateCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
        Assert.Equal(new string('B', 64), (await _profiles.GetLastAsync())!.PinnedFingerprint);
    }

    [Fact]
    public async Task Unexpected_failure_never_leaves_the_spinner_running()
    {
        await SetUpOnceAsync();
        _printer.ConnectFailure = new InvalidOperationException("boom");

        var vm = Launch();
        await vm.InitializeAsync();

        Assert.True(vm.IsFailed);
        Assert.False(vm.IsBusy);
        Assert.Contains("boom", vm.Error);
    }

    [Fact]
    public async Task Factory_reset_recovers_through_edit_and_trust()
    {
        await SetUpOnceAsync();
        _printer.Fingerprint = new string('B', 64);
        _printer.AccessCode = "87654321";

        var vm = Launch();
        await vm.InitializeAsync();
        Assert.True(vm.IsCertificateChanged);

        vm.EditCommand.Execute(null);
        vm.AccessCode = "87654321";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsCertificateChanged); // a known printer's new certificate still needs confirming

        await vm.TrustCertificateCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        Assert.Equal("", vm.AccessCode);

        var relaunched = Launch();
        await relaunched.InitializeAsync();
        Assert.True(relaunched.IsConnected);
    }

    [Fact]
    public async Task One_printer_found_is_preselected_and_fills_the_address()
    {
        _discovery.Printers.Add(FakeDiscovery.Announced());
        var vm = Launch();
        await vm.InitializeAsync();

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("Found 1 printer.", vm.SearchStatus);
        Assert.Same(vm.Discovered[0], vm.SelectedDiscovered);
        Assert.Equal("192.168.1.50", vm.Host);

        vm.AccessCode = _printer.AccessCode;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        Assert.Equal("Workshop X1C", vm.PrinterTitle);
        Assert.Equal("X1 Carbon", vm.ModelText);
        Assert.Equal("Firmware 01.12.00.00", vm.FirmwareText);
        Assert.False(vm.NeedsModelChoice);
    }

    [Fact]
    public async Task Several_printers_found_lets_the_user_pick()
    {
        _discovery.Printers.Add(FakeDiscovery.Announced(serial: "00M999999999999", host: "192.168.1.60", name: "Other"));
        _discovery.Printers.Add(FakeDiscovery.Announced());
        var vm = Launch();
        await vm.InitializeAsync();

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Discovered.Count);
        Assert.Null(vm.SelectedDiscovered);
        Assert.Contains("Choose yours", vm.SearchStatus);

        vm.SelectedDiscovered = vm.Discovered[1];
        Assert.Equal("192.168.1.50", vm.Host);
    }

    [Fact]
    public async Task Nothing_found_points_to_manual_entry()
    {
        var vm = Launch();
        await vm.InitializeAsync();

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.False(vm.HasDiscovered);
        Assert.Contains("enter its IP address", vm.SearchStatus);
    }

    [Fact]
    public async Task Unknown_model_asks_the_user_and_remembers_the_answer()
    {
        _printer.Serial = "ZZZ000000000001";
        var vm = await SetUpOnceAsync();
        Assert.True(vm.NeedsModelChoice);
        Assert.False(vm.SaveModelCommand.CanExecute(null));

        vm.ChosenModel = "P1S";
        await vm.SaveModelCommand.ExecuteAsync(null);

        Assert.False(vm.NeedsModelChoice);
        Assert.Equal("P1S", vm.ModelText);
        var relaunched = Launch();
        await relaunched.InitializeAsync();
        Assert.Equal("P1S", relaunched.ModelText);
    }

    [Fact]
    public async Task Test_does_not_save()
    {
        var vm = Launch();
        await vm.InitializeAsync();
        vm.Host = "192.168.1.50";
        vm.AccessCode = _printer.AccessCode;

        await vm.TestCommand.ExecuteAsync(null);

        Assert.True(vm.IsSetup);
        Assert.Contains("Connection works", vm.Status);
        Assert.Null(await _profiles.GetLastAsync());
    }

    [Fact]
    public async Task Forget_returns_to_setup()
    {
        var vm = await SetUpOnceAsync();

        await vm.ForgetCommand.ExecuteAsync(null);

        Assert.True(vm.IsSetup);
        Assert.Null(await _profiles.GetLastAsync());
        Assert.Null(await _credentials.GetAsync("00M000000000001"));
    }

    [Fact]
    public void Formats_fingerprints_in_groups_of_four()
    {
        Assert.Equal("145C 6BBE 7E", ConnectionViewModel.FormatFingerprint("145C6BBE7E"));
    }
}
