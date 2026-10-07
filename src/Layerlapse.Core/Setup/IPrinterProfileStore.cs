namespace Layerlapse.Core.Setup;

/// <summary>Saved printers and which one to reconnect to at launch.</summary>
public interface IPrinterProfileStore
{
    Task<PrinterProfile?> GetLastAsync(CancellationToken cancellationToken = default);

    Task<PrinterProfile?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Every saved printer, in the order they were added.</summary>
    Task<IReadOnlyList<PrinterProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Makes a saved printer the one to reconnect to at launch.</summary>
    Task SetLastAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces the profile with the same id and makes it the last used.</summary>
    Task SaveAsync(PrinterProfile profile, CancellationToken cancellationToken = default);

    Task RemoveAsync(string id, CancellationToken cancellationToken = default);
}
