using System.Text.Json;
using System.Text.RegularExpressions;

namespace Layerlapse.Core.Timelapses;

/// <summary>
/// On-disk cache for one printer: the last listing, thumbnails and downloaded videos. Videos are capped in
/// total size and evicted least recently used first. Listings and thumbnails are small and never evicted.
/// </summary>
public sealed partial class TimelapseCache
{
    public const long DefaultVideoCap = 2L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public TimelapseCache(string root, long videoCapBytes = DefaultVideoCap)
    {
        Root = root;
        VideoCapBytes = videoCapBytes;
        Directory.CreateDirectory(ThumbnailFolder);
        Directory.CreateDirectory(VideoFolder);
        foreach (var partial in Directory.EnumerateFiles(Root, "*.part", SearchOption.AllDirectories))
        {
            TryDelete(partial); // left over from an interrupted download
        }
    }

    public string Root { get; }

    public long VideoCapBytes { get; }

    private string ThumbnailFolder => Path.Combine(Root, "thumbnails");

    private string VideoFolder => Path.Combine(Root, "videos");

    private string ListingFile => Path.Combine(Root, "listing.json");

    /// <summary>Layerlapse/cache/&lt;printer id&gt; under the user's local app data folder.</summary>
    public static TimelapseCache ForPrinter(string printerId) =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "Layerlapse", "cache", SafeFolderName(printerId)));

    public async Task<CachedListing?> LoadListingAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ListingFile))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(ListingFile);
            return await JsonSerializer.DeserializeAsync<CachedListing>(stream, Options, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveListingAsync(CachedListing listing, CancellationToken cancellationToken = default)
    {
        var temp = ListingFile + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, listing, Options, cancellationToken);
        }

        File.Move(temp, ListingFile, overwrite: true);
    }

    public string ThumbnailPath(Timelapse timelapse) => Path.Combine(ThumbnailFolder, Checked(TimelapseNames.ThumbnailName(timelapse.Name)));

    public string VideoPath(Timelapse timelapse) => Path.Combine(VideoFolder, Checked(timelapse.Name));

    /// <summary>A cached video is usable only when it is complete.</summary>
    public bool HasVideo(Timelapse timelapse) =>
        new FileInfo(VideoPath(timelapse)) is { Exists: true } file && file.Length == timelapse.Size;

    /// <summary>Marks a video as just used, so eviction keeps it.</summary>
    public void Touch(string path)
    {
        if (File.Exists(path))
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
    }

    /// <summary>Deletes least recently used videos until the total fits the cap, never touching <paramref name="keep"/>.</summary>
    public void EvictVideos(string? keep = null)
    {
        var videos = new DirectoryInfo(VideoFolder).GetFiles("*.mp4")
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();
        var total = videos.Sum(f => f.Length);
        foreach (var video in videos)
        {
            if (total <= VideoCapBytes)
            {
                break;
            }

            if (keep is not null && string.Equals(video.FullName, Path.GetFullPath(keep), StringComparison.Ordinal))
            {
                continue;
            }

            total -= video.Length;
            TryDelete(video.FullName);
        }
    }

    public static string SafeFolderName(string id) => UnsafeCharacters().Replace(id, "_");

    private static string Checked(string name) =>
        name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal) || name.Any(char.IsControl)
            ? throw new ArgumentException($"Refusing unsafe file name '{name}'.", nameof(name))
            : name;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeCharacters();
}

/// <summary>What the cache remembers about the timelapse folder.</summary>
public sealed record CachedListing(
    DateTime UpdatedUtc,
    int ClockOffsetMinutes,
    ClockOffsetSource ClockOffsetSource,
    IReadOnlyList<CachedTimelapse> Timelapses);

public sealed record CachedTimelapse(string Name, long Size, DateTime ModifiedUtc);
