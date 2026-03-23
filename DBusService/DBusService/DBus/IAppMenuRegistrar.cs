using Tmds.DBus;

namespace DBusService.DBus;

/// <summary>
/// DBus interface for com.canonical.AppMenu.Registrar — CLIENT PROXY side.
/// Used to call GetMenuForWindow on an external registrar (e.g. valapanel).
/// NOT used for our server-side implementation — see IAppMenuRegistrarService.
/// </summary>
[DBusInterface("com.canonical.AppMenu.Registrar")]
public interface IAppMenuRegistrar : IDBusObject
{
    /// <summary>Called by Qt/KDE apps to register their dbusmenu on startup.</summary>
    Task RegisterWindowAsync(uint windowId, ObjectPath menuObjectPath);

    /// <summary>Called by Qt/KDE apps when their window is destroyed.</summary>
    Task UnregisterWindowAsync(uint windowId);

    /// <summary>
    /// Returns the DBus service name and object path of the dbusmenu for the given X11 window ID.
    /// Throws DBusException if no menu is registered for the window.
    /// </summary>
    Task<(string Service, ObjectPath MenuObjectPath)> GetMenuForWindowAsync(uint windowId);
}

/// <summary>
/// SERVER-SIDE interface for com.canonical.AppMenu.Registrar.
/// Contains only the methods that apps call on us (RegisterWindow, UnregisterWindow).
/// Excludes GetMenuForWindow, whose tuple return type can confuse Tmds.DBus's
/// runtime reflection adapter and silently break the entire interface registration.
/// </summary>
[DBusInterface("com.canonical.AppMenu.Registrar")]
public interface IAppMenuRegistrarService : IDBusObject
{
    Task RegisterWindowAsync(uint windowId, ObjectPath menuObjectPath);
    Task UnregisterWindowAsync(uint windowId);
}
