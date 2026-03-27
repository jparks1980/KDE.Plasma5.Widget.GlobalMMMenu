using System.Collections.Concurrent;
using Tmds.DBus;

namespace DBusService.DBus;

/// <summary>
/// Server-side implementation of com.canonical.AppMenu.Registrar.
///
/// Replaces the valapanel appmenu-registrar by taking ownership of the
/// com.canonical.AppMenu.Registrar bus name with REPLACE_EXISTING.  When
/// Qt/KDE apps (Dolphin, Kate, Konsole …) detect the NameOwnerChanged they
/// automatically re-call RegisterWindow with their window ID and dbusmenu path,
/// populating our in-memory registry so the Worker can look up menus instantly.
/// </summary>
public sealed class AppMenuRegistrarImpl : IAppMenuRegistrarService
{
    // windowId → (busName, menuObjectPath)
    // busName may be null until the async resolution in RegisterWindow completes.
    private readonly ConcurrentDictionary<uint, (string? Service, string Path)> _registry = new();

    private IFreedesktopDBus? _dbus;
    private IActiveWindowMonitor? _monitor;
    private ILogger? _logger;

    public ObjectPath ObjectPath => new("/com/canonical/AppMenu/Registrar");

    public void Initialize(IFreedesktopDBus dbus, IActiveWindowMonitor monitor, ILogger logger)
    {
        _dbus    = dbus;
        _monitor = monitor;
        _logger  = logger;
    }

    // ── IAppMenuRegistrar server methods ─────────────────────────────────────

    public Task RegisterWindowAsync(uint windowId, ObjectPath menuObjectPath)
    {
        var path = menuObjectPath.ToString();
        _registry[windowId] = (null, path);
        _logger?.LogInformation("  [Registrar] RegisterWindow 0x{W:X8}: path={P}", windowId, path);
        _ = ResolveServiceAsync(windowId, path);
        return Task.CompletedTask;
    }

    public Task UnregisterWindowAsync(uint windowId)
    {
        _registry.TryRemove(windowId, out _);
        return Task.CompletedTask;
    }

    public Task<(string Service, ObjectPath MenuObjectPath)> GetMenuForWindowAsync(uint windowId)
    {
        if (_registry.TryGetValue(windowId, out var entry) && entry.Service != null)
            return Task.FromResult((entry.Service, new ObjectPath(entry.Path)));

        throw new DBusException(
            "com.canonical.AppMenu.Registrar.Error.WindowNotFound",
            $"No menu registered for window 0x{windowId:X8}");
    }

    // ── Internal helpers for Worker ──────────────────────────────────────────

    /// <summary>
    /// Returns the stored service+path for a window, or (null, null) if not registered.
    /// Service may be null if the async resolution after RegisterWindow hasn't completed yet.
    /// </summary>
    public (string? Service, string? Path) TryGetMenu(uint windowId)
        => _registry.TryGetValue(windowId, out var e) ? (e.Service, e.Path) : (null, null);

    /// <summary>Removes a stale registration so the next focus event triggers fresh discovery.</summary>
    public void RemoveRegistration(uint windowId) => _registry.TryRemove(windowId, out _);

    /// <summary>
    /// Stores a fully-resolved entry for a window ID that wasn't registered via RegisterWindow.
    /// Used when PID discovery finds the correct menu for an X11 window ID that differs from
    /// the Qt internal WinId (e.g., the X11 WM frame window vs Qt's client window).
    /// </summary>
    public void StoreResolved(uint windowId, string service, string path)
        => _registry[windowId] = (service, path);

    /// <summary>Returns all currently registered (windowId, path) pairs for diagnostic logging.</summary>
    public (uint WindowId, string Path)[] GetAllRegistrations()
        => _registry.Select(kv => (kv.Key, kv.Value.Path)).ToArray();

    /// <summary>
    /// Returns all paths registered by a specific D-Bus service name.
    /// Used when PID discovery finds the bus name but the X11 window ID doesn't match
    /// any registered window ID (Qt internal WinId ≠ X11 WM frame window ID).
    /// </summary>
    public (string Path, uint RegisteredWindowId)[] GetPathsForService(string serviceName)
        => _registry
            .Where(kv => kv.Value.Service == serviceName)
            .Select(kv => (kv.Value.Path, kv.Key))
            .ToArray();

    /// <summary>
    /// Returns all registered paths whose service name has not yet been resolved.
    /// Qt apps call RegisterWindow with an internal Qt WinId; X11 PID resolution fails
    /// for these (they have no _NET_WM_PID), leaving Service=null. PID discovery probes
    /// these paths on the matching connection to confirm ownership.
    /// </summary>
    public string[] GetUnresolvedPaths()
        => _registry
            .Where(kv => kv.Value.Service == null)
            .Select(kv => kv.Value.Path)
            .ToArray();

    // ── Private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Deferred — service name resolution happens on-demand in Worker.FindMenuByPidAsync
    /// when the window is focused. Probing ALL D-Bus connections here floods the shared
    /// Connection and prevents Tmds.DBus from dispatching responses for any other calls.
    /// </summary>
    private Task ResolveServiceAsync(uint windowId, string menuPath)
    {
        return Task.CompletedTask;
    }
}
