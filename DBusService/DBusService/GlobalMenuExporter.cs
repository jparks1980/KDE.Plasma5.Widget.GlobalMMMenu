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

    // AT-SPI items don't have integer IDs — we assign synthetic ones and map back to path.
    // Key = synthetic int ID (>= 1), Value = (atspiBusName, objectPath)
    private readonly Dictionary<int, (string BusName, string Path)> _atspiIdMap = new();

    public ObjectPath ObjectPath => new("/com/kde/GlobalMMMenu");

    public Task<string> GetActiveMenuJsonAsync() => Task.FromResult(_menuJson);

    public async Task ExecuteItemAsync(int itemId)
    {
        // AT-SPI items: look up the stored bus+path and call DoAction(0).
        if (_atspiReader != null && _atspiIdMap.TryGetValue(itemId, out var entry))
        {
            await _atspiReader.ExecuteItemAsync(entry.BusName, entry.Path, CancellationToken.None);
            return;
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
        _atspiIdMap.Clear();
    }

    /// <summary>
    /// Stores an AT-SPI menu. The JSON must contain "id", "atspi-bus", "atspi-path"
    /// fields on every leaf node — these are used for execution routing.
    /// </summary>
    public void UpdateAtSpi(string menuJson, AtSpiMenuReader reader, Dictionary<int, (string BusName, string Path)> idMap)
    {
        _menuJson    = menuJson;
        _activeMenu  = null;
        _atspiReader = reader;
        _atspiIdMap.Clear();
        foreach (var (k, v) in idMap)
            _atspiIdMap[k] = v;
    }
}
