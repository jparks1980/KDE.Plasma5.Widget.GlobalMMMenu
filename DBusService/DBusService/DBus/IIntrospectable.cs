using Tmds.DBus;

namespace DBusService.DBus;

/// <summary>
/// Standard D-Bus introspection interface.
/// Returns XML describing the objects and interfaces at a given path.
/// </summary>
[DBusInterface("org.freedesktop.DBus.Introspectable")]
public interface IIntrospectable : IDBusObject
{
    Task<string> IntrospectAsync();
}
