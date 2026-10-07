using System.Diagnostics;

namespace Layerlapse.Core.Timelapses;

/// <summary>Plays a downloaded video file.</summary>
public interface IVideoPlayer
{
    /// <exception cref="InvalidOperationException">No player could be started.</exception>
    void Play(string localPath);
}

/// <summary>Opens the file in the operating system's default video player (the plan's fallback player).</summary>
public sealed class DefaultAppVideoPlayer(Func<ProcessStartInfo, Process?>? start = null) : IVideoPlayer
{
    private readonly Func<ProcessStartInfo, Process?> _start = start ?? Process.Start;

    public void Play(string localPath)
    {
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException("The video is not on this computer.", localPath);
        }

        var info = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open") { ArgumentList = { localPath } }
            : OperatingSystem.IsLinux() ? new ProcessStartInfo("xdg-open") { ArgumentList = { localPath } }
            : new ProcessStartInfo(localPath) { UseShellExecute = true };
        try
        {
            // With the Windows shell, Process.Start returns null when the file was handed to an app that is
            // already running or is a packaged app (such as Media Player). The video still opens, so a missing
            // process is not an error; only a failure to start anything is.
            using var _ = _start(info);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException("No app is set up to play MP4 videos on this computer.", e);
        }
    }
}
