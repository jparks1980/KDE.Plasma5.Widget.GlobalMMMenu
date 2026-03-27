using System.Runtime.InteropServices;
using static DBusService.X11.NativeMethods;

namespace DBusService.X11;

/// <summary>
/// X11 implementation of <see cref="IActiveWindowMonitor"/>.
/// Monitors the X11 root window for _NET_ACTIVE_WINDOW property changes and
/// fires <see cref="ActiveWindowChanged"/> whenever the focused window changes.
/// Menu associations are persisted as _KDE_NET_WM_APPMENU_* atoms on each window.
/// </summary>
public sealed class X11ActiveWindowMonitor : IActiveWindowMonitor
{
    private IntPtr _display;
    private IntPtr _root;
    private IntPtr _netActiveWindowAtom;
    private IntPtr _netWmWindowTypeAtom;
    private IntPtr _netWmWindowTypeNormalAtom;
    private IntPtr _netWmWindowTypeDialogAtom;
    private IntPtr _netWmWindowTypeDockAtom;
    private IntPtr _netWmWindowTypeDesktopAtom;
    private IntPtr _netWmPidAtom;
    private IntPtr _netWmNameAtom;
    private IntPtr _wmNameAtom;   // fallback: older ASCII window title
    // KDE appmenu atoms — set directly on each window by Qt/KDE apps.
    // Reading these avoids any dependency on the registrar or kded5 state.
    private IntPtr _kdeNetWmAppmenuServiceNameAtom;
    private IntPtr _kdeNetWmAppmenuObjectPathAtom;
    private bool   _disposed;

    // Kept alive to prevent GC of the delegate while the display is open.
    private static NativeMethods.XErrorHandlerDelegate? _errorHandler;

    /// <summary>
    /// Raised on the event-loop thread when the active X11 window changes.
    /// AppmenuService/AppmenuPath come directly from the window's X11 properties
    /// and are null if the app didn't set them (GTK apps, etc.).
    /// </summary>
    public event Action<(uint WindowId, string? AppmenuService, string? AppmenuPath)>? ActiveWindowChanged;

    /// <summary>
    /// Opens a connection to the X11 display and selects PropertyChangeMask on the root window.
    /// Returns <c>false</c> if no X11 display is available (e.g. pure Wayland session).
    /// </summary>
    public bool Connect()
    {
        // Enable X11 thread safety so GetWindowPid() can be called from non-event-loop threads
        // (e.g., the DBus dispatch thread when AppMenuRegistrarImpl resolves service names).
        XInitThreads();

        _display = XOpenDisplay(null);
        if (_display == IntPtr.Zero)
            return false;

        _root                      = XDefaultRootWindow(_display);
        _netActiveWindowAtom       = XInternAtom(_display, "_NET_ACTIVE_WINDOW", false);
        _netWmWindowTypeAtom       = XInternAtom(_display, "_NET_WM_WINDOW_TYPE", false);
        _netWmWindowTypeNormalAtom = XInternAtom(_display, "_NET_WM_WINDOW_TYPE_NORMAL", false);
        _netWmWindowTypeDialogAtom = XInternAtom(_display, "_NET_WM_WINDOW_TYPE_DIALOG", false);
        _netWmWindowTypeDockAtom    = XInternAtom(_display, "_NET_WM_WINDOW_TYPE_DOCK", false);
        _netWmWindowTypeDesktopAtom = XInternAtom(_display, "_NET_WM_WINDOW_TYPE_DESKTOP", false);
        _netWmPidAtom                      = XInternAtom(_display, "_NET_WM_PID", false);
        _netWmNameAtom                     = XInternAtom(_display, "_NET_WM_NAME", false);
        _wmNameAtom                        = XInternAtom(_display, "WM_NAME", false);
        _kdeNetWmAppmenuServiceNameAtom    = XInternAtom(_display, "_KDE_NET_WM_APPMENU_SERVICE_NAME", false);
        _kdeNetWmAppmenuObjectPathAtom     = XInternAtom(_display, "_KDE_NET_WM_APPMENU_OBJECT_PATH", false);

        XSelectInput(_display, _root, PropertyChangeMask);
        XFlush(_display);

        // Replace the default error handler (which calls exit()) with one that
        // tolerates BadWindow — windows can be destroyed while we still hold a
        // reference, and that should never kill the service.
        _errorHandler = (IntPtr _, ref XErrorEvent e) =>  
        {
            const byte BadWindow = 3;
            if (e.error_code != BadWindow)
            {
                // Log unexpected errors to stderr but don't abort.
                Console.Error.WriteLine(
                    $"[X11] Unexpected error: code={e.error_code} request={e.request_code}");
            }
            return 0;
        };
        XSetErrorHandler(_errorHandler);

        return true;
    }

