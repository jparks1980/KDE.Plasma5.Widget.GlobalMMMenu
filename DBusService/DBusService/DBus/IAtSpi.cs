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

    /// <summary>
    /// Subscribes to the org.freedesktop.DBus.Properties.PropertiesChanged signal for this object.
    /// Tmds.DBus 0.x recognises this method name as a special case and generates the subscription
    /// inline — no separate [DBusInterface("org.freedesktop.DBus.Properties")] type is needed,
    /// so there is no risk of a ModuleBuilder duplicate-type collision.
    /// Fired when properties like ChildCount are updated (e.g. when a GTK app adds a menu bar).
    /// </summary>
    Task<IDisposable> WatchPropertiesChangedAsync(
        Action<PropertyChanges> handler,
        Action<Exception>? onError = null);
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

/// <summary>
/// org.a11y.atspi.Registry — the AT-SPI registry daemon.
/// Used to register event listeners so the daemon pushes events to us
/// instead of requiring polls.
/// </summary>
[DBusInterface("org.a11y.atspi.Registry")]
public interface IAtSpiRegistry : IDBusObject
{
    /// <summary>
    /// Register to receive AT-SPI events matching <paramref name="eventType"/>.
    /// Example: "Window:Activate" or "Window:" (all window events).
    /// </summary>
    Task RegisterEventListenerAsync(string eventType);

    /// <summary>Remove a previously registered event listener.</summary>
    Task DeregisterEventListenerAsync(string eventType);
}

/// <summary>
/// org.a11y.atspi.Event.Window — signal interface for AT-SPI window events.
/// KDE/Qt emits "Activate" events when a top-level window gains focus.
/// The signal sender's unique D-Bus bus name identifies the application.
/// Body: (string detail, int detail2, (int,int) anyData, string srcAppName).
/// </summary>
[DBusInterface("org.a11y.atspi.Event.Window")]
public interface IAtSpiWindowEvent : IDBusObject
{
    /// <summary>
    /// Subscribes to the <c>Activate</c> signal emitted when a window gains focus.
    /// The handler receives the signal body; use the connection's sender resolution
    /// to obtain the originating unique bus name.
    /// </summary>
    Task<IDisposable> WatchActivateAsync(
        Action<(string Detail, int Detail2, (int V1, int V2) AnyData, string SrcApp)> handler,
        Action<Exception>? onError = null);
}

/// <summary>
/// org.gtk.Menus — GMenuModel D-Bus transport interface exported by GTK3/4 GtkApplication windows.
///
/// GTK3 apps (e.g. HandBrake) that use GtkApplication.set_menubar() export their menu bar here
/// when a com.canonical.AppMenu.Registrar is present on the session bus.  The app hides the in-app
/// GtkMenuBar and makes the model available for the global menu to consume.
///
/// The protocol uses a subscription/section model:
///   • Subscription IDs identify logical menu trees (root = 0, each submenu = its own ID).
///   • Start([id, ...]) subscribes to those IDs and returns the current items as sections.
///   • Each section is (subscription_id, section_index, [{item dict}, ...]).
///   • Item dictionaries contain "label", ":submenu" (uu), ":section" (uu), "action", "accel", etc.
///   • End([id, ...]) unsubscribes when no longer needed.
/// </summary>
[DBusInterface("org.gtk.Menus")]
public interface IGtkMenus : IDBusObject
{
    /// <summary>
    /// Subscribe to the given subscription IDs and return their current section contents.
    /// Pass [0] to read the root menu bar.  Each ":submenu" item in the returned data will
    /// reference a new subscription ID; call Start again with those IDs to walk the tree.
    /// Returns a(uuaa{sv}) — array of (subscription_id, section_index, item_array).
    /// </summary>
    Task<(uint SubId, uint Section, IDictionary<string, object>[] Items)[]> StartAsync(uint[] subscriptionIds);

    /// <summary>Unsubscribes from the given subscription IDs and releases server-side resources.</summary>
    Task EndAsync(uint[] subscriptionIds);

    /// <summary>Fired when any subscribed section changes.</summary>
    Task<IDisposable> WatchChangedAsync(Action handler, Action<Exception>? onError = null);
}

/// <summary>
/// org.gtk.Actions — GAction dispatch interface exported alongside org.gtk.Menus.
/// Used to activate menu items by their action name.
///
/// The "action" field in GMenuModel items is a prefixed action name:
///   "app.<name>"    → activate on the GtkApplication (this D-Bus path)
///   "win.<name>"    → activate on the active GtkApplicationWindow
///   "unity.<name>"  → activate on the appmenu-gtk-module Unity action group
/// </summary>
[DBusInterface("org.gtk.Actions")]
public interface IGtkActions : IDBusObject
{
    /// <summary>
    /// Activates action <paramref name="actionName"/> with optional typed target
    /// and platform hint dictionary.  Pass empty arrays for both to activate a
    /// parameterless action.
    /// </summary>
    Task ActivateAsync(string actionName, object[] targetValue, IDictionary<string, object> platformData);
}

/// <summary>
/// org.a11y.Status — session-bus service provided by at-spi-bus-launcher.
/// Controls whether Qt/GTK apps load their AT-SPI accessibility bridge.
///   IsEnabled = true  → all running apps load their AT-SPI bridge (same as Orca starting)
///   IsEnabled = false → apps skip AT-SPI registration (default on most KDE desktops)
/// Setting IsEnabled = true causes PropertiesChanged to broadcast to all apps, which in
/// turn causes Qt's QSpiAccessibilityBridge to activate and register on the AT-SPI bus.
///
/// IMPORTANT: Tmds.DBus 0.x requires SetAsync(string, object) — non-generic — for the
/// Properties.Set call. Using SetAsync&lt;T&gt; causes "Cannot (de)serialize Type ''" because
/// Tmds.DBus cannot resolve the generic type parameter at runtime.
/// </summary>
[DBusInterface("org.a11y.Status")]
public interface IAtSpiStatus : IDBusObject
{
    /// <summary>Reads a property via org.freedesktop.DBus.Properties.Get.</summary>
    Task<T> GetAsync<T>(string prop);
    /// <summary>
    /// Writes a property via org.freedesktop.DBus.Properties.Set.
    /// Must use non-generic <c>object</c> — Tmds.DBus 0.x only recognises this exact signature.
    /// </summary>
    Task SetAsync(string prop, object value);
}
