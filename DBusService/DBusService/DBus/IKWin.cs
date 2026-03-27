using Tmds.DBus;

namespace DBusService.DBus;

/// <summary>
/// Tmds.DBus proxy for the <c>org.kde.KWin</c> interface at <c>/KWin</c>.
///
/// NOTE: <c>getWindowInfo(uuid)</c> below is a KWin 6 method and does NOT exist in
/// KWin 5 / Plasma 5.  It is retained here for reference only and is not called by
/// the current Plasma 5 implementation.
///
/// If needed in a future KWin 6 port: KWin 6 D-Bus methods use camelCase names.
/// Tmds.DBus derives the D-Bus method name by stripping the "Async" suffix from the
/// C# method name — no case conversion is performed.  Therefore, C# method names here
/// intentionally start with a lowercase letter to match the KWin D-Bus convention.
/// </summary>
[DBusInterface("org.kde.KWin")]
public interface IKWin : IDBusObject
{
    /// <summary>
    /// Returns metadata for the window identified by <paramref name="uuid"/>.
    /// Returns an empty dictionary if the window no longer exists.
    /// D-Bus method name: <c>getWindowInfo</c>
    /// </summary>
    // ReSharper disable once InconsistentNaming
    Task<IDictionary<string, object>> getWindowInfoAsync(string uuid);
}

/// <summary>
/// Tmds.DBus proxy for the <c>org.kde.kwin.Scripting</c> interface at <c>/Scripting</c>.
/// Used to load a KWin JavaScript script that watches workspace.windowActivated and
/// fires a D-Bus callback into this service on every window focus change.
/// Method names start with lowercase to match KWin's camelCase D-Bus convention.
/// </summary>
[DBusInterface("org.kde.kwin.Scripting")]
public interface IKWinScripting : IDBusObject
{
    /// <summary>
    /// Loads a KWin script from <paramref name="filePath"/> and registers it under
    /// <paramref name="pluginName"/> so it can be identified for later unloading.
    /// Returns the internal script ID, or -1 on failure.
    /// D-Bus method name: <c>loadScript</c>
    /// </summary>
    // ReSharper disable once InconsistentNaming
    Task<int> loadScriptAsync(string filePath, string pluginName);

    /// <summary>
    /// Starts all loaded but not-yet-running scripts.
    /// D-Bus method name: <c>start</c>
    /// </summary>
    // ReSharper disable once InconsistentNaming
    Task startAsync();

    /// <summary>
    /// Returns true if a script with the given <paramref name="pluginName"/> is currently loaded.
    /// D-Bus method name: <c>isScriptLoaded</c>
    /// </summary>
    // ReSharper disable once InconsistentNaming
    Task<bool> isScriptLoadedAsync(string pluginName);

    /// <summary>
    /// Unloads the script registered under <paramref name="pluginName"/>.
    /// D-Bus method name: <c>unloadScript</c>
    /// </summary>
    // ReSharper disable once InconsistentNaming
    Task<bool> unloadScriptAsync(string pluginName);
}

/// <summary>
/// Server-side D-Bus interface that the KWin JavaScript window-monitor script calls
/// via <c>callDBus</c> when <c>workspace.clientActivated</c> fires (KWin 5 / Plasma 5).
///
/// Registered on the session bus as <c>com.kde.GlobalMMMenu</c> at path
/// <c>/com/kde/globalmmmenu/windowmonitor</c>.
///
/// The KWin script invokes <c>WindowActivated(windowId, pid, caption)</c> on this interface.
/// All arguments are passed as strings from the script to avoid int32/uint32 D-Bus type
/// signature mismatches that occur when KWin JS passes numeric values via callDBus.
/// </summary>
[DBusInterface("com.kde.GlobalMMMenu.WindowMonitor")]
public interface IKWinWindowCallback : IDBusObject
{
    /// <summary>
    /// Called by the KWin script whenever a new window gains focus.
    /// <paramref name="windowId"/> is <c>String(client.windowId)</c> — the numeric KWin window ID.
    /// <paramref name="pid"/>      is <c>String(client.pid)</c>      — passed as string to avoid D-Bus type mismatch.
    /// <paramref name="caption"/>  is <c>String(client.caption)</c>  — window title for logging and fallback matching.
    /// </summary>
    Task WindowActivatedAsync(string windowId, string pid, string caption);
}
