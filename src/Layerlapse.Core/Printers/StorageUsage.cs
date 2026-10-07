namespace Layerlapse.Core.Printers;

/// <summary>
/// How much space each kind of file takes on the printer's storage. The printer's FTP server cannot report
/// free space or capacity, so this is space used only.
/// </summary>
public sealed record StorageUsage(
    StorageCategory Timelapses,
    StorageCategory Thumbnails,
    StorageCategory CameraRecordings,
    StorageCategory Models,
    StorageCategory Other)
{
    public long TotalBytes => Timelapses.Bytes + Thumbnails.Bytes + CameraRecordings.Bytes + Models.Bytes + Other.Bytes;

    /// <summary>Lists /timelapse/, /timelapse/thumbnail/, /ipcam/ and the root folder. Read-only.</summary>
    public static async Task<StorageUsage> MeasureAsync(PrinterSession session, CancellationToken cancellationToken = default) =>
        await session.RunAsync(async client =>
        {
            async Task<IReadOnlyList<RemoteEntry>> Files(string folder)
            {
                try
                {
                    return (await client.ListAsync(folder, cancellationToken)).Where(e => !e.IsDirectory).ToList();
                }
                catch (FtpReplyException)
                {
                    return []; // folder missing on this printer
                }
            }

            var root = await Files("/");
            var models = root.Where(e => e.Name.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase)).ToList();
            return new StorageUsage(
                StorageCategory.Of(await Files("/timelapse/")),
                StorageCategory.Of(await Files("/timelapse/thumbnail/")),
                StorageCategory.Of(await Files("/ipcam/")),
                StorageCategory.Of(models),
                StorageCategory.Of(root.Except(models).ToList()));
        }, cancellationToken);
}

public sealed record StorageCategory(int Files, long Bytes)
{
    public static StorageCategory Of(IReadOnlyCollection<RemoteEntry> entries) => new(entries.Count, entries.Sum(e => e.Size));
}
