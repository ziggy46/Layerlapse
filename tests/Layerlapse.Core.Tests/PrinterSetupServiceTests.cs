using Layerlapse.Core.Credentials;
using Layerlapse.Core.Printers;
using Layerlapse.Core.Setup;

namespace Layerlapse.Core.Tests;

public sealed class PrinterSetupServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "layerlapse-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakePrinter _printer = new();
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly JsonPrinterProfileStore _profiles;
    private readonly PrinterSetupService _service;

    public PrinterSetupServiceTests()
    {
        _profiles = new JsonPrinterProfileStore(Path.Combine(_folder, "printers.json"));
        _service = new PrinterSetupService(_credentials, _profiles, _printer.Create);
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
