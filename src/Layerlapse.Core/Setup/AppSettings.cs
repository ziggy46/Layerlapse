using System.Text.Json;

namespace Layerlapse.Core.Setup;

/// <summary>Small, non-secret preferences.</summary>
/// <param name="AllowDelete">Deleting timelapses from the printer is off unless the user turns it on.</param>
/// <param name="AutoDownloadFolder">Where new timelapses are saved automatically; null when auto-download is off.</param>
/// <param name="AutoDownloadSince">Only timelapses finished after this (UTC) are auto-downloaded, so turning the
/// feature on does not fetch every old timelapse.</param>
public sealed record AppSettings(
    string? LastDownloadFolder = null,
    bool AllowDelete = false,
    string? AutoDownloadFolder = null,
    DateTime? AutoDownloadSince = null)
{
    public bool AutoDownloadEnabled => AutoDownloadFolder is not null && AutoDownloadSince is not null;
}

/// <summary>Reads and writes settings.json next to printers.json. Never holds access codes.</summary>
public sealed class JsonSettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IgnoreReadOnlyProperties = true,
    };

    public static JsonSettingsStore CreateDefault() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "Layerlapse", "settings.json"));

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken) ?? new AppSettings();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var temp = filePath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
        }

        File.Move(temp, filePath, overwrite: true);
    }
}
