namespace Layerlapse.Core.Timelapses;

/// <summary>
/// Which timelapses count as new for auto-download: only those finished after the moment the user turned the
/// feature on (so enabling it never fetches the whole history), and only from a complete listing.
/// </summary>
public static class AutoDownload
{
    public static IReadOnlyList<Timelapse> SelectNew(IEnumerable<Timelapse> timelapses, DateTime sinceUtc) =>
        timelapses.Where(t => t.ModifiedUtc > sinceUtc).OrderBy(t => t.ModifiedUtc).ToList();
}
