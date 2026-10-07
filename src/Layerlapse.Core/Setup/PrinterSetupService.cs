using Layerlapse.Core.Credentials;
using Layerlapse.Core.Printers;

namespace Layerlapse.Core.Setup;

/// <summary>
/// Testing, saving and reconnecting to a printer. Read-only towards the printer: a test logs in and
/// lists the root folder, nothing more. The certificate is pinned only after a successful login.
/// </summary>
public sealed class PrinterSetupService(
    ICredentialStore credentials,
    IPrinterProfileStore profiles,
    Func<PrinterConnection, IPrinterClient> clientFactory,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public ICredentialStore Credentials => credentials;

    /// <summary>Connects, logs in and lists "/" to prove the encrypted data channel works.</summary>
    /// <exception cref="PrinterConnectionException">With a message for the user.</exception>
    public async Task<PrinterTestResult> TestAsync(string host, string accessCode, string? pinnedFingerprint = null, CancellationToken cancellationToken = default)
    {
        host = host.Trim();
        accessCode = accessCode.Trim();
        if (PrinterAddress.Validate(host) is { } addressError)
        {
            throw new PrinterConnectionException(addressError);
        }

        if (accessCode.Length == 0)
        {
            throw new PrinterConnectionException("Enter the access code from the printer screen (Settings, then LAN Only Mode).");
        }

        await using var client = clientFactory(new PrinterConnection(host, accessCode, pinnedFingerprint));
        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch (IOException e) when (e is not PrinterConnectionException)
        {
            throw new PrinterConnectionException("The connection to the printer failed: " + e.Message, e);
        }

        try
        {
            await client.ListAsync("/", cancellationToken);
        }
        catch (Exception e) when (e is IOException and not PrinterConnectionException)
        {
            throw new PrinterConnectionException("Logged in, but the printer refused to list its files. " + e.Message, e);
        }

        return new PrinterTestResult(host, client.Serial, client.CertificateFingerprint!);
    }

    /// <summary>
    /// Tests the connection and, if it works, saves the code to the credential store and pins the certificate.
    /// </summary>
    /// <exception cref="PrinterCertificateMismatchException">
    /// This printer was saved before with a different certificate. Nothing is saved; the user must confirm
    /// with <see cref="TrustNewCertificateAsync"/>.
    /// </exception>
    public async Task<PrinterSaveResult> ConnectAndSaveAsync(string host, string accessCode, CancellationToken cancellationToken = default)
    {
        var test = await TestAsync(host, accessCode, null, cancellationToken);
        var id = PrinterProfile.IdFor(test.Serial, test.Host);
        if (await profiles.GetAsync(id, cancellationToken) is { } existing
            && !string.Equals(existing.PinnedFingerprint, test.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new PrinterCertificateMismatchException(existing.PinnedFingerprint, test.Fingerprint);
        }

        var profile = new PrinterProfile(id, test.Serial, test.Host, test.Fingerprint, _time.GetUtcNow());
        var warning = await SaveCodeAsync(profile.Id, accessCode.Trim(), cancellationToken);
        await profiles.SaveAsync(profile, cancellationToken);
        return new PrinterSaveResult(profile, warning);
    }

    /// <summary>The last used printer, or null when none is saved.</summary>
    public Task<PrinterProfile?> GetLastAsync(CancellationToken cancellationToken = default) => profiles.GetLastAsync(cancellationToken);

    /// <summary>Reconnects to the saved printer using the stored code and pinned certificate.</summary>
    /// <exception cref="PrinterConnectionException">With a message for the user.</exception>
    /// <exception cref="AccessCodeMissingException">The store has no code for this printer.</exception>
    public async Task<PrinterProfile> ReconnectAsync(PrinterProfile profile, CancellationToken cancellationToken = default)
    {
        string? code;
        try
        {
            code = await credentials.GetAsync(profile.Id, cancellationToken);
        }
        catch (CredentialStoreException e)
        {
            throw new AccessCodeMissingException(e.Message, e);
        }

        if (string.IsNullOrEmpty(code))
        {
            throw new AccessCodeMissingException(
                credentials.IsPersistent
                    ? $"No access code is saved for this printer in {credentials.Name}. Enter it again."
                    : "Access codes cannot be remembered on this system, so enter it again.");
        }

        var test = await TestAsync(profile.Host, code, profile.PinnedFingerprint, cancellationToken);
        var updated = profile with { LastConnected = _time.GetUtcNow(), Serial = test.Serial ?? profile.Serial };
        await profiles.SaveAsync(updated, cancellationToken);
        return updated;
    }

    /// <summary>
    /// After the user has confirmed a certificate change: log in without the old pin and pin the new certificate.
    /// Uses <paramref name="newAccessCode"/> when given (after a factory reset the code changes too) and saves it.
    /// </summary>
    public async Task<PrinterProfile> TrustNewCertificateAsync(PrinterProfile profile, string? newAccessCode = null, CancellationToken cancellationToken = default)
    {
        var host = profile.Host;
        var code = newAccessCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            code = await credentials.GetAsync(profile.Id, cancellationToken)
                ?? throw new AccessCodeMissingException("Enter the access code again to trust the new certificate.");
        }

        var test = await TestAsync(host, code, null, cancellationToken);
        if (!string.IsNullOrEmpty(newAccessCode))
        {
            await SaveCodeAsync(profile.Id, code, cancellationToken);
        }

        var updated = profile with { Host = test.Host, PinnedFingerprint = test.Fingerprint, Serial = test.Serial ?? profile.Serial, LastConnected = _time.GetUtcNow() };
        await profiles.SaveAsync(updated, cancellationToken);
        return updated;
    }

    /// <summary>Removes the saved printer and its access code.</summary>
    public async Task ForgetAsync(PrinterProfile profile, CancellationToken cancellationToken = default)
    {
        try
        {
            await credentials.RemoveAsync(profile.Id, cancellationToken);
        }
        finally
        {
            await profiles.RemoveAsync(profile.Id, cancellationToken);
        }
    }

    private async Task<string?> SaveCodeAsync(string id, string accessCode, CancellationToken cancellationToken)
    {
        try
        {
            await credentials.SaveAsync(id, accessCode, cancellationToken);
            return credentials.IsPersistent
                ? null
                : "No secure password store was found, so the access code will be forgotten when Layerlapse quits.";
        }
        catch (CredentialStoreException e)
        {
            return e.Message + " You will need to enter the access code again next time.";
        }
    }
}

public sealed record PrinterTestResult(string Host, string? Serial, string Fingerprint);

public sealed record PrinterSaveResult(PrinterProfile Profile, string? Warning);

/// <summary>The saved printer has no access code in the credential store.</summary>
public sealed class AccessCodeMissingException(string message, Exception? innerException = null)
    : Exception(message, innerException);
