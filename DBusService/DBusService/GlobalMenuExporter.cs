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

    public ObjectPath ObjectPath => new("/com/kde/GlobalMMMenu");

    public Task<string> GetActiveMenuJsonAsync() => Task.FromResult(_menuJson);

    public async Task ExecuteItemAsync(int itemId)
    {
        var menu = _activeMenu;
        if (menu is null) return;
        // Send a "clicked" event to the application. data=0 (int32) per spec.
        await menu.EventAsync(itemId, "clicked", (int)0, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>Replaces the stored menu JSON and active proxy. Called from the Worker on every menu update.</summary>
    public void Update(string menuJson, IDbusMenu? menu = null)
    {
        _menuJson   = menuJson;
        _activeMenu = menu;
    }
}
