using Tmds.DBus;

namespace DBusService.DBus;

/// <summary>
/// Minimal proxy for the org.freedesktop.DBus daemon bus interface.
/// Used to enumerate connections and map PIDs to bus names.
/// </summary>
[DBusInterface("org.freedesktop.DBus")]
public interface IFreedesktopDBus : IDBusObject
{
    Task<string[]> ListNamesAsync();
    Task<uint> GetConnectionUnixProcessIDAsync(string busName);
    /// <summary>Returns the unique connection name that currently owns the given well-known name.</summary>
    Task<string> GetNameOwnerAsync(string name);
    Task<IDisposable> WatchNameOwnerChangedAsync(
        Action<(string Name, string OldOwner, string NewOwner)> handler,
        Action<Exception> onError);
}
