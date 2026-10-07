using System.Text.Json;
using Layerlapse.Core.Printers;

namespace Layerlapse.Core.Models;

/// <summary>
/// The 3MF projects in the printer's root folder: cached listing, previews read from inside each archive
/// (only the zip directory and the preview are transferred), and slicing details. Read-only.
/// </summary>
public sealed class ModelLibrary(PrinterSession session, string cacheRoot, TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IgnoreReadOnlyProperties = true,
    };
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private string ListingFile => Path.Combine(cacheRoot, "models.json");

    private string PreviewFolder => Path.Combine(cacheRoot, "model-previews");

    public static async Task<IReadOnlyList<ModelFile>> LoadCachedAsync(string cacheRoot, CancellationToken cancellationToken = default)
    {
        var file = Path.Combine(cacheRoot, "models.json");
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(file);
            return (await JsonSerializer.DeserializeAsync<CachedModels>(stream, Options, cancellationToken))?.Models ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Lists the root folder, newest first, asking exact times only for new files.</summary>
    public async Task<IReadOnlyList<ModelFile>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var known = (await LoadCachedAsync(cacheRoot, cancellationToken)).ToDictionary(m => (m.Name, m.Size));
        var models = await session.RunAsync(async client =>
        {
            var list = await client.ListAsync("/", cancellationToken);
            var result = new List<ModelFile>();
            foreach (var entry in list.Where(e => !e.IsDirectory && ModelFile.IsModel(e.Name)))
            {
                if (known.TryGetValue((entry.Name, entry.Size), out var cached))
                {
                    result.Add(cached);
                    continue;
                }

                var exact = await client.GetModifiedTimeAsync(entry.FullPath, cancellationToken);
                result.Add(new ModelFile(entry.Name, entry.Size, exact ?? entry.Modified));
            }

            return result;
        }, cancellationToken);

        var sorted = models.OrderByDescending(m => m.ModifiedUtc).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
        Directory.CreateDirectory(cacheRoot);
        var temp = ListingFile + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, new CachedModels(_time.GetUtcNow().UtcDateTime, sorted), Options, cancellationToken);
        }

        File.Move(temp, ListingFile, overwrite: true);
        return sorted;
    }

    /// <summary>Cached preview path and details, if this model was read before.</summary>
    public static (string? PreviewPath, ModelInfo? Info) LoadCachedPreview(string cacheRoot, ModelFile model)
    {
        var folder = Path.Combine(cacheRoot, "model-previews");
        var png = Path.Combine(folder, model.CacheKey + ".png");
        var json = Path.Combine(folder, model.CacheKey + ".json");
        if (!File.Exists(json))
        {
            return (null, null);
        }

        try
        {
            var info = JsonSerializer.Deserialize<ModelInfo>(File.ReadAllText(json), Options);
            return (File.Exists(png) ? png : null, info);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Reads the preview and details from inside the archive on the printer (a few small ranged reads) and
    /// caches them. Returns the PNG path, or null when the archive has no preview.
    /// </summary>
    public async Task<(string? PreviewPath, ModelInfo Info)> GetPreviewAsync(ModelFile model, CancellationToken cancellationToken = default)
    {
        var (cachedPath, cachedInfo) = LoadCachedPreview(cacheRoot, model);
        if (cachedInfo is not null)
        {
            return (cachedPath, cachedInfo);
        }

        var (preview, info) = await session.RunAsync(client => Task.Run(() =>
        {
            using var remote = new RemoteFileStream(
                (offset, length) => client.ReadRangeAsync(model.RemotePath, offset, length, cancellationToken).GetAwaiter().GetResult(),
                model.Size);
            try
            {
                return ModelArchive.Read(remote);
            }
            catch (InvalidDataException)
            {
                return ((byte[]?)null, ModelInfo.Empty); // not a readable zip
            }
        }, cancellationToken), cancellationToken);

        Directory.CreateDirectory(PreviewFolder);
        string? path = null;
        if (preview is not null)
        {
            path = Path.Combine(PreviewFolder, model.CacheKey + ".png");
            await File.WriteAllBytesAsync(path, preview, cancellationToken);
        }

        await File.WriteAllTextAsync(Path.Combine(PreviewFolder, model.CacheKey + ".json"), JsonSerializer.Serialize(info, Options), cancellationToken);
        return (path, info);
    }

    private sealed record CachedModels(DateTime UpdatedUtc, IReadOnlyList<ModelFile> Models);
}