    /// <summary>
    /// Returns true if the window has a _NET_WM_WINDOW_TYPE indicating it is an
    /// application or dialog window, rather than a popup, context menu, tooltip, etc.
    /// Windows with no type property set are assumed to be normal application windows.
    /// </summary>
    private bool IsApplicationWindow(IntPtr window)
    {
        if (XGetWindowProperty(_display, window, _netWmWindowTypeAtom,
                0, 32, false, AnyPropertyType,
                out _, out int format, out long nItems, out _, out IntPtr propReturn) != 0
            || propReturn == IntPtr.Zero || nItems == 0)
        {
            // No window type atom set. Exclude system windows (dock, desktop,
            // compositor overlays) that have no _NET_WM_PID either — those are
            // shell/compositor internals like the KDE panel frame itself.
            bool hasPid = XGetWindowProperty(_display, window, _netWmPidAtom,
                0, 1, false, AnyPropertyType,
                out _, out _, out long pidItems, out _, out IntPtr pidReturn) == 0
                && pidReturn != IntPtr.Zero && pidItems > 0;
            if (pidReturn != IntPtr.Zero) XFree(pidReturn);
            return hasPid;
        }

        bool isApp = false;
        for (long i = 0; i < nItems; i++)
        {
            // format==32 → each atom is a native long (8 bytes on 64-bit).
            var atom = (IntPtr)(format == 32
                ? Marshal.ReadInt64(propReturn, (int)(i * 8))
                : Marshal.ReadInt32(propReturn, (int)(i * 4)));

            if (atom == _netWmWindowTypeNormalAtom || atom == _netWmWindowTypeDialogAtom)
            {
                isApp = true;
                break;
            }
            // Explicitly exclude dock (panel) and desktop windows.
            if (atom == _netWmWindowTypeDockAtom || atom == _netWmWindowTypeDesktopAtom)
            {
                isApp = false;
                break;
            }
        }

        XFree(propReturn);
        return isApp;
    }

    /// <summary>Returns the window title (_NET_WM_NAME, falling back to WM_NAME), or null if neither is set.</summary>
    public string? GetWindowName(IntPtr window)
        => ReadStringProperty(window, _netWmNameAtom)
        ?? ReadStringProperty(window, _wmNameAtom);

    /// <summary>Returns the _NET_WM_PID of a window, or 0 if not set.</summary>
    public uint GetWindowPid(IntPtr window)
    {
        if (XGetWindowProperty(_display, window, _netWmPidAtom,
                0, 1, false, AnyPropertyType,
                out _, out int format, out long nItems, out _, out IntPtr propReturn) != 0
            || propReturn == IntPtr.Zero || nItems == 0)
            return 0;

        uint pid = format == 32
            ? (uint)Marshal.ReadInt64(propReturn)
            : (uint)Marshal.ReadInt32(propReturn);
        XFree(propReturn);
        return pid;
    }

    /// <summary>
    /// Writes _KDE_NET_WM_APPMENU_SERVICE_NAME and _KDE_NET_WM_APPMENU_OBJECT_PATH onto a window.
    /// Called after successful PID-based discovery to restore props cleared by registrar restarts.
    /// Safe to call from any thread because XInitThreads() was called in Connect().
    /// </summary>
    public void SetWindowMenuInfo(IntPtr window, string service, string path)
    {
        if (_display == IntPtr.Zero) return;
        var PropModeReplace = 0;
        var XA_STRING = (IntPtr)31;   // predefined Xlib atom

        var svcBytes = System.Text.Encoding.UTF8.GetBytes(service + '\0');
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(path + '\0');

        XChangeProperty(_display, window, _kdeNetWmAppmenuServiceNameAtom, XA_STRING, 8, PropModeReplace, svcBytes, svcBytes.Length - 1);
        XChangeProperty(_display, window, _kdeNetWmAppmenuObjectPathAtom,  XA_STRING, 8, PropModeReplace, pathBytes, pathBytes.Length - 1);
        XFlush(_display);
    }

