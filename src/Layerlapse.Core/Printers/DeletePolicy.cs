using System.Text.RegularExpressions;

namespace Layerlapse.Core.Printers;

/// <summary>
/// The only files Layerlapse may ever delete on a printer: a timelapse video in /timelapse/ and its thumbnail.
/// Never models in the root folder, never certificate/ or verify_job, never anything else. Checked by the
/// printer client itself and again by the library, so no caller can get around it.
/// </summary>
public static partial class DeletePolicy
{
    [GeneratedRegex(@"^/timelapse/video_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.mp4\z")]
    private static partial Regex Video();

    [GeneratedRegex(@"^/timelapse/thumbnail/video_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.jpg\z")]
    private static partial Regex Thumbnail();

    public static bool IsAllowed(string remotePath) => Video().IsMatch(remotePath) || Thumbnail().IsMatch(remotePath);

    /// <exception cref="UnauthorizedAccessException">The path is not a timelapse or its thumbnail.</exception>
    public static void Check(string remotePath)
    {
        if (!IsAllowed(remotePath))
        {
            throw new UnauthorizedAccessException($"Layerlapse never deletes '{remotePath}'. Only timelapse videos and their thumbnails can be deleted.");
        }
    }
}
