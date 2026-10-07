using Avalonia.Controls;
using Layerlapse.Core.Timelapses;

namespace Layerlapse.App.Player;

/// <summary>
/// Plays timelapses inside Layerlapse using the operating system's video framework. Implemented for macOS
/// (AVFoundation). Elsewhere, and if the native view cannot be created, it opens the default player.
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

        if (OperatingSystem.IsMacOS() && MacVideoView.IsSupported)
        {
            var window = new PlayerWindow(localPath, new MacVideoView(localPath));
            if (owner() is { } parent)
            {
                window.Show(parent);
            }
            else
            {
                window.Show();
            }

            return;
        }

        _fallback.Play(localPath);
    }
}
