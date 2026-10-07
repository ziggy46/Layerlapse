using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Layerlapse.App.Player;

/// <summary>Minimal Objective-C runtime calls for hosting AVKit's player view on macOS.</summary>
[SupportedOSPlatform("macos")]
internal static class ObjC
{
    private const string Runtime = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect(double x, double y, double width, double height)
    {
        public double X = x;
        public double Y = y;
        public double Width = width;
        public double Height = height;
    }

    /// <summary>CMTime, as used by -[AVPlayer seekToTime:].</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CMTime
    {
        public long Value;
        public int Timescale;
        public uint Flags;
        public long Epoch;
    }

    private static readonly Lazy<bool> Frameworks = new(() =>
        NativeLibrary.TryLoad("/System/Library/Frameworks/AVFoundation.framework/AVFoundation", out _)
        && NativeLibrary.TryLoad("/System/Library/Frameworks/AVKit.framework/AVKit", out _));

    /// <summary>Loads AVFoundation and AVKit so their classes are registered.</summary>
    public static bool LoadFrameworks() => Frameworks.Value;

    public static IntPtr Class(string name) => objc_getClass(name);

    public static IntPtr Sel(string name) => sel_registerName(name);

    public static IntPtr Send(IntPtr receiver, string selector) => objc_msgSend(receiver, Sel(selector));

    public static IntPtr Send(IntPtr receiver, string selector, IntPtr argument) => objc_msgSend(receiver, Sel(selector), argument);

    public static IntPtr Send(IntPtr receiver, string selector, long argument) => objc_msgSend_long(receiver, Sel(selector), argument);

    public static IntPtr Send(IntPtr receiver, string selector, CGRect argument) => objc_msgSend_rect(receiver, Sel(selector), argument);

    public static void Send(IntPtr receiver, string selector, CMTime time, CMTime toleranceBefore, CMTime toleranceAfter) =>
        objc_msgSend_seek(receiver, Sel(selector), time, toleranceBefore, toleranceAfter);

    public static double SendDouble(IntPtr receiver, string selector) => objc_msgSend_double(receiver, Sel(selector));

    public static CMTime SendTime(IntPtr receiver, string selector) => objc_msgSend_cmtime(receiver, Sel(selector));

    public static IntPtr NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return Send(Class("NSString"), "stringWithUTF8String:", utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    [DllImport(Runtime)]
    private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Runtime)]
    private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Runtime)]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(Runtime)]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_long(IntPtr receiver, IntPtr selector, long argument);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_rect(IntPtr receiver, IntPtr selector, CGRect argument);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_seek(IntPtr receiver, IntPtr selector, CMTime time, CMTime before, CMTime after);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern double objc_msgSend_double(IntPtr receiver, IntPtr selector);

    [DllImport(Runtime, EntryPoint = "objc_msgSend")]
    private static extern CMTime objc_msgSend_cmtime(IntPtr receiver, IntPtr selector);
}
