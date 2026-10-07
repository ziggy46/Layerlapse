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
    private ConnectionViewModel Launch() => new(new PrinterSetupService(_credentials, _profiles, _printer.Create));

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
        Assert.Equal("00M000000000001", vm.PrinterTitle);
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
