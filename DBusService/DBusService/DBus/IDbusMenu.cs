using Tmds.DBus;

namespace DBusService.DBus;

/// <summary>
/// DBus proxy interface for com.canonical.dbusmenu.
/// Represents the menu tree exposed by an application window.
/// </summary>
[DBusInterface("com.canonical.dbusmenu")]
public interface IDbusMenu : IDBusObject
{
    /// <summary>
    /// Fetches the menu layout rooted at <paramref name="parentId"/>.
    /// Pass 0 for the root, a positive integer for max depth (e.g. 10 for all levels),
    /// and an empty array for all properties.
    /// Children in the returned layout are raw variant arrays: object[] { int id, IDictionary props, object[] children }.
    /// </summary>
    Task<(uint Revision, (int Id, IDictionary<string, object> Properties, object[] Children) Layout)>
        GetLayoutAsync(int parentId, int recursionDepth, string[] propertyNames);

    /// <summary>
    /// Signals that menu item <paramref name="id"/> is about to be shown.
    /// Qt/KDE apps use lazy population — this must be called before children appear in GetLayout.
    /// Returns true if the layout needs to be refreshed.
    /// </summary>
    Task<bool> AboutToShowAsync(int id);

    /// <summary>
    /// Fired when the menu layout changes. Revision is monotonically increasing;
    /// Parent is the ID of the root item that changed (-1 means the whole tree changed).
    /// </summary>
    Task<IDisposable> WatchLayoutUpdatedAsync(
        Action<(uint Revision, int Parent)> handler,
        Action<Exception>? onError = null);

    /// <summary>
    /// Fired when individual item properties (e.g. label, enabled, icon) change
    /// without a full layout rebuild.
    /// </summary>
    Task<IDisposable> WatchItemsPropertiesUpdatedAsync(
        Action<((int Id, IDictionary<string, object> Properties)[] UpdatedProps,
                (int Id, string[] RemovedNames)[] RemovedProps)> handler,
        Action<Exception>? onError = null);

    /// <summary>
    /// Sends an event to a menu item (e.g. "clicked").
    /// The application handles the event, executing the associated action.
    /// <paramref name="eventId"/> is typically "clicked".
    /// <paramref name="timestamp"/> is a uint32 event timestamp (0 is fine).
    /// </summary>
    Task EventAsync(int id, string eventId, object data, uint timestamp);
}
