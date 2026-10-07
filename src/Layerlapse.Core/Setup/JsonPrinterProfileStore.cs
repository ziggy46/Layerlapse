using System.Text.Json;
using System.Text.Json.Serialization;

namespace Layerlapse.Core.Setup;

/// <summary>
/// Stores profiles in printers.json in the app's data folder. Never holds access codes.
/// </summary>
public sealed class JsonPrinterProfileStore(string filePath) : IPrinterProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public string FilePath { get; } = filePath;

    /// <summary>The default location: Layerlapse/printers.json under the user's local app data folder.</summary>
    public static JsonPrinterProfileStore CreateDefault() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "Layerlapse",
            "printers.json"));

    public async Task<PrinterProfile?> GetLastAsync(CancellationToken cancellationToken = default)
    {
        var file = await ReadAsync(cancellationToken);
        return file.Printers.FirstOrDefault(p => p.Id == file.LastPrinterId);
    }

    public async Task<PrinterProfile?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        (await ReadAsync(cancellationToken)).Printers.FirstOrDefault(p => p.Id == id);

    public async Task SaveAsync(PrinterProfile profile, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var file = await ReadAsync(cancellationToken);
            var printers = file.Printers.Where(p => p.Id != profile.Id).Append(profile).ToList();
            await WriteAsync(new ProfileFile(profile.Id, printers), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var file = await ReadAsync(cancellationToken);
            var printers = file.Printers.Where(p => p.Id != id).ToList();
            var last = file.LastPrinterId == id ? printers.LastOrDefault()?.Id : file.LastPrinterId;
            await WriteAsync(new ProfileFile(last, printers), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ProfileFile> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return ProfileFile.Empty;
        }

        try
        {
            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<ProfileFile>(stream, Options, cancellationToken) ?? ProfileFile.Empty;
        }
        catch (JsonException)
        {
            // A corrupt file only costs the saved printer; the user can set it up again.
            return ProfileFile.Empty;
        }
    }

    private async Task WriteAsync(ProfileFile file, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, file, Options, cancellationToken);
        }

        File.Move(temp, FilePath, overwrite: true);
    }

    private sealed record ProfileFile(string? LastPrinterId, IReadOnlyList<PrinterProfile> Printers)
    {
        public static ProfileFile Empty { get; } = new(null, []);
    }
}
