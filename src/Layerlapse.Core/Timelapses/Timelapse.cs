namespace Layerlapse.Core.Timelapses;

/// <summary>One timelapse video on the printer.</summary>
/// <param name="Start">Print start from the filename, on the printer's clock. Shown as is.</param>
/// <param name="ModifiedUtc">Exact modification time (MDTM), which is about when the print ended.</param>
/// <param name="ApproximateDuration">Modification time minus start, after correcting for the printer's clock;
/// null when the result is implausible (the printer's clock was different when it was recorded).</param>
public sealed record Timelapse(
    string Name,
    long Size,
    DateTime Start,
    DateTime ModifiedUtc,
    TimeSpan? ApproximateDuration)
{
    public string RemotePath => TimelapseNames.Folder + Name;

    public string ThumbnailRemotePath => TimelapseNames.ThumbnailFolder + TimelapseNames.ThumbnailName(Name);

    public static readonly TimeSpan LongestPlausiblePrint = TimeSpan.FromHours(48);

    public static TimeSpan? Duration(DateTime start, DateTime modifiedUtc, PrinterClockOffset clock)
    {
        var duration = modifiedUtc - clock.ToUtc(start);
        return duration > TimeSpan.Zero && duration < LongestPlausiblePrint ? duration : null;
    }
}
