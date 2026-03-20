using Tmds.DBus;

namespace DBusService.DBus;

/// <summary>
/// DBus proxy interface for org.kde.kappmenu (provided by kded5 appmenu module).
/// Fires showRequest whenever focus moves to a window with a registered menu —
/// including windows that registered before appmenu-registrar was installed.
/// </summary>
[DBusInterface("org.kde.kappmenu")]
public interface IKAppMenu : IDBusObject
{
    /// <summary>
    /// Fired when the active window has a registered menu. Provides the DBus
    /// service name and object path needed to call com.canonical.dbusmenu.
    /// </summary>
    Task<IDisposable> WatchShowRequestAsync(
        Action<(string Service, ObjectPath MenuObjectPath, int ActionId)> handler,
        Action<Exception>? onError = null);

    /// <summary>
    /// Fired when the active window has no registered menu (e.g. plain terminal, desktop).
    /// </summary>
    Task<IDisposable> WatchMenuHiddenAsync(
        Action<(string Service, ObjectPath MenuObjectPath)> handler,
        Action<Exception>? onError = null);
}
