using System.Runtime.InteropServices;

namespace DBusService.X11;

/// <summary>
/// P/Invoke bindings for libX11 and the X11 event structs needed for
/// monitoring root-window property changes (_NET_ACTIVE_WINDOW).
/// </summary>
internal static class NativeMethods
{
    private const string LibX11 = "libX11.so.6";

    // Must be called before any other X11 calls when using X11 from multiple threads.
    [DllImport(LibX11)] internal static extern int XInitThreads();

    [DllImport(LibX11)] internal static extern IntPtr XOpenDisplay(string? display);
    [DllImport(LibX11)] internal static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] internal static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] internal static extern int XSelectInput(IntPtr display, IntPtr window, long eventMask);
    [DllImport(LibX11)] internal static extern int XPending(IntPtr display);
    [DllImport(LibX11)] internal static extern int XNextEvent(IntPtr display, out XEvent @event);
    [DllImport(LibX11)] internal static extern IntPtr XInternAtom(IntPtr display, string atomName, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [DllImport(LibX11)] internal static extern int XGetWindowProperty(
        IntPtr display, IntPtr window, IntPtr property,
        long offset, long length,
        [MarshalAs(UnmanagedType.Bool)] bool delete,
        IntPtr reqType,
        out IntPtr actualType, out int actualFormat,
        out long nItems, out long bytesAfter,
        out IntPtr propReturn);
    [DllImport(LibX11)] internal static extern int XFree(IntPtr data);
    [DllImport(LibX11)] internal static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] internal static extern int XChangeProperty(
        IntPtr display, IntPtr window, IntPtr property, IntPtr type,
        int format, int mode, byte[] data, int nElements);
    [DllImport(LibX11)] internal static extern int XDeleteProperty(
        IntPtr display, IntPtr window, IntPtr property);

    // Installs a global X error handler. The delegate must be kept alive (stored
    // in a static field) for the lifetime of the display connection.
    internal delegate int XErrorHandlerDelegate(IntPtr display, ref XErrorEvent errorEvent);
    [DllImport(LibX11)] internal static extern XErrorHandlerDelegate XSetErrorHandler(XErrorHandlerDelegate handler);

    // ── Event constants ──────────────────────────────────────────────────────
    internal const int  PropertyNotify     = 28;
    internal const long PropertyChangeMask = 1L << 22;
    internal static readonly IntPtr AnyPropertyType = IntPtr.Zero;
}

// ── XPropertyEvent layout for 64-bit Linux ──────────────────────────────────
// C layout: int, unsigned long, Bool(int), Display*, Window, Atom, Time, int
// With 64-bit alignment padding after `type` and after `send_event`.

/// <summary>XPropertyEvent (PropertyNotify = 28) from X11/Xlib.h.</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct XPropertyEvent
{
    [FieldOffset( 0)] public int   type;
    [FieldOffset( 8)] public nuint serial;
    [FieldOffset(16)] public int   send_event;
    [FieldOffset(24)] public nuint display;
    [FieldOffset(32)] public nuint window;
    [FieldOffset(40)] public nuint atom;
    [FieldOffset(48)] public nuint time;
    [FieldOffset(56)] public int   state;
}

/// <summary>
/// XEvent union — 192 bytes on 64-bit Linux.
/// Only the fields used by this service are exposed.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
internal struct XEvent
{
    [FieldOffset(0)] public int          type;
    [FieldOffset(0)] public XPropertyEvent xproperty;
}

/// <summary>XErrorEvent — passed to the XSetErrorHandler callback.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XErrorEvent
{
    public int    type;
    public IntPtr display;
    public nuint  resourceid;
    public nuint  serial;
    public byte   error_code;
    public byte   request_code;
    public byte   minor_code;
}
