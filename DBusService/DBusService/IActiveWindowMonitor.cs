namespace DBusService;

/// <summary>
/// Platform-agnostic active-window monitoring abstraction.
///
/// On X11  — implemented by <see cref="X11.X11ActiveWindowMonitor"/>, which reads
///           X11 root-window property changes (_NET_ACTIVE_WINDOW) and stores menu
///           info directly in window atoms (_KDE_NET_WM_APPMENU_*).
///
/// On Wayland — implemented by <see cref="Wayland.WaylandWindowMonitor"/>, which
///           polls KWin's D-Bus interface (org.kde.KWin at /KWin) for the active
///           window and stores menu info in an in-process dictionary.
///
/// Window handles are passed as <c>uint</c> IDs cast to <c>IntPtr</c>.  On X11
/// these are real X11 window IDs; on Wayland they are stable synthetic IDs assigned
/// by the monitor (mapped from KWin UUIDs).
/// </summary>
public interface IActiveWindowMonitor : IDisposable
{
    /// <summary>Fired (on the monitor's event-loop thread) when the focused window changes.</summary>
    event Action<(uint WindowId, string? AppmenuService, string? AppmenuPath)>? ActiveWindowChanged;

    /// <summary>
    /// Opens the connection to the underlying platform API.
    /// Returns <c>false</c> if the display/compositor is unavailable (wrong session type, headless, etc.).
    /// </summary>
    bool Connect();

    /// <summary>
    /// Blocking event loop — call from a dedicated thread (e.g. <c>Task.Run</c>).
    /// Exits when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    void RunEventLoop(CancellationToken cancellationToken);

    /// <summary>Returns the currently active/focused window ID, or 0 if unknown.</summary>
    uint GetActiveWindow();

    /// <summary>
    /// Returns all known managed top-level application windows.
    /// On X11 this is the _NET_CLIENT_LIST; on Wayland it is the set of windows seen since startup.
    /// </summary>
    uint[] GetAllClientWindows();

    /// <summary>Returns the window title, or <c>null</c> if unavailable.</summary>
    string? GetWindowName(IntPtr window);

    /// <summary>Returns the OS process ID that owns the window, or 0 if unavailable.</summary>
    uint GetWindowPid(IntPtr window);

    /// <summary>
    /// Associates a D-Bus menu service name and object path with a window.
    /// On X11 this writes _KDE_NET_WM_APPMENU_* atoms onto the window.
    /// On Wayland this stores the association in an in-process dictionary.
    /// </summary>
    void SetWindowMenuInfo(IntPtr window, string service, string path);

    /// <summary>Removes any D-Bus menu association from a window.</summary>
    void ClearWindowMenuInfo(IntPtr window);

    /// <summary>Reads the D-Bus menu service name and object path for a window.</summary>
    (string? Service, string? Path) GetWindowMenuInfo(IntPtr window);
}
