namespace Layerlapse.App.Player;

/// <summary>
/// A video view without controls of its own (Windows and Linux). <see cref="PlayerWindow"/> draws a bar with
/// play/pause, a position slider and the time underneath it. macOS's AVPlayerView has its own controls.
/// </summary>
public interface IPlaybackView
{
    bool IsPlaying { get; }

    double PositionSeconds { get; }

    /// <summary>0 until the video is open.</summary>
    double DurationSeconds { get; }

    /// <summary>Plays, from the start again if the video had ended.</summary>
    void Play();

    void Pause();

    void Seek(double seconds);

    /// <summary>Raised on the UI thread when the video cannot be played here, with a one-line reason.</summary>
    event Action<string>? Failed;
}
