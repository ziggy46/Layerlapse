using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Layerlapse.App.Player;

/// <summary>
/// Plays a video inside an Avalonia window with GStreamer's playbin, drawing into the X11 child window that
/// <see cref="NativeControlHost"/> creates. Linux on X11 only. Timelapses are H.264, which most distributions
/// decode only with an extra package (gstreamer1.0-libav); without it <see cref="Failed"/> says so.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxVideoView : NativeControlHost, IPlaybackView
{
    private const string Gst = "libgstreamer-1.0.so.0";
    private const string GstVideo = "libgstvideo-1.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";

    private const int StateNull = 1;
    private const int StateReady = 2;
    private const int StatePlaying = 4;
    private const int StatePaused = 3;
    private const int StateChangeFailure = 0;
    private const int FormatTime = 3;
    private const int SeekFlushAccurate = 1 | 2;
    private const int MessageEos = 1;
    private const int MessageError = 2;

    private static bool? s_initialized;

    private readonly string _path;
    private readonly DispatcherTimer _busTimer;
    private IntPtr _pipeline;
    private IntPtr _bus;
    private IntPtr _sink;
    private bool _playing;
    private bool _ended;

    public LinuxVideoView(string path)
    {
        _path = path;
        _busTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => PollBus());
    }

    public event Action<string>? Failed;

    /// <summary>True when GStreamer is installed and starts. Without it the app opens the default player.</summary>
    public static bool IsSupported
    {
        get
        {
            if (s_initialized is null)
            {
                try
                {
                    s_initialized = gst_init_check(IntPtr.Zero, IntPtr.Zero, out _);
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
                {
                    s_initialized = false;
                }
            }

            return s_initialized.Value;
        }
    }

    public bool IsPlaying => _playing;

    public double PositionSeconds =>
        _pipeline != IntPtr.Zero && gst_element_query_position(_pipeline, FormatTime, out var ns) ? ns / 1e9 : 0;

    public double DurationSeconds =>
        _pipeline != IntPtr.Zero && gst_element_query_duration(_pipeline, FormatTime, out var ns) && ns > 0 ? ns / 1e9 : 0;

    public void Play()
    {
        if (_pipeline == IntPtr.Zero)
        {
            return;
        }

        if (_ended)
        {
            Seek(0);
        }

        gst_element_set_state(_pipeline, StatePlaying);
        _playing = true;
    }

    public void Pause()
    {
        if (_pipeline != IntPtr.Zero)
        {
            gst_element_set_state(_pipeline, StatePaused);
            _playing = false;
        }
    }

    public void Seek(double seconds)
    {
        if (_pipeline != IntPtr.Zero)
        {
            _ended = false;
            gst_element_seek_simple(_pipeline, FormatTime, SeekFlushAccurate, (long)(Math.Max(0, seconds) * 1e9));
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        if (handle.HandleDescriptor != "XID")
        {
            ReportFailure("The built-in player needs X11 (this session uses " + handle.HandleDescriptor + ").");
            return handle;
        }

        try
        {
            Start(handle.Handle);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            Stop();
            ReportFailure(e.Message);
        }

        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Stop();
        base.DestroyNativeControlCore(control);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _sink != IntPtr.Zero)
        {
            gst_video_overlay_expose(_sink); // redraw the paused picture at the new size
        }
    }

    private void Start(IntPtr xid)
    {
        if (!IsSupported)
        {
            throw new InvalidOperationException("GStreamer is not installed.");
        }

        _pipeline = gst_element_factory_make("playbin", null);
        if (_pipeline == IntPtr.Zero)
        {
            throw new InvalidOperationException("GStreamer's playbin is missing (install gstreamer1.0-plugins-base).");
        }

        gst_object_ref_sink(_pipeline);
        _sink = CreateSink();
        gst_video_overlay_set_window_handle(_sink, (nuint)xid);
        g_object_set(_pipeline, "video-sink", _sink, IntPtr.Zero); // playbin takes the sink's reference
        g_object_set(_pipeline, "uri", new Uri(Path.GetFullPath(_path)).AbsoluteUri, IntPtr.Zero);
        _bus = gst_element_get_bus(_pipeline);
        if (gst_element_set_state(_pipeline, StatePlaying) == StateChangeFailure)
        {
            throw new InvalidOperationException("GStreamer could not start playing this video.");
        }

        _playing = true;
        _busTimer.Start();
    }

    /// <summary>
    /// xvimagesink scales in hardware but needs the XVideo extension, which some X servers (and Xvfb) lack;
    /// ximagesink works everywhere. The sink is tried in READY, where it opens the display.
    /// </summary>
    private static IntPtr CreateSink()
    {
        foreach (var factory in new[] { "xvimagesink", "ximagesink" })
        {
            var sink = gst_element_factory_make(factory, null);
            if (sink == IntPtr.Zero)
            {
                continue;
            }

            if (gst_element_set_state(sink, StateReady) != StateChangeFailure)
            {
                gst_element_set_state(sink, StateNull);
                return sink;
            }

            gst_element_set_state(sink, StateNull);
            gst_object_ref_sink(sink);
            gst_object_unref(sink);
        }

        throw new InvalidOperationException("GStreamer has no X11 video output (install gstreamer1.0-plugins-base).");
    }

    private void PollBus()
    {
        if (_bus == IntPtr.Zero)
        {
            return;
        }

        var error = gst_bus_pop_filtered(_bus, MessageError);
        if (error != IntPtr.Zero)
        {
            gst_message_parse_error(error, out var gerror, out var debug);
            var text = gerror == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(gerror, 8)); // GError.message
            if (gerror != IntPtr.Zero)
            {
                g_error_free(gerror);
            }

            g_free(debug);
            gst_mini_object_unref(error);
            Stop();
            ReportFailure("GStreamer cannot play this video: " + text
                + " H.264 needs the gstreamer1.0-libav package (gst-libav on some systems).");
            return;
        }

        var eos = gst_bus_pop_filtered(_bus, MessageEos);
        if (eos != IntPtr.Zero)
        {
            gst_mini_object_unref(eos);
            gst_element_set_state(_pipeline, StatePaused);
            _playing = false;
            _ended = true;
        }
    }

    private void Stop()
    {
        _busTimer.Stop();
        _playing = false;
        if (_pipeline != IntPtr.Zero)
        {
            gst_element_set_state(_pipeline, StateNull);
        }

        if (_bus != IntPtr.Zero)
        {
            gst_object_unref(_bus);
            _bus = IntPtr.Zero;
        }

        if (_pipeline != IntPtr.Zero)
        {
            gst_object_unref(_pipeline);
            _pipeline = IntPtr.Zero;
        }

        _sink = IntPtr.Zero;
    }

    private void ReportFailure(string message) => Dispatcher.UIThread.Post(() => Failed?.Invoke(message));

    [DllImport(Gst)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool gst_init_check(IntPtr argc, IntPtr argv, out IntPtr error);

    [DllImport(Gst)]
    private static extern IntPtr gst_element_factory_make(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string factory, [MarshalAs(UnmanagedType.LPUTF8Str)] string? name);

    [DllImport(Gst)]
    private static extern int gst_element_set_state(IntPtr element, int state);

    [DllImport(Gst)]
    private static extern IntPtr gst_element_get_bus(IntPtr element);

    [DllImport(Gst)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool gst_element_query_position(IntPtr element, int format, out long position);

    [DllImport(Gst)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool gst_element_query_duration(IntPtr element, int format, out long duration);

    [DllImport(Gst)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool gst_element_seek_simple(IntPtr element, int format, int flags, long position);

    [DllImport(Gst)]
    private static extern IntPtr gst_bus_pop_filtered(IntPtr bus, int types);

    [DllImport(Gst)]
    private static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);

    [DllImport(Gst)]
    private static extern void gst_mini_object_unref(IntPtr obj);

    [DllImport(Gst)]
    private static extern IntPtr gst_object_ref_sink(IntPtr obj);

    [DllImport(Gst)]
    private static extern void gst_object_unref(IntPtr obj);

    [DllImport(GstVideo)]
    private static extern void gst_video_overlay_set_window_handle(IntPtr overlay, nuint handle);

    [DllImport(GstVideo)]
    private static extern void gst_video_overlay_expose(IntPtr overlay);

    // g_object_set is variadic; with pointer-sized arguments only, a fixed signature is safe on x86-64 and arm64 Linux.
    [DllImport(GObject)]
    private static extern void g_object_set(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr value, IntPtr end);

    [DllImport(GObject)]
    private static extern void g_object_set(
        IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, IntPtr end);

    [DllImport(GLib)]
    private static extern void g_error_free(IntPtr error);

    [DllImport(GLib)]
    private static extern void g_free(IntPtr mem);
}