    /// <summary>
    /// Removes _KDE_NET_WM_APPMENU_SERVICE_NAME and _KDE_NET_WM_APPMENU_OBJECT_PATH from a window.
    /// Called when a stale path is detected so the next focus event triggers fresh discovery.
    /// </summary>
    public void ClearWindowMenuInfo(IntPtr window)
    {
        if (_display == IntPtr.Zero) return;
        XDeleteProperty(_display, window, _kdeNetWmAppmenuServiceNameAtom);
        XDeleteProperty(_display, window, _kdeNetWmAppmenuObjectPathAtom);
        XFlush(_display);
    }

    /// <summary>
    /// Reads _KDE_NET_WM_APPMENU_SERVICE_NAME and _KDE_NET_WM_APPMENU_OBJECT_PATH
    /// from a window's X11 properties. Returns nulls if not set.
    /// Must be called from the same thread that owns _display.
    /// </summary>
    public (string? Service, string? Path) GetWindowMenuInfo(IntPtr window)
    {
        string? service = ReadStringProperty(window, _kdeNetWmAppmenuServiceNameAtom);
        string? path    = ReadStringProperty(window, _kdeNetWmAppmenuObjectPathAtom);
        return (service, path);
    }

    private string? ReadStringProperty(IntPtr window, IntPtr atom)
    {
        if (XGetWindowProperty(_display, window, atom,
                0, 256, false, AnyPropertyType,
                out _, out _, out long nItems, out _, out IntPtr propReturn) != 0
            || propReturn == IntPtr.Zero || nItems == 0)
            return null;

        try   { return Marshal.PtrToStringUTF8(propReturn); }
        finally { XFree(propReturn); }
    }

    /// <summary>Reads the current value of _NET_ACTIVE_WINDOW from the root window.</summary>
    public uint GetActiveWindow()
    {
        if (_display == IntPtr.Zero) return 0;

        if (XGetWindowProperty(_display, _root, _netActiveWindowAtom,
                0, 1, false, AnyPropertyType,
                out _, out int format, out long nItems, out _, out IntPtr propReturn) != 0
            || propReturn == IntPtr.Zero || nItems == 0)
        {
            return 0;
        }

        // When format==32, X11 stores each item as a native long (8 bytes on 64-bit).
        uint windowId = format == 32
            ? (uint)Marshal.ReadInt64(propReturn)
            : (uint)Marshal.ReadInt32(propReturn);

        XFree(propReturn);
        return windowId;
    }

    /// <summary>
    /// Returns all managed X11 windows reported by the window manager via _NET_CLIENT_LIST.
    /// Used by the background prefetch worker to discover menus for windows not yet focused.
    /// </summary>
    public uint[] GetAllClientWindows()
    {
        if (_display == IntPtr.Zero) return [];
        var atom = XInternAtom(_display, "_NET_CLIENT_LIST", false);
        if (XGetWindowProperty(_display, _root, atom,
                0, 8192, false, AnyPropertyType,
                out _, out int format, out long nItems, out _, out IntPtr propReturn) != 0
            || propReturn == IntPtr.Zero || nItems == 0)
            return [];
        try
        {
            var windows = new uint[nItems];
            for (long i = 0; i < nItems; i++)
                windows[i] = format == 32
                    ? (uint)Marshal.ReadInt64(propReturn, (int)(i * 8))
                    : (uint)Marshal.ReadInt32(propReturn, (int)(i * 4));
            return windows;
        }
        finally { XFree(propReturn); }
    }

