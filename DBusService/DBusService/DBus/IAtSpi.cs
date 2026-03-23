using Tmds.DBus;

namespace DBusService.DBus;

/// <summary>
/// org.a11y.Bus — session bus service that returns the AT-SPI socket address.
/// </summary>
[DBusInterface("org.a11y.Bus")]
public interface IAtSpiLauncher : IDBusObject
{
    Task<string> GetAddressAsync();
}

/// <summary>
/// org.a11y.atspi.Accessible — core interface implemented by every accessible object.
///
/// GetAsync&lt;T&gt; is the Tmds.DBus 0.x built-in pattern for accessing D-Bus properties:
/// Tmds.DBus generates the Properties.Get call internally when it sees GetAsync&lt;T&gt; on
/// the interface, so no separate IProperties proxy type is created. This avoids the
/// "Duplicate type name" ModuleBuilder collision that occurs when two C# interfaces
/// share the same [DBusInterface("org.freedesktop.DBus.Properties")] attribute value.
/// </summary>
[DBusInterface("org.a11y.atspi.Accessible")]
public interface IAtSpiAccessible : IDBusObject
{
    /// <summary>Returns all child accessible objects as (busName, objectPath) pairs.</summary>
    Task<(string BusName, ObjectPath Path)[]> GetChildrenAsync();

    /// <summary>Returns the AT-SPI role integer for this object (e.g. 34 = MENU_BAR, 33 = MENU).</summary>
    Task<uint> GetRoleAsync();

    /// <summary>Returns the accessibility state bitset as two uint32 words.</summary>
    Task<uint[]> GetStateAsync();

    /// <summary>Returns application-defined attributes (key/value pairs) for this object.</summary>
    Task<IDictionary<string, string>> GetAttributesAsync();

    /// <summary>
    /// Reads a D-Bus property by name via org.freedesktop.DBus.Properties.Get.
    /// Tmds.DBus 0.x recognises this signature and generates the Properties call internally.
    /// Usage: await acc.GetAsync&lt;string&gt;("Name")
    /// </summary>
    Task<T> GetAsync<T>(string prop);
}

/// <summary>
/// org.a11y.atspi.Action — interface that allows triggering actions on a menu item.
/// </summary>
[DBusInterface("org.a11y.atspi.Action")]
public interface IAtSpiAction : IDBusObject
{
    Task<int> GetNActionsAsync();
    /// <summary>Performs the action at index (0 = primary click/activate).</summary>
    Task<bool> DoActionAsync(int index);
    Task<string> GetActionNameAsync(int index);
    /// <summary>
    /// Returns the key binding string for the action, e.g. "Ctrl+N" or "Ctrl+Shift+S".
    /// Qt serialises this as QKeySequence::PortableText.
    /// </summary>
    Task<string> GetKeyBindingAsync(int index);
}

/// <summary>

/// org.freedesktop.DBus daemon interface — separate C# type for use on the AT-SPI bus.
/// IFreedesktopDBus is already proxied on the session bus connection in Worker; reusing
/// it on the AT-SPI connection would cause a Tmds.DBus "Duplicate type name" collision
/// because both [DBusInterface("org.freedesktop.DBus")] attributes share the same
/// generated proxy class name in the ModuleBuilder.
/// </summary>
[DBusInterface("org.freedesktop.DBus")]
public interface IAtSpiDBusDaemon : IDBusObject
{
    Task<string[]> ListNamesAsync();
    Task<uint> GetConnectionUnixProcessIDAsync(string busName);
}
