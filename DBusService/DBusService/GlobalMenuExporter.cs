using System.Collections.Concurrent;
using System.Text.Json;
using DBusService.DBus;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<GlobalMenuExporter> _logger;

    public GlobalMenuExporter(ILogger<GlobalMenuExporter> logger)
    {
        _logger = logger;
    }

    private volatile string    _menuJson       = "{}";
    private volatile string    _activeMenuPath = "?";
    private volatile bool      _menuInteractionFrozen = false;
    private volatile uint      _activeWindowId = 0;
    private volatile string    _activeAtSpiBus = "";

    /// <summary>When true, appends a [Debug: ...] item to every served menu. Set from config at startup.</summary>
    public bool ShowDebugMenu { get; set; } = false;
    private          IDbusMenu? _activeMenu = null;
    private          AtSpiMenuReader? _atspiReader = null;
    private          GtkMenuReader?   _gtkReader   = null;
    private          Connection?      _sessionConn = null;

    // AT-SPI / GtkMenu items don't have integer IDs — we assign synthetic ones and map back to path.
    // Key = synthetic int ID (>= 1), Value = (atspiBusName/sessionBusName, objectPath/encodedAction)
    // ConcurrentDictionary: Update* methods write from background Task.Run threads; ExecuteItemAsync
    // reads from the D-Bus dispatch thread. A plain Dictionary would race on Clear()+repopulate.
    private readonly ConcurrentDictionary<int, (string BusName, string Path)> _atspiIdMap = new();

    public ObjectPath ObjectPath => new("/com/kde/GlobalMMMenu");

    public Task<string> GetActiveMenuJsonAsync()
    {
        var json = _menuJson;
        if (json == "{}" || string.IsNullOrEmpty(json))
            return Task.FromResult(json);
        try
        {
            if (!ShowDebugMenu)
                return Task.FromResult(json);

            // Inject a [Debug] top-level menu to verify each window has its own distinct menu.
            using var doc   = JsonDocument.Parse(json);
            var root        = doc.RootElement;
            if (!root.TryGetProperty("children", out var children))
                return Task.FromResult(json);

            var debugLabel = $"[Debug: wid=0x{_activeWindowId:X8} path={_activeMenuPath} bus={_activeAtSpiBus}]";
            var debugItem  = $"{{\"id\":99999,\"label\":\"{debugLabel}\",\"enabled\":false,\"children\":[]}}";

            // Rebuild children array with debug item appended.
            var childrenJson = children.GetRawText(); // e.g. [{...},{...}]
            var merged = childrenJson.TrimEnd(']') + "," + debugItem + "]";

            // Replace the children array in the root JSON.
            var rootRaw  = root.GetRawText();
            var childRaw = children.GetRawText();
            var idx      = rootRaw.LastIndexOf(childRaw, StringComparison.Ordinal);
            string result;
            if (idx >= 0)
                result = rootRaw[..idx] + merged + rootRaw[(idx + childRaw.Length)..];
            else
                result = json;
            return Task.FromResult(result);
        }
        catch
        {
            return Task.FromResult(json);
        }
    }

    public async Task ExecuteItemAsync(int itemId)
    {
        if (_atspiIdMap.TryGetValue(itemId, out var entry))
        {
            // dbusmenu-routed items: AT-SPI synthetic ID was remapped to a dbusmenu item ID
            // during MergeDbusIconsIntoAtSpiJson so that execution goes through the per-window
            // dbusmenu proxy (_activeMenu) instead of AT-SPI.  This fixes multi-window
            // Brave/Chromium: AT-SPI window selection is unreliable on Wayland (frame ID ≠
            // client ID), but dbusmenu paths are correctly assigned per-window.
            if (entry.BusName == "dbusmenu" && int.TryParse(entry.Path, out var dbusItemId))
            {
                var dbMenu = _activeMenu;
                if (dbMenu is null) return;
                _logger.LogInformation("  ExecuteItem({AId}) → dbusmenu routing (item {DId}, path={P})", itemId, dbusItemId, _activeMenuPath);
                _menuInteractionFrozen = false;
                await dbMenu.EventAsync(dbusItemId, "clicked", (int)0, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                return;
            }
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
        _logger.LogInformation("  ExecuteItem({Id}) → dbusmenu path={P}", itemId, _activeMenuPath);
        _menuInteractionFrozen = false;
        await menu.EventAsync(itemId, "clicked", (int)0, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>
    /// Called by the C++ plugin just before a native popup menu opens (menuOpen=true)
    /// and after it closes (menuOpen=false).
    /// While frozen, <see cref="Update"/> will not overwrite <see cref="_activeMenu"/> so
    /// transient Wayland focus changes (e.g. Brave re-focusing a different internal window
    /// when the panel grabs input) cannot redirect the click to the wrong application window.
    /// </summary>
    public Task SetMenuOpenAsync(bool menuOpen)
    {
        if (menuOpen)
        {
            _menuInteractionFrozen = true;
            _logger.LogInformation("  Menu interaction frozen (popup opened, active path={P})", _activeMenuPath);
        }
        else
        {
            _menuInteractionFrozen = false;
            _logger.LogInformation("  Menu interaction unfrozen (popup closed)");
        }
        return Task.CompletedTask;
    }

    /// <summary>The most recent JSON passed to <see cref="Update"/>. Empty object <c>{}</c> when no menu is active.</summary>
    public string LastMenuJson => _menuJson;

    /// <summary>Replaces the stored menu JSON and active dbusmenu proxy. Clears AT-SPI state.</summary>
    public void Update(string menuJson, IDbusMenu? menu = null, string? activePath = null, uint windowId = 0)
    {
        // Always update the JSON (display) but don't overwrite the active proxy while the popup
        // menu is open — Brave re-focuses its last-used internal window when the panel grabs
        // Wayland input, which would redirect the pending click to the wrong window.
        _menuJson = menuJson;
        if (!_menuInteractionFrozen)
        {
            _activeMenu     = menu;
            _activeMenuPath = activePath ?? "?";
            if (windowId != 0) _activeWindowId = windowId;  // don't reset to 0 on blank update
            if (menu == null) _activeAtSpiBus = "";          // only clear bus on blank/reset update
            _atspiReader    = null;
            _gtkReader      = null;
            _sessionConn    = null;
            _atspiIdMap.Clear();
        }
    }


    /// <summary>Stores a GtkMenu (org.gtk.Menus) menu for execution routing.</summary>
    public void UpdateGtkMenu(string menuJson, GtkMenuReader reader, Connection sessionConnection,
        Dictionary<int, (string BusName, string Path)> idMap, uint windowId = 0)
    {
        _menuJson = menuJson;
        if (!_menuInteractionFrozen)
        {
            _activeMenu  = null;
            _atspiReader = null;
            _gtkReader   = reader;
            _sessionConn = sessionConnection;
            _activeWindowId = windowId;
            _activeAtSpiBus = "";
            _activeMenuPath = "gtk";
            _atspiIdMap.Clear();
            foreach (var (k, v) in idMap)
                _atspiIdMap.TryAdd(k, v);
        }
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
        Dictionary<int, (string BusName, string Path)> idMap, IDbusMenu? dbusMenu = null,
        uint windowId = 0, string atSpiBus = "")
    {
        // Always update the JSON (display) but — exactly like Update() — do not overwrite the
        // active proxy or idMap while the popup is open.  On Wayland, opening the panel popup
        // causes KWin to re-fire clientActivated for another Brave window (same PID), which
        // triggers UpdateAtSpi() from the Worker.  Without this guard that call would overwrite
        // _activeMenu and _atspiIdMap with the wrong window's data before the user clicks.
        _menuJson = menuJson;
        if (!_menuInteractionFrozen)
        {
            _activeMenu     = dbusMenu;
            _atspiReader    = reader;
            _gtkReader      = null;
            _sessionConn    = null;
            if (windowId != 0) _activeWindowId = windowId;
            if (atSpiBus != "") _activeAtSpiBus = atSpiBus;
            _atspiIdMap.Clear();
            foreach (var (k, v) in idMap)
                _atspiIdMap.TryAdd(k, v);
        }
    }

    /// <summary>
    /// Sets the debug context (window ID + AT-SPI bus) used by the [Debug] menu item.
    /// Call once per focus event before any Update*/UpdateAtSpi calls so every subsequent
    /// update automatically picks up the correct window context — avoids threading windowId
    /// through all 16 UpdateAtSpi call sites.
    /// </summary>
    public void SetDebugContext(uint windowId, string atSpiBus = "")
    {
        _activeWindowId = windowId;
        _activeAtSpiBus = atSpiBus;
    }
}
