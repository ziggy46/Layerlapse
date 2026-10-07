using Layerlapse.Core.Printers;

namespace Layerlapse.Core.Timelapses;

public enum ClockOffsetSource
{
    /// <summary>Measured from camera recordings: name (printer clock) versus modification time (UTC).</summary>
    CameraRecordings,

    /// <summary>No recordings to measure; assumed to match this computer's time zone.</summary>
    ComputerTimeZone,
}

/// <summary>
/// Filenames use the printer's local clock, listings use UTC (see docs/FINDINGS.md). This estimates how far
/// the printer's clock is behind UTC so a start time can be compared with a modification time.
/// </summary>
public sealed record PrinterClockOffset(TimeSpan PrinterToUtc, ClockOffsetSource Source)
{
    /// <summary>
    /// Camera recordings are short segments (about 5 minutes) whose name is their start and whose modification
    /// time is their end, so <c>mtime - name</c> is the offset plus at most a few minutes. Real time zones are
    /// multiples of 15 minutes, so the smallest difference rounded down to 15 minutes is the offset.
    /// </summary>
    public static PrinterClockOffset Estimate(IEnumerable<RemoteEntry> cameraRecordings, DateTime someLocalTime)
    {
        var differences = cameraRecordings
            .Select(e => TimelapseNames.TryParseCameraRecording(e.Name, out var start) && e.Modified.TimeOfDay != TimeSpan.Zero
                ? e.Modified - DateTime.SpecifyKind(start, DateTimeKind.Utc)
                : (TimeSpan?)null)
            .OfType<TimeSpan>()
            .Where(d => d > TimeSpan.FromHours(-15) && d < TimeSpan.FromHours(15))
            .ToList();

        if (differences.Count > 0)
        {
            var minutes = Math.Floor(differences.Min().TotalMinutes / 15) * 15;
            return new PrinterClockOffset(TimeSpan.FromMinutes(minutes), ClockOffsetSource.CameraRecordings);
        }

        return new PrinterClockOffset(-TimeZoneInfo.Local.GetUtcOffset(someLocalTime), ClockOffsetSource.ComputerTimeZone);
    }

    public DateTime ToUtc(DateTime printerTime) => DateTime.SpecifyKind(printerTime + PrinterToUtc, DateTimeKind.Utc);
}
