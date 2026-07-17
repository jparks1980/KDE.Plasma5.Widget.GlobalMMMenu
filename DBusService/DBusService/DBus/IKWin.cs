using Tmds.DBus;

namespace DBusService.DBus;

/// <summary>
/// Tmds.DBus proxy for the <c>org.kde.KWin</c> interface at <c>/KWin</c>.
/// KWin 6 (Plasma 6) method — does NOT exist in KWin 5.
/// </summary>
[DBusInterface("org.kde.KWin")]
public interface IKWin : IDBusObject
{
    /// <summary>
    /// Returns metadata for the window identified by <paramref name="uuid"/>.
    /// Returns an empty dictionary if the window no longer exists.
    /// D-Bus method name: <c>getWindowInfo</c>  (camelCase — KWin 6 convention)
    /// </summary>
    // ReSharper disable once InconsistentNaming
    Task<IDictionary<string, object>> getWindowInfoAsync(string uuid);
}

/// <summary>
/// Tmds.DBus proxy for the <c>org.kde.kwin.Scripting</c> interface.
/// Supports both KWin 5 (Plasma 5) and KWin 6 (Plasma 6) scripting.
///
/// KWin 5 has: loadScript, start, isScriptLoaded, unloadScript
/// KWin 6 has: loadScript, unloadScript   (start and isScriptLoaded were removed)
///
/// Method names start with lowercase to match KWin's camelCase D-Bus convention.
/// Tmds.DBus strips the "Async" suffix → D-Bus method = C# name minus "Async".
/// </summary>
[DBusInterface("org.kde.kwin.Scripting")]
public interface IKWinScripting : IDBusObject
{
    /// <summary>
    /// Loads a KWin script from <paramref name="filePath"/> registered under
    /// <paramref name="pluginName"/>.  Returns the internal script ID, or -1
    /// on failure.  Present in both KWin 5 and KWin 6.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    Task<int> loadScriptAsync(string filePath, string pluginName);

    /// <summary>
    /// Starts all loaded but not-yet-running scripts.
    /// Present in KWin 5 ONLY — removed in KWin 6.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    Task startAsync();

    /// <summary>
    /// Returns true if a script with <paramref name="pluginName"/> is loaded.
    /// Present in KWin 5 ONLY — removed in KWin 6.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    Task<bool> isScriptLoadedAsync(string pluginName);

    /// <summary>
    /// Unloads the script registered under <paramref name="pluginName"/>.
    /// Present in both KWin 5 and KWin 6.
    /// </summary>
    // ReSharper disable once InconsistentNaming
    Task<bool> unloadScriptAsync(string pluginName);
}

/// <summary>
/// Server-side D-Bus interface called by the KWin window-monitor script
/// via <c>callDBus</c> when a window gains focus (both KWin 5 and KWin 6).
///
/// KWin 5 fires <c>workspace.clientActivated</c>; KWin 6 fires
/// <c>workspace.windowActivated</c>.  Both call this same callback method.
///
/// All arguments are passed as strings from JS to avoid int32/uint32 type
/// mismatch via KWin's callDBus.
/// </summary>
[DBusInterface("com.kde.GlobalMMMenu.WindowMonitor")]
public interface IKWinWindowCallback : IDBusObject
{
    /// <summary>
    /// Called by the KWin script whenever a new window gains focus.
    /// <paramref name="windowId"/>  = String(client.windowId || 0) — numeric KWin ID (0 on Wayland/KWin6)
    /// <paramref name="pid"/>       = String(client.pid || 0)      — process ID, always as string
    /// <paramref name="caption"/>   = String(client.caption || '') — window title
    /// <paramref name="internalId"/>= String(client.internalId)    — KWin UUID e.g. "{4898dcbd-…}"
    /// <paramref name="gx"/>        = String(x position)           — screen X coordinate
    /// <paramref name="gy"/>        = String(y position)           — screen Y coordinate
    /// </summary>
    Task WindowActivatedAsync(string windowId, string pid, string caption, string internalId, string gx, string gy);
}

