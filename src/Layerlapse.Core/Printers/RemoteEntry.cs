namespace Layerlapse.Core.Printers;

/// <summary>One entry from a printer directory listing. <see cref="Modified"/> is UTC, to the minute for
/// recent entries and to the day for entries older than about six months.</summary>
public sealed record RemoteEntry(
    string Name,
    string FullPath,
    long Size,
    DateTime Modified,
    bool IsDirectory);
