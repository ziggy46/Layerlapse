using System.Globalization;
using System.Text.RegularExpressions;

namespace Layerlapse.Core.Timelapses;

/// <summary>
/// The only names the app trusts: timelapses are "video_YYYY-MM-DD_HH-MM-SS.mp4" (plus a JPEG of the same
/// base name in thumbnail/), camera recordings "ipcam-record.YYYY-MM-DD_HH-MM-SS.N.mp4". Anything else is
/// ignored, which also keeps path separators and ".." out of cache paths.
/// </summary>
public static partial class TimelapseNames
{
    public const string Folder = "/timelapse/";
    public const string ThumbnailFolder = "/timelapse/thumbnail/";

    [GeneratedRegex(@"^video_(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\.mp4$")]
    private static partial Regex VideoRegex();

    [GeneratedRegex(@"^ipcam-record\.(\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})\.\d+\.mp4$")]
    private static partial Regex CameraRegex();

    /// <summary>The start time in the filename, on the printer's clock (not UTC).</summary>
    public static bool TryParseVideo(string name, out DateTime start) => TryParse(VideoRegex(), name, out start);

    public static bool TryParseCameraRecording(string name, out DateTime start) => TryParse(CameraRegex(), name, out start);

    public static string ThumbnailName(string videoName) => Path.ChangeExtension(videoName, ".jpg");

    private static bool TryParse(Regex regex, string name, out DateTime start)
    {
        start = default;
        var match = regex.Match(name);
        return match.Success && DateTime.TryParseExact(
            match.Groups[1].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out start);
    }
}
