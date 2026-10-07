using Avalonia.Controls;
using Layerlapse.Core.Timelapses;

namespace Layerlapse.App.Player;

/// <summary>
/// Plays timelapses inside Layerlapse using the operating system's video framework: AVFoundation on macOS,
/// Media Foundation on Windows, GStreamer on Linux. Where none is available it opens the default player.
/// </summary>
public sealed class BuiltInVideoPlayer(Func<Window?> owner) : IVideoPlayer
{
    private readonly DefaultAppVideoPlayer _fallback = new();

    public void Play(string localPath)
    {
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException("The video is not on this computer.", localPath);
        }

        if (CreateWindow(localPath) is not { } window)
        {
            _fallback.Play(localPath);
            return;
        }

        if (owner() is { } parent)
        {
            window.Show(parent);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>A player window for this system, or null when only the default player can play it.</summary>
    public static PlayerWindow? CreateWindow(string localPath)
    {
        if (OperatingSystem.IsMacOS() && MacVideoView.IsSupported)
        {
            return new PlayerWindow(localPath, new MacVideoView(localPath));
        }

        if (OperatingSystem.IsWindows())
        {
            return new PlayerWindow(localPath, new WindowsVideoView(localPath));
        }

        if (OperatingSystem.IsLinux() && LinuxVideoView.IsSupported)
        {
            return new PlayerWindow(localPath, new LinuxVideoView(localPath));
        }

        return null;
    }
}
