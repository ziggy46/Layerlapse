using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Layerlapse.App.Player;

/// <summary>
/// Hosts AVKit's AVPlayerView (AVFoundation playback with the system's play button and scrubber) inside an
/// Avalonia window. macOS only.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacVideoView : NativeControlHost
{
    private const long ControlsStyleInline = 1;

    private readonly string _path;
    private IntPtr _player;
    private IntPtr _view;

    public MacVideoView(string path)
    {
        _path = path;
    }

    public static bool IsSupported => OperatingSystem.IsMacOS() && ObjC.LoadFrameworks();

    /// <summary>The player's current position in seconds (for tests and diagnostics).</summary>
    public double CurrentSeconds => _player == IntPtr.Zero ? 0 : Seconds(ObjC.SendTime(_player, "currentTime"));

    public void Seek(double seconds)
    {
        if (_player == IntPtr.Zero)
        {
            return;
        }

        var exact = new ObjC.CMTime { Value = 0, Timescale = 1, Flags = 1 };
        ObjC.Send(_player, "seekToTime:toleranceBefore:toleranceAfter:",
            new ObjC.CMTime { Value = (long)(seconds * 600), Timescale = 600, Flags = 1 }, exact, exact);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!ObjC.LoadFrameworks() || ObjC.Class("AVPlayerView") == IntPtr.Zero || ObjC.Class("AVPlayer") == IntPtr.Zero)
        {
            throw new PlatformNotSupportedException("AVKit is not available on this Mac.");
        }

        var url = ObjC.Send(ObjC.Class("NSURL"), "fileURLWithPath:", ObjC.NSString(_path));
        _player = ObjC.Send(ObjC.Send(ObjC.Class("AVPlayer"), "playerWithURL:", url), "retain");

        var view = ObjC.Send(ObjC.Class("AVPlayerView"), "alloc");
        _view = ObjC.Send(view, "initWithFrame:", new ObjC.CGRect(0, 0, Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height)));
        ObjC.Send(_view, "setControlsStyle:", ControlsStyleInline);
        ObjC.Send(_view, "setPlayer:", _player);
        ObjC.Send(_player, "play");
        return new PlatformHandle(_view, "NSView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_player != IntPtr.Zero)
        {
            ObjC.Send(_player, "pause");
            ObjC.Send(_view, "setPlayer:", IntPtr.Zero);
            ObjC.Send(_player, "release");
            _player = IntPtr.Zero;
        }

        if (_view != IntPtr.Zero)
        {
            ObjC.Send(_view, "removeFromSuperview");
            ObjC.Send(_view, "release");
            _view = IntPtr.Zero;
        }
    }

    private static double Seconds(ObjC.CMTime time) => time.Timescale == 0 ? 0 : (double)time.Value / time.Timescale;
}
