namespace Layerlapse.Core.Printers;

/// <summary>One entry from a printer directory listing.</summary>
public sealed record RemoteEntry(
    string Name,
    string FullPath,
    long Size,
    DateTime Modified,
    bool IsDirectory);
