using Tmds.DBus;

namespace DBusService.DBus;

/// <summary>
/// D-Bus service interface exposed by this process so the KDE Plasma widget
/// can retrieve the current window's menu tree.
/// Service name : com.kde.GlobalMMMenu
/// Object path  : /com/kde/GlobalMMMenu
/// </summary>
[DBusInterface("com.kde.GlobalMMMenu")]
public interface IGlobalMenuService : IDBusObject
{
    /// <summary>Returns the full menu tree for the currently-focused window as JSON.</summary>
    Task<string> GetActiveMenuJsonAsync();

    /// <summary>
    /// Triggers a menu item by its DBusMenu item id.
    /// The service forwards a "clicked" event to the application via the active
    /// com.canonical.dbusmenu proxy.
    /// </summary>
    Task ExecuteItemAsync(int itemId);
}
