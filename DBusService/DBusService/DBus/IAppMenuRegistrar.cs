using Tmds.DBus;

namespace DBusService.DBus;

/// <summary>
/// DBus proxy interface for com.canonical.AppMenu.Registrar.
/// Allows querying which application owns the global menu for a given X11 window.
/// </summary>
[DBusInterface("com.canonical.AppMenu.Registrar")]
public interface IAppMenuRegistrar : IDBusObject
{
    /// <summary>
    /// Returns the DBus service name and object path of the dbusmenu for the given X11 window ID.
    /// Throws DBusException if no menu is registered for the window.
    /// </summary>
    Task<(string Service, ObjectPath MenuObjectPath)> GetMenuForWindowAsync(uint windowId);
}