    /// <summary>
    /// Blocking event loop — call from a dedicated thread (e.g. <c>Task.Run</c>).
    /// Fires <see cref="ActiveWindowChanged"/> whenever _NET_ACTIVE_WINDOW changes,
    /// or when a newly-focused window writes its appmenu properties (startup race fix).
    /// Exits when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public void RunEventLoop(CancellationToken cancellationToken)
    {
        // Window we're watching for _KDE_NET_WM_APPMENU_SERVICE_NAME to appear.
        // Set when focus changes to a window that has no menu props yet.
        IntPtr watchedWindow = IntPtr.Zero;

        uint lastWindowId = GetActiveWindow();
        if (lastWindowId != 0)
        {
            var (svc, pth) = GetWindowMenuInfo((IntPtr)lastWindowId);
            ActiveWindowChanged?.Invoke((lastWindowId, svc, pth));
            if (string.IsNullOrEmpty(svc))
            {
                watchedWindow = (IntPtr)lastWindowId;
                XSelectInput(_display, watchedWindow, PropertyChangeMask);
                XFlush(_display);
                // TOCTOU re-check (same race as in the main loop below).
                var (svc2, pth2) = GetWindowMenuInfo(watchedWindow);
                if (!string.IsNullOrEmpty(svc2))
                {
                    XSelectInput(_display, watchedWindow, 0);
                    watchedWindow = IntPtr.Zero;
                    ActiveWindowChanged?.Invoke((lastWindowId, svc2, pth2));
                }
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (XPending(_display) > 0)
            {
                XNextEvent(_display, out XEvent xevent);

                if (xevent.type == PropertyNotify)
                {
                    var eventWindow = (IntPtr)(long)xevent.xproperty.window;
                    var eventAtom   = xevent.xproperty.atom;

                    if (eventWindow == _root && eventAtom == (nuint)_netActiveWindowAtom)
                    {
                        // ── Active window changed ─────────────────────────────
                        uint windowId = GetActiveWindow();
                        if (windowId != 0 && windowId != lastWindowId && IsApplicationWindow((IntPtr)windowId))
                        {
                            // Stop watching the previous window for menu props.
                            if (watchedWindow != IntPtr.Zero)
                            {
                                XSelectInput(_display, watchedWindow, 0);
                                watchedWindow = IntPtr.Zero;
                            }

                            lastWindowId = windowId;
                            var (svc, pth) = GetWindowMenuInfo((IntPtr)windowId);
                            ActiveWindowChanged?.Invoke((windowId, svc, pth));

                            // If menu props aren't set yet, watch this window so we
                            // catch them the moment the app writes them (startup race).
                            if (string.IsNullOrEmpty(svc))
                            {
                                watchedWindow = (IntPtr)windowId;
                                XSelectInput(_display, watchedWindow, PropertyChangeMask);
                                XFlush(_display);
                                // TOCTOU: the property may have been written between our
                                // first read above and the XSelectInput call — re-check now.
                                var (svc2, pth2) = GetWindowMenuInfo(watchedWindow);
                                if (!string.IsNullOrEmpty(svc2))
                                {
                                    XSelectInput(_display, watchedWindow, 0);
                                    watchedWindow = IntPtr.Zero;
                                    ActiveWindowChanged?.Invoke((windowId, svc2, pth2));
                                }
                            }
                        }
                    }
                    else if (watchedWindow != IntPtr.Zero &&
                             eventWindow == watchedWindow &&
                             eventAtom == (nuint)_kdeNetWmAppmenuServiceNameAtom)
                    {
                        // ── Menu properties arrived on the focused window ──────
                        var (svc, pth) = GetWindowMenuInfo(watchedWindow);
                        if (!string.IsNullOrEmpty(svc))
                        {
                            // Stop watching — we have what we need.
                            XSelectInput(_display, watchedWindow, 0);
                            watchedWindow = IntPtr.Zero;
                            ActiveWindowChanged?.Invoke((lastWindowId, svc, pth));
                        }
                    }
                }
            }
            else
            {
                // Yield briefly so the loop doesn't spin when there are no events.
                Thread.Sleep(20);
            }
        }

        // Clean up window watch subscription on exit.
        if (watchedWindow != IntPtr.Zero)
            XSelectInput(_display, watchedWindow, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_display != IntPtr.Zero)
        {
            XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
        _disposed = true;
    }
}
