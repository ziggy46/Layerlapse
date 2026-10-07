using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Layerlapse.Core.Timelapses;

namespace Layerlapse.App.Player;

/// <summary>
/// A window playing one downloaded timelapse with the operating system's own video view. Views without
/// controls of their own (<see cref="IPlaybackView"/>) get a play/pause button, a position slider and the time.
/// </summary>
public sealed class PlayerWindow : Window
{
    private readonly DockPanel _root;
    private readonly Button? _playPause;
    private readonly Slider? _position;
    private readonly TextBlock? _time;
    private readonly DispatcherTimer? _timer;
    private bool _updating;

    public PlayerWindow(string path, Control video)
    {
        Title = TimelapseNames.TryParseVideo(Path.GetFileName(path), out var start)
            ? "Timelapse " + start.ToString("ddd d MMM yyyy, HH:mm", System.Globalization.CultureInfo.CurrentCulture)
            : Path.GetFileName(path);
        Width = 960;
        Height = 600;
        MinWidth = 320;
        MinHeight = 240;
        Icon = AppIcon.Get();
        Video = video;

        var openElsewhere = new Button { Content = "Open in default player", Margin = new Thickness(8) };
        openElsewhere.Click += (_, _) =>
        {
            new DefaultAppVideoPlayer().Play(path);
            Close();
        };

        var bar = new DockPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        DockPanel.SetDock(openElsewhere, Dock.Right);
        bar.Children.Add(openElsewhere);

        if (video is IPlaybackView playback)
        {
            // Native video windows are always drawn on top, so the controls sit below the video, not over it.
            _playPause = new Button { Content = "Pause", MinWidth = 72, Margin = new Thickness(8, 8, 0, 8) };
            _playPause.Click += (_, _) =>
            {
                if (playback.IsPlaying)
                {
                    playback.Pause();
                }
                else
                {
                    playback.Play();
                }

                Refresh(playback);
            };
            _time = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), Text = "0:00 / 0:00" };
            _position = new Slider { Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
            _position.ValueChanged += (_, e) =>
            {
                if (!_updating)
                {
                    playback.Seek(e.NewValue);
                }
            };
            DockPanel.SetDock(_playPause, Dock.Left);
            DockPanel.SetDock(_time, Dock.Right);
            bar.Children.Add(_playPause);
            bar.Children.Add(_time);
            bar.Children.Add(_position);

            playback.Failed += message => ShowFailure(message);
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Refresh(playback));
            Opened += (_, _) => _timer.Start();
            Closed += (_, _) => _timer.Stop();
        }
        else
        {
            bar.LastChildFill = false;
        }

        DockPanel.SetDock(bar, Dock.Bottom);
        _root = new DockPanel();
        _root.Children.Add(bar);
        _root.Children.Add(video);
        Content = new Themes.XpWindowChrome(this, _root);
    }

    public Control Video { get; private set; }

    /// <summary>The video cannot play here: replace it with the reason, keeping "Open in default player".</summary>
    private void ShowFailure(string message)
    {
        _timer?.Stop();
        _root.Children.Remove(Video);
        Video = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
        };
        _root.Children.Add(Video);
        foreach (var control in new Control?[] { _playPause, _position, _time })
        {
            control?.IsEnabled = false;
        }
    }

    private void Refresh(IPlaybackView playback)
    {
        var duration = playback.DurationSeconds;
        var position = Math.Min(playback.PositionSeconds, duration);
        _updating = true;
        try
        {
            _position!.Maximum = Math.Max(duration, 1);
            _position.Value = Math.Max(0, position);
        }
        finally
        {
            _updating = false;
        }

        _time!.Text = Format(position) + " / " + Format(duration);
        _playPause!.Content = playback.IsPlaying ? "Pause" : "Play";
    }

    private static string Format(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}
