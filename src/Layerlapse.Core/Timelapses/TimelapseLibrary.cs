using Layerlapse.Core.Printers;

namespace Layerlapse.Core.Timelapses;

/// <summary>
/// The timelapse folder of one printer: cached listing for instant display, background refresh, thumbnails
/// and videos downloaded to the cache. Read-only towards the printer.
/// </summary>
public sealed class TimelapseLibrary(PrinterSession session, TimelapseCache cache, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public TimelapseCache Cache => cache;

    /// <summary>The last listing, straight from disk, newest first. Empty when there is none.</summary>
    public Task<TimelapseListing> LoadCachedAsync(CancellationToken cancellationToken = default) => LoadCachedAsync(cache, cancellationToken);

    /// <inheritdoc cref="LoadCachedAsync(CancellationToken)"/>
    public static async Task<TimelapseListing> LoadCachedAsync(TimelapseCache cache, CancellationToken cancellationToken = default) =>
        await cache.LoadListingAsync(cancellationToken) is { } cached ? FromCache(cached) : TimelapseListing.Empty;

    /// <summary>
    /// Lists the folder, asks for exact modification times of new files only, measures the printer's clock
    /// offset from its camera recordings, and saves the result to the cache.
    /// </summary>
    public async Task<TimelapseListing> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var previous = (await cache.LoadListingAsync(cancellationToken))?.Timelapses
            .ToDictionary(t => (t.Name, t.Size)) ?? [];

        var (entries, cameraRecordings, exactTimes) = await session.RunAsync(async client =>
        {
            var list = await client.ListAsync(TimelapseNames.Folder, cancellationToken);
            var videos = list.Where(e => !e.IsDirectory && TimelapseNames.TryParseVideo(e.Name, out _)).ToList();
            IReadOnlyList<RemoteEntry> recordings;
            try
            {
                recordings = await client.ListAsync("/ipcam/", cancellationToken);
            }
            catch (FtpReplyException)
            {
                recordings = []; // no camera folder on this printer
            }

            var times = new Dictionary<string, DateTime>();
            foreach (var video in videos.Where(v => !previous.ContainsKey((v.Name, v.Size))))
            {
                if (await client.GetModifiedTimeAsync(video.FullPath, cancellationToken) is { } exact)
                {
                    times[video.Name] = exact;
                }
            }

            return (videos, recordings, times);
        }, cancellationToken);

        var clock = PrinterClockOffset.Estimate(cameraRecordings, DateTime.Now);
        var cached = new CachedListing(
            _time.GetUtcNow().UtcDateTime,
            (int)clock.PrinterToUtc.TotalMinutes,
            clock.Source,
            entries.Select(e => new CachedTimelapse(
                    e.Name,
                    e.Size,
                    previous.TryGetValue((e.Name, e.Size), out var known) ? known.ModifiedUtc
                        : exactTimes.TryGetValue(e.Name, out var exact) ? exact
                        : e.Modified))
                .ToList());
        await cache.SaveListingAsync(cached, cancellationToken);
        return FromCache(cached);
    }

    /// <summary>
    /// Deletes one timelapse and its thumbnail from the printer, then drops it from the cached listing.
    /// The cached copy of the video (if it was played) is kept: it may now be the only copy.
    /// </summary>
    /// <param name="deletingEnabled">The user's "allow deleting" setting. When false, nothing is sent.</param>
    /// <exception cref="UnauthorizedAccessException">Deleting is turned off, or the file is not a timelapse.</exception>
    public async Task DeleteAsync(Timelapse timelapse, bool deletingEnabled, CancellationToken cancellationToken = default)
    {
        if (!deletingEnabled)
        {
            throw new UnauthorizedAccessException("Deleting from the printer is turned off in Settings.");
        }

        DeletePolicy.Check(timelapse.RemotePath);
        DeletePolicy.Check(timelapse.ThumbnailRemotePath);
        await session.RunAsync(async client =>
        {
            await client.DeleteAsync(timelapse.RemotePath, cancellationToken);
            try
            {
                await client.DeleteAsync(timelapse.ThumbnailRemotePath, cancellationToken);
            }
            catch (FtpReplyException e) when (e.ReplyCode == 550)
            {
                // No thumbnail: the video is gone, which is what matters.
            }
        }, cancellationToken);

        if (await cache.LoadListingAsync(cancellationToken) is { } listing)
        {
            await cache.SaveListingAsync(listing with { Timelapses = listing.Timelapses.Where(t => t.Name != timelapse.Name).ToList() }, cancellationToken);
        }
    }

    /// <summary>Local path of the thumbnail, downloading it if needed. Null when the printer has none.</summary>
    public async Task<string?> GetThumbnailAsync(Timelapse timelapse, CancellationToken cancellationToken = default)
    {
        var path = cache.ThumbnailPath(timelapse);
        if (File.Exists(path))
        {
            return path;
        }

        try
        {
            await DownloadAsync(timelapse.ThumbnailRemotePath, path, null, null, cancellationToken);
            return path;
        }
        catch (FtpReplyException)
        {
            return null; // missing thumbnail: show a placeholder
        }
    }

    /// <summary>Local path of the complete video, downloading it to the cache first if needed.</summary>
    public async Task<string> GetVideoAsync(Timelapse timelapse, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var path = cache.VideoPath(timelapse);
        if (!cache.HasVideo(timelapse))
        {
            await DownloadAsync(timelapse.RemotePath, path, timelapse.Size, progress, cancellationToken);
        }

        cache.Touch(path);
        cache.EvictVideos(keep: path);
        return path;
    }

    private async Task DownloadAsync(string remotePath, string localPath, long? expectedSize, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var partial = localPath + ".part";
        try
        {
            await session.RunAsync(client => client.DownloadAsync(remotePath, partial, progress, cancellationToken), cancellationToken);
            var length = new FileInfo(partial).Length;
            if (expectedSize is { } size && length != size)
            {
                throw new IOException($"The download stopped early ({length:N0} of {size:N0} bytes). Try again.");
            }

            File.Move(partial, localPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    private static TimelapseListing FromCache(CachedListing cached)
    {
        var clock = new PrinterClockOffset(TimeSpan.FromMinutes(cached.ClockOffsetMinutes), cached.ClockOffsetSource);
        var items = cached.Timelapses
            .Select(t => TimelapseNames.TryParseVideo(t.Name, out var start)
                ? new Timelapse(t.Name, t.Size, start, t.ModifiedUtc, Timelapse.Duration(start, t.ModifiedUtc, clock))
                : null)
            .OfType<Timelapse>()
            .OrderByDescending(t => t.Start)
            .ToList();
        return new TimelapseListing(items, cached.UpdatedUtc, clock);
    }
}

public sealed record TimelapseListing(IReadOnlyList<Timelapse> Timelapses, DateTime? UpdatedUtc, PrinterClockOffset? Clock)
{
    public static TimelapseListing Empty { get; } = new([], null, null);
}
