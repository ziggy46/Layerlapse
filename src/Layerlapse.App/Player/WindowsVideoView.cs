using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Layerlapse.App.Player;

/// <summary>
/// Plays a video inside an Avalonia window with Media Foundation's MFPlay, which draws into the child window
/// that <see cref="NativeControlHost"/> creates. Windows only; Windows "N" editions without Media Foundation
/// report <see cref="Failed"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVideoView : NativeControlHost, IPlaybackView
{
    private const int StatePlaying = 2;
    private const int EventMediaItemCreated = 5;
    private const int EventMediaItemSet = 6;
    private const int EventError = 10;
    private const int EventPlaybackEnded = 11;
    private const ushort VtI8 = 20;

    private readonly string _path;
    private IMFPMediaPlayer? _player;
    private Callback? _callback;
    private bool _ended;

    public WindowsVideoView(string path)
    {
        _path = path;
    }

    public event Action<string>? Failed;

    public bool IsPlaying => _player is not null && _player.GetState(out var state) >= 0 && state == StatePlaying;

    public double PositionSeconds => Read((IMFPMediaPlayer p, ref Guid type, out PropVariant value) => p.GetPosition(ref type, out value));

    public double DurationSeconds => Read((IMFPMediaPlayer p, ref Guid type, out PropVariant value) => p.GetDuration(ref type, out value));

    public void Play()
    {
        if (_player is null)
        {
            return;
        }

        if (_ended)
        {
            _ended = false;
            Seek(0);
        }

        _player.Play();
    }

    public void Pause()
    {
        _player?.Pause();
        _player?.UpdateVideo();
    }

    public void Seek(double seconds)
    {
        if (_player is null)
        {
            return;
        }

        _ended = false;
        var type = Guid.Empty; // MFP_POSITIONTYPE_100NS
        var value = new PropVariant { Vt = VtI8, Value = (long)(Math.Max(0, seconds) * 10_000_000) };
        _player.SetPosition(ref type, ref value);
        _player.UpdateVideo();
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        try
        {
            _callback = new Callback(this);
            var hr = MFPCreateMediaPlayer(_path, true, 0, _callback, handle.Handle, out var player);
            Marshal.ThrowExceptionForHR(hr);
            _player = player;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            _player = null;
            ReportFailure("Windows could not start its video player (" + e.Message.Trim() + ").");
        }

        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_player is { } player)
        {
            _player = null;
            player.Shutdown();
            Marshal.ReleaseComObject(player);
        }

        _callback = null;
        base.DestroyNativeControlCore(control);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            _player?.UpdateVideo(); // MFPlay keeps the old picture size until told
        }
    }

    private delegate int PositionGetter(IMFPMediaPlayer player, ref Guid type, out PropVariant value);

    private double Read(PositionGetter get)
    {
        if (_player is null)
        {
            return 0;
        }

        var type = Guid.Empty;
        return get(_player, ref type, out var value) >= 0 ? value.Value / 10_000_000.0 : 0;
    }

    private void ReportFailure(string message) => Dispatcher.UIThread.Post(() => Failed?.Invoke(message));

    /// <summary>Called on a Media Foundation thread with each player event.</summary>
    private void OnEvent(int type, int hr)
    {
        if (type == EventPlaybackEnded)
        {
            Dispatcher.UIThread.Post(() => _ended = true);
        }
        else if (hr < 0 && type is EventMediaItemCreated or EventMediaItemSet or EventError)
        {
            ReportFailure("Windows cannot play this video (" + Marshal.GetExceptionForHR(hr)?.Message.Trim() + ").");
        }
    }

    [DllImport("mfplay.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFPCreateMediaPlayer(
        string url,
        [MarshalAs(UnmanagedType.Bool)] bool startPlayback,
        int options,
        IMFPMediaPlayerCallback? callback,
        IntPtr hwnd,
        out IMFPMediaPlayer player);

    /// <summary>PROPVARIANT holding a VT_I8/VT_UI8 (24 bytes on 64-bit Windows).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Vt;

        [FieldOffset(8)]
        public long Value;
    }

    [ComVisible(true)]
    private sealed class Callback(WindowsVideoView owner) : IMFPMediaPlayerCallback
    {
        public void OnMediaPlayerEvent(IntPtr header)
        {
            // MFP_EVENT_HEADER: eEventType, hrEvent, ... (valid only during this call)
            owner.OnEvent(Marshal.ReadInt32(header, 0), Marshal.ReadInt32(header, 4));
        }
    }

    [ComImport]
    [Guid("766c8ffb-5fdb-4fea-a28d-b912996f51bd")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFPMediaPlayerCallback
    {
        [PreserveSig]
        void OnMediaPlayerEvent(IntPtr header);
    }

    /// <summary>IMFPMediaPlayer, in mfplay.h's vtable order. Unused methods only hold their slot.</summary>
    [ComImport]
    [Guid("a714590a-58af-430a-85bf-44f5ec838d85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFPMediaPlayer
    {
        [PreserveSig] int Play();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
        [PreserveSig] int FrameStep();
        [PreserveSig] int SetPosition(ref Guid positionType, ref PropVariant position);
        [PreserveSig] int GetPosition(ref Guid positionType, out PropVariant position);
        [PreserveSig] int GetDuration(ref Guid positionType, out PropVariant duration);
        [PreserveSig] int SetRate(float rate);
        [PreserveSig] int GetRate(out float rate);
        [PreserveSig] int GetSupportedRates(int forward, out float slowest, out float fastest);
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int CreateMediaItemFromURL(IntPtr url, int sync, IntPtr userData, out IntPtr item);
        [PreserveSig] int CreateMediaItemFromObject(IntPtr obj, int sync, IntPtr userData, out IntPtr item);
        [PreserveSig] int SetMediaItem(IntPtr item);
        [PreserveSig] int ClearMediaItem();
        [PreserveSig] int GetMediaItem(out IntPtr item);
        [PreserveSig] int GetVolume(out float volume);
        [PreserveSig] int SetVolume(float volume);
        [PreserveSig] int GetBalance(out float balance);
        [PreserveSig] int SetBalance(float balance);
        [PreserveSig] int GetMute(out int mute);
        [PreserveSig] int SetMute(int mute);
        [PreserveSig] int GetNativeVideoSize(IntPtr video, IntPtr arVideo);
        [PreserveSig] int GetIdealVideoSize(IntPtr minSize, IntPtr maxSize);
        [PreserveSig] int SetVideoSourceRect(IntPtr rect);
        [PreserveSig] int GetVideoSourceRect(IntPtr rect);
        [PreserveSig] int SetAspectRatioMode(int mode);
        [PreserveSig] int GetAspectRatioMode(out int mode);
        [PreserveSig] int GetVideoWindow(out IntPtr hwnd);
        [PreserveSig] int UpdateVideo();
        [PreserveSig] int SetBorderColor(int color);
        [PreserveSig] int GetBorderColor(out int color);
        [PreserveSig] int InsertEffect(IntPtr effect, int optional);
        [PreserveSig] int RemoveEffect(IntPtr effect);
        [PreserveSig] int RemoveAllEffects();
        [PreserveSig] int Shutdown();
    }
}
