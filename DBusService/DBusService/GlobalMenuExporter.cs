using DBusService.DBus;
using Tmds.DBus;

namespace DBusService;

/// <summary>
/// Implements the <see cref="IGlobalMenuService"/> D-Bus object.
/// The Worker registers this on the session bus and calls <see cref="Update"/>
/// whenever the active window's menu changes.
/// Thread-safe: volatile field for lock-free reads from the D-Bus dispatch thread.
/// </summary>
public class GlobalMenuExporter : IGlobalMenuService
{
    private volatile string    _menuJson   = "{}";
    private          IDbusMenu? _activeMenu = null;
    private          AtSpiMenuReader? _atspiReader = null;
    private          GtkMenuReader?   _gtkReader   = null;
    private          Connection?      _sessionConn = null;

    // AT-SPI / GtkMenu items don't have integer IDs — we assign synthetic ones and map back to path.
    // Key = synthetic int ID (>= 1), Value = (atspiBusName/sessionBusName, objectPath/encodedAction)
    private readonly Dictionary<int, (string BusName, string Path)> _atspiIdMap = new();

    public ObjectPath ObjectPath => new("/com/kde/GlobalMMMenu");

    public Task<string> GetActiveMenuJsonAsync() => Task.FromResult(_menuJson);

    public async Task ExecuteItemAsync(int itemId)
    {
        if (_atspiIdMap.TryGetValue(itemId, out var entry))
        {
            // GtkMenu items: path is encoded as "{actionsBasePath}|{actionName}".
            if (_gtkReader != null && _sessionConn != null && entry.Path.Contains('|'))
            {
                await _gtkReader.ExecuteItemAsync(_sessionConn, entry.BusName, entry.Path, CancellationToken.None);
                return;
            }
            // AT-SPI items: look up the stored bus+path and call DoAction(0).
            if (_atspiReader != null)
            {
                await _atspiReader.ExecuteItemAsync(entry.BusName, entry.Path, CancellationToken.None);
                return;
            }
        }

        // dbusmenu items: send "clicked" event.
        var menu = _activeMenu;
        if (menu is null) return;
        await menu.EventAsync(itemId, "clicked", (int)0, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>The most recent JSON passed to <see cref="Update"/>. Empty object <c>{}</c> when no menu is active.</summary>
    public string LastMenuJson => _menuJson;

    /// <summary>Replaces the stored menu JSON and active dbusmenu proxy. Clears AT-SPI state.</summary>
    public void Update(string menuJson, IDbusMenu? menu = null)
    {
        _menuJson    = menuJson;
        _activeMenu  = menu;
        _atspiReader = null;
        _gtkReader   = null;
        _sessionConn = null;
        _atspiIdMap.Clear();
    }

    /// <summary>Stores a GtkMenu (org.gtk.Menus) menu for execution routing.</summary>
    public void UpdateGtkMenu(string menuJson, GtkMenuReader reader, Connection sessionConnection,
        Dictionary<int, (string BusName, string Path)> idMap)
    {
        _menuJson    = menuJson;
        _activeMenu  = null;
        _atspiReader = null;
        _gtkReader   = reader;
        _sessionConn = sessionConnection;
        _atspiIdMap.Clear();
        foreach (var (k, v) in idMap)
            _atspiIdMap[k] = v;
    }

    /// <summary>
    /// Stores an AT-SPI menu. The JSON must contain "id", "atspi-bus", "atspi-path"
    /// fields on every leaf node — these are used for execution routing.
    /// <para>
    /// <paramref name="dbusMenu"/> is optional. When supplied (after a DBus icon-merge pass
    /// that may have injected DBus submenu children), item IDs that are NOT in
    /// <paramref name="idMap"/> will be routed through the DBus proxy instead of AT-SPI.
    /// This handles cases where Qt's AT-SPI bridge did not populate a lazy submenu
    /// (e.g. Dolphin's "Create New") — those children come straight from the DBus layout
    /// and must be executed via <c>EventAsync</c>.
    /// </para>
    /// </summary>
    public void UpdateAtSpi(string menuJson, AtSpiMenuReader reader,
        Dictionary<int, (string BusName, string Path)> idMap, IDbusMenu? dbusMenu = null)
    {
        _menuJson    = menuJson;
        _activeMenu  = dbusMenu;
        _atspiReader = reader;
        _atspiIdMap.Clear();
        foreach (var (k, v) in idMap)
            _atspiIdMap[k] = v;
    }
}
