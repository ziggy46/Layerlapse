using System.Diagnostics;

namespace Layerlapse.Core.Timelapses;

/// <summary>Plays a downloaded video file.</summary>
public interface IVideoPlayer
{
    /// <exception cref="InvalidOperationException">No player could be started.</exception>
    void Play(string localPath);
}

/// <summary>Opens the file in the operating system's default video player (the plan's fallback player).</summary>
public sealed class DefaultAppVideoPlayer : IVideoPlayer
{
    public void Play(string localPath)
    {
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException("The video is not on this computer.", localPath);
        }

        var start = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open") { ArgumentList = { localPath } }
            : OperatingSystem.IsLinux() ? new ProcessStartInfo("xdg-open") { ArgumentList = { localPath } }
            : new ProcessStartInfo(localPath) { UseShellExecute = true };
        try
        {
            using var _ = Process.Start(start) ?? throw new InvalidOperationException("No video player started.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException("No app is set up to play MP4 videos on this computer.", e);
        }
    }
}
