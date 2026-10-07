using Layerlapse.Core.Credentials;
using Layerlapse.Core.Discovery;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Setup;

namespace Layerlapse.Core.Tests;

public sealed class PrinterSetupServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "layerlapse-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakePrinter _printer = new();
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly JsonPrinterProfileStore _profiles;
    private readonly FakeDiscovery _discovery = new();
    private readonly PrinterSetupService _service;

    public PrinterSetupServiceTests()
    {
        _profiles = new JsonPrinterProfileStore(Path.Combine(_folder, "printers.json"));
        _service = new PrinterSetupService(_credentials, _profiles, _printer.Create, _discovery);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Saves_code_by_serial_and_pins_certificate_after_login()
    {
        var result = await _service.ConnectAndSaveAsync(" 192.168.1.50 ", "12345678");

        Assert.Contains("forgotten when Layerlapse quits", result.Warning); // the in-memory store is session-only
        Assert.Equal("00M000000000001", result.Profile.Id);
        Assert.Equal("192.168.1.50", result.Profile.Host);
        Assert.Equal(_printer.Fingerprint, result.Profile.PinnedFingerprint);
        Assert.Equal("12345678", await _credentials.GetAsync("00M000000000001"));
        Assert.Equal(result.Profile, await _profiles.GetLastAsync());
    }

    [Fact]
    public async Task Saves_announced_model_name_and_firmware()
    {
        var result = await _service.ConnectAndSaveAsync("192.168.1.50", "12345678", FakeDiscovery.Announced(), null);

        Assert.Equal("X1 Carbon", result.Profile.Model);
        Assert.Equal("BL-P001", result.Profile.ModelCode);
        Assert.Equal("Workshop X1C", result.Profile.Name);
        Assert.Equal("01.12.00.00", result.Profile.Firmware);
        Assert.Equal("Workshop X1C", result.Profile.DisplayName);
    }

    [Fact]
    public async Task Manual_entry_takes_model_from_serial_prefix_or_users_choice()
    {
        Assert.Equal("X1 Carbon", (await _service.ConnectAndSaveAsync("192.168.1.50", "12345678")).Profile.Model);

        _printer.Serial = "ZZZ000000000001";
        Assert.Null((await _service.ConnectAndSaveAsync("192.168.1.50", "12345678")).Profile.Model);
        Assert.Equal("P1S", (await _service.ConnectAndSaveAsync("192.168.1.50", "12345678", null, "P1S")).Profile.Model);
    }

    [Fact]
    public async Task Ignores_announcement_details_for_a_different_printer()
    {
        var other = FakeDiscovery.Announced(serial: "00M999999999999", name: "Other");

        var result = await _service.ConnectAndSaveAsync("192.168.1.50", "12345678", other, null);

        Assert.Null(result.Profile.Name);
        Assert.Null(result.Profile.ModelCode);
    }

    [Fact]
    public async Task Reconnect_finds_a_printer_whose_address_changed()
    {
        var saved = (await _service.ConnectAndSaveAsync("192.168.1.50", "12345678")).Profile;
        _printer.UnreachableHosts.Add("192.168.1.50");
        _discovery.Printers.Add(FakeDiscovery.Announced(host: "192.168.1.77"));

        var reconnected = await _service.ReconnectAsync(saved);

        Assert.Equal("192.168.1.77", reconnected.Host);
        Assert.Equal("Workshop X1C", reconnected.Name);
        Assert.Equal("192.168.1.77", (await _profiles.GetLastAsync())!.Host);
        Assert.Equal(saved.PinnedFingerprint, _printer.Connections[^1].PinnedFingerprint);
    }

    [Fact]
    public async Task Moved_printer_must_still_present_the_pinned_certificate()
    {
        var saved = (await _service.ConnectAndSaveAsync("192.168.1.50", "12345678")).Profile;
        _printer.UnreachableHosts.Add("192.168.1.50");
        _printer.Fingerprint = new string('B', 64);
        _discovery.Printers.Add(FakeDiscovery.Announced(host: "192.168.1.77"));

        await Assert.ThrowsAsync<PrinterCertificateMismatchException>(() => _service.ReconnectAsync(saved));
        Assert.Equal("192.168.1.50", (await _profiles.GetLastAsync())!.Host);
    }

    [Fact]
    public async Task Reconnect_fills_in_a_missing_model_from_the_serial()
    {
        var saved = (await _service.ConnectAndSaveAsync("192.168.1.50", "12345678")).Profile with { Model = null };
        await _profiles.SaveAsync(saved);

        Assert.Equal("X1 Carbon", (await _service.ReconnectAsync(saved)).Model);
    }

    [Fact]
    public async Task Refresh_takes_name_and_firmware_from_the_announcement_but_keeps_the_address()
    {
        var saved = (await _service.ConnectAndSaveAsync("192.168.1.50", "12345678")).Profile;
        _discovery.Printers.Add(FakeDiscovery.Announced(host: "192.168.1.99"));

        var refreshed = await _service.RefreshDetailsAsync(saved);

        Assert.Equal("Workshop X1C", refreshed.Name);
        Assert.Equal("01.12.00.00", refreshed.Firmware);
        Assert.Equal("192.168.1.50", refreshed.Host);
        Assert.Equal(refreshed, await _profiles.GetLastAsync());
    }

    [Fact]
    public async Task Refresh_does_not_bring_back_a_forgotten_printer()
    {
        var saved = (await _service.ConnectAndSaveAsync("192.168.1.50", "12345678")).Profile;
        _discovery.Printers.Add(FakeDiscovery.Announced());
        await _service.ForgetAsync(saved);

        await _service.RefreshDetailsAsync(saved);

        Assert.Null(await _profiles.GetLastAsync());
    }

    [Fact]
    public async Task Unreachable_and_not_announced_stays_unreachable()
    {
        var saved = (await _service.ConnectAndSaveAsync("192.168.1.50", "12345678")).Profile;
        _printer.Reachable = false;

        await Assert.ThrowsAsync<PrinterUnreachableException>(() => _service.ReconnectAsync(saved));
        Assert.Equal(1, _discovery.Calls);
    }

    [Fact]
    public async Task Wrong_code_saves_nothing()
    {
        await Assert.ThrowsAsync<PrinterAuthenticationException>(() => _service.ConnectAndSaveAsync("192.168.1.50", "00000000"));

        Assert.Null(await _profiles.GetLastAsync());
        Assert.Null(await _credentials.GetAsync("00M000000000001"));
        Assert.False(File.Exists(_profiles.FilePath));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("printer.local")]
    [InlineData("")]
    public async Task Refuses_non_local_addresses_without_connecting(string host)
    {
        await Assert.ThrowsAsync<PrinterConnectionException>(() => _service.TestAsync(host, "12345678"));

        Assert.Empty(_printer.Connections);
    }

    [Fact]
    public async Task Reconnect_uses_stored_code_and_pinned_certificate()
    {
        var saved = (await _service.ConnectAndSaveAsync("192.168.1.50", "12345678")).Profile;

        var reconnected = await _service.ReconnectAsync((await _service.GetLastAsync())!);

        Assert.Equal(saved.Id, reconnected.Id);
        var last = _printer.Connections[^1];
        Assert.Equal("12345678", last.AccessCode);
        Assert.Equal(saved.PinnedFingerprint, last.PinnedFingerprint);
    }

    [Fact]
    public async Task Reconnect_refuses_a_changed_certificate_until_trusted()
    {
        var saved = (await _service.ConnectAndSaveAsync("192.168.1.50", "12345678")).Profile;
        _printer.Fingerprint = new string('B', 64);

        var error = await Assert.ThrowsAsync<PrinterCertificateMismatchException>(() => _service.ReconnectAsync(saved));
        Assert.Equal(saved.PinnedFingerprint, error.ExpectedFingerprint);
        Assert.Equal(_printer.Fingerprint, error.ActualFingerprint);
        Assert.Equal(saved.PinnedFingerprint, (await _profiles.GetLastAsync())!.PinnedFingerprint);

        var trusted = await _service.TrustNewCertificateAsync(saved);
        Assert.Equal(_printer.Fingerprint, trusted.PinnedFingerprint);
        await _service.ReconnectAsync(trusted);
    }

    [Fact]
    public async Task Saving_a_known_printer_with_a_changed_certificate_needs_trust()
    {
        await _service.ConnectAndSaveAsync("192.168.1.50", "12345678");
        _printer.Fingerprint = new string('B', 64);
        _printer.AccessCode = "87654321"; // factory reset changes both

        await Assert.ThrowsAsync<PrinterCertificateMismatchException>(() => _service.ConnectAndSaveAsync("192.168.1.50", "87654321"));
        var profile = (await _profiles.GetLastAsync())!;
        Assert.Equal(new string('A', 64), profile.PinnedFingerprint);
        Assert.Equal("12345678", await _credentials.GetAsync(profile.Id));

        var trusted = await _service.TrustNewCertificateAsync(profile, "87654321");
        Assert.Equal(new string('B', 64), trusted.PinnedFingerprint);
        Assert.Equal("87654321", await _credentials.GetAsync(profile.Id));
    }

    [Fact]
    public async Task Unexpected_io_errors_become_connection_errors()
    {
        _printer.ConnectFailure = new IOException("The printer closed the connection.");

        var error = await Assert.ThrowsAsync<PrinterConnectionException>(() => _service.TestAsync("192.168.1.50", "12345678"));
        Assert.Contains("closed the connection", error.Message);
    }

    [Fact]
    public async Task Reconnect_without_stored_code_asks_for_it()
    {
        var saved = (await _service.ConnectAndSaveAsync("192.168.1.50", "12345678")).Profile;
        await _credentials.RemoveAsync(saved.Id);

        await Assert.ThrowsAsync<AccessCodeMissingException>(() => _service.ReconnectAsync(saved));
    }

    [Fact]
    public async Task Credential_store_failure_still_connects_with_a_warning()
    {
        var service = new PrinterSetupService(new FailingCredentialStore(), _profiles, _printer.Create);

        var result = await service.ConnectAndSaveAsync("192.168.1.50", "12345678");

        Assert.NotNull(result.Warning);
        Assert.Contains("enter the access code again", result.Warning);
    }

    [Fact]
    public async Task Printer_without_serial_is_keyed_by_host()
    {
        _printer.Serial = null;

        var result = await _service.ConnectAndSaveAsync("10.0.0.7", "12345678");

        Assert.Equal("host:10.0.0.7", result.Profile.Id);
    }

    [Fact]
    public async Task Forget_removes_code_and_profile()
    {
        var saved = (await _service.ConnectAndSaveAsync("192.168.1.50", "12345678")).Profile;

        await _service.ForgetAsync(saved);

        Assert.Null(await _service.GetLastAsync());
        Assert.Null(await _credentials.GetAsync(saved.Id));
    }

    private sealed class FailingCredentialStore : ICredentialStore
    {
        public string Name => "broken store";

        public bool IsPersistent => true;

        public Task<string?> GetAsync(string printerId, CancellationToken cancellationToken = default) =>
            throw new CredentialStoreException("Could not read the broken store.");

        public Task SaveAsync(string printerId, string accessCode, CancellationToken cancellationToken = default) =>
            throw new CredentialStoreException("Could not save to the broken store.");

        public Task RemoveAsync(string printerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
