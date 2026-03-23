using System.Diagnostics;
using System.Text.Json;
using DBusService.DBus;
using Tmds.DBus;

namespace DBusService;

/// <summary>
/// Reads the menu bar of any application via the AT-SPI2 accessibility bus.
/// This works for ALL Qt/KDE apps regardless of whether dbusmenu was initialized,
/// because Qt always exports the full UI tree to AT-SPI unconditionally.
///
/// Usage:
///   var reader = new AtSpiMenuReader(logger);
///   await reader.ConnectAsync();
///   var json = await reader.GetMenuJsonForPidAsync(pid, cancellationToken);
///   var id   = await reader.ExecuteItemAsync(pid, itemPath, cancellationToken);
/// </summary>
public sealed class AtSpiMenuReader(ILogger logger) : IAsyncDisposable
{
    // AT-SPI role constants (subset used here)
    private const uint RoleMenuBar  = 34;
    private const uint RoleMenu     = 33;
    private const uint RoleMenuItem = 35;
    private const uint RoleSeparator = 50;
    private const uint RoleCheckMenuItem  = 8;
    private const uint RoleRadioMenuItem  = 45;
    private const uint RoleTearOffMenuItem = 59;
    private const uint RolePopupMenu = 41;

    // AT-SPI2 StateType bit positions — each enum value N occupies bit N in the 64-bit
    // state bitfield (word 0 = bits 0-31, word 1 = bits 32-63).
    // ENABLED=8, SENSITIVE=24 (not greyed-out), CHECKED=4, VISIBLE=30, SHOWING=25
    private const uint StateEnabled   = 1u << 8;   // ATSPI_STATE_ENABLED
    private const uint StateSensitive = 1u << 24;  // ATSPI_STATE_SENSITIVE — Qt sets this for non-disabled items
    private const uint StateChecked   = 1u << 4;   // ATSPI_STATE_CHECKED
    private const uint StateVisible   = 1u << 30;  // ATSPI_STATE_VISIBLE

    private Connection? _atspiConnection;
    private IAtSpiDBusDaemon? _atspiBusDaemon;

    // When true, also fetch icon-name and shortcut per node via AT-SPI Image/Action interfaces.
    // Adds ~2 extra D-Bus round-trips per non-separator node; leave false unless debugging.
    public bool RichMetadata { get; set; } = false;

    // Resolved per-session AT-SPI bus address (changes per login).
    private string? _atspiAddress;

    /// <summary>Connects to the session bus, fetches the AT-SPI socket address, and opens a connection to it.</summary>
    public async Task<bool> ConnectAsync()
    {
        try
        {
            // First get the AT-SPI bus address from the well-known session bus service.
            using var sessionConn = new Connection(Address.Session!);
            await sessionConn.ConnectAsync();
            var launcher = sessionConn.CreateProxy<IAtSpiLauncher>("org.a11y.Bus", new ObjectPath("/org/a11y/bus"));
            _atspiAddress = await launcher.GetAddressAsync();

            // Open a raw connection to the AT-SPI socket.
            _atspiConnection = new Connection(_atspiAddress);
            await _atspiConnection.ConnectAsync();

            // Also open a proxy to the daemon on that bus so we can look up PIDs.
            // Use IAtSpiDBusDaemon (not IFreedesktopDBus) to avoid a Tmds.DBus
            // "Duplicate type name" collision — IFreedesktopDBus is already registered
            // against the session bus connection in Worker.
            _atspiBusDaemon = _atspiConnection.CreateProxy<IAtSpiDBusDaemon>(
                "org.freedesktop.DBus", "/org/freedesktop/DBus");

            logger.LogInformation("[AT-SPI] Connected to AT-SPI bus at {Addr}", _atspiAddress);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning("[AT-SPI] Could not connect to AT-SPI bus: {M}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Builds a menu JSON string for the window belonging to the given OS process ID.
    /// Also returns an ID→(busName,path) map so the exporter can route ExecuteItem calls.
    /// Returns (null, empty) if the app has no menu bar or is not registered on AT-SPI.
    /// </summary>
    public async Task<(string? Json, Dictionary<int, (string BusName, string Path)> IdMap)>
        GetMenuJsonForPidAsync(uint pid, CancellationToken cancellationToken)
    {
        var empty = (Json: (string?)null, IdMap: new Dictionary<int, (string, string)>());
        if (_atspiConnection == null || _atspiBusDaemon == null)
            return empty;

        // ── Find the AT-SPI connection whose OS PID matches ──────────────────
        string? appBusName = null;
        try
        {
            var names = await _atspiBusDaemon.ListNamesAsync();
            foreach (var name in names)
            {
                if (cancellationToken.IsCancellationRequested) return empty;
                if (!name.StartsWith(':')) continue;
                try
                {
                    var connPid = await _atspiBusDaemon.GetConnectionUnixProcessIDAsync(name);
                    if (connPid == pid) { appBusName = name; break; }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("[AT-SPI] ListNames failed: {M}", ex.Message);
            return empty;
        }

        if (appBusName == null)
        {
            logger.LogDebug("[AT-SPI] No AT-SPI connection found for pid={P}", pid);
            return empty;
        }

        return await GetMenuJsonFromConnectionAsync(appBusName, cancellationToken);
    }

    /// <summary>
    /// Builds a menu JSON string directly from a known AT-SPI bus name.
    /// Walks: app root → window children → finds ROLE_MENU_BAR → serializes tree.
    /// Also returns the ID→(busName,path) map needed for execution routing.
    /// </summary>
    public async Task<(string? Json, Dictionary<int, (string BusName, string Path)> IdMap)>
        GetMenuJsonFromConnectionAsync(string atspiBusName, CancellationToken cancellationToken)
    {
        var empty = (Json: (string?)null, IdMap: new Dictionary<int, (string, string)>());
        if (_atspiConnection == null) return empty;

        try
        {
            var appRoot = _atspiConnection.CreateProxy<IAtSpiAccessible>(
                atspiBusName, new ObjectPath("/org/a11y/atspi/accessible/root"));

            var windows = await appRoot.GetChildrenAsync();
            foreach (var (winBus, winPath) in windows)
            {
                if (cancellationToken.IsCancellationRequested) return empty;
                var winAcc = _atspiConnection.CreateProxy<IAtSpiAccessible>(winBus, winPath);
                var winChildren = await winAcc.GetChildrenAsync();

                foreach (var (cBus, cPath) in winChildren)
                {
                    if (cancellationToken.IsCancellationRequested) return empty;
                    var cAcc = _atspiConnection.CreateProxy<IAtSpiAccessible>(cBus, cPath);
                    if (await cAcc.GetRoleAsync() != RoleMenuBar) continue;

                    // Found the menu bar — build JSON + id map
                    var idMap    = new Dictionary<int, (string BusName, string Path)>();
                    var counter  = new IdCounter();
                    var rootNode = await BuildMenuBarNodeAsync(cBus, cPath, idMap, counter, cancellationToken);
                    if (rootNode == null) continue;

                    var json = JsonSerializer.Serialize(rootNode, new JsonSerializerOptions { WriteIndented = true });
                    logger.LogDebug("[AT-SPI] Built menu JSON for {Bus} ({Len} chars, {N} items)", atspiBusName, json.Length, idMap.Count);
                    return (json, idMap);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("[AT-SPI] GetMenuJson failed for {Bus}: {M}", atspiBusName, ex.Message);
        }
        return empty;
    }

    /// <summary>
    /// Executes the menu item at the given AT-SPI object path (calls DoAction(0)).
    /// The path is the string stored in the JSON node's "atspi-path" field.
    /// </summary>
    public async Task ExecuteItemAsync(string atspiBusName, string objectPath, CancellationToken cancellationToken)
    {
        if (_atspiConnection == null) return;
        try
        {
            var action = _atspiConnection.CreateProxy<IAtSpiAction>(
                atspiBusName, new ObjectPath(objectPath));
            await action.DoActionAsync(0);
            logger.LogDebug("[AT-SPI] Executed item at {Path}", objectPath);
        }
        catch (Exception ex)
        {
            logger.LogDebug("[AT-SPI] Execute failed at {Path}: {M}", objectPath, ex.Message);
        }
    }

    // ── Private tree-building helpers ─────────────────────────────────────────

    // Wraps a mutable int counter — async methods can't have ref parameters.
    private sealed class IdCounter { public int Value = 1; }

    private async Task<Dictionary<string, object?>?> BuildMenuBarNodeAsync(
        string busName, ObjectPath barPath,
        Dictionary<int, (string BusName, string Path)> idMap,
        IdCounter counter, CancellationToken ct)
    {
        var acc = _atspiConnection!.CreateProxy<IAtSpiAccessible>(busName, barPath);
        var topMenus = await acc.GetChildrenAsync();
        logger.LogDebug("[AT-SPI] MenuBar at {Bar} has {Count} top-level items", barPath, topMenus.Length);

        var children = new List<object?>();
        foreach (var (mBus, mPath) in topMenus)
        {
            // Skip CT check when debugger is attached — pausing the debugger causes the
            // timeout to fire and cancels the scan before all nodes are visited.
            if (!Debugger.IsAttached && ct.IsCancellationRequested) break;
            var node = await BuildMenuNodeAsync(mBus, mPath, idMap, counter, ct);
            if (node != null) children.Add(node);
        }

        return new Dictionary<string, object?>
        {
            ["id"]       = 0,
            ["label"]    = "Root",
            ["children"] = children,
        };
    }

    private async Task<Dictionary<string, object?>?> BuildMenuNodeAsync(
        string busName, ObjectPath path,
        Dictionary<int, (string BusName, string Path)> idMap,
        IdCounter counter, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;

        IAtSpiAccessible acc;
        uint nodeRole;
        uint[] stateWords;
        string label;
        try
        {
            acc = _atspiConnection!.CreateProxy<IAtSpiAccessible>(busName, path);
            // Fetch role, state, and name in parallel — 1 round-trip batch instead of 3 sequential.
            var roleTask  = acc.GetRoleAsync();
            var stateTask = acc.GetStateAsync();
            var nameTask  = acc.GetAsync<string>("Name");
            await Task.WhenAll(roleTask, stateTask, nameTask);
            nodeRole   = roleTask.Result;
            stateWords = stateTask.Result ?? [];
            label      = nameTask.Result ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogTrace("[AT-SPI] BuildMenuNode init failed for {Path}: {T}: {M}", path, ex.GetType().Name, ex.Message);
            return null;
        }

        if (nodeRole == RoleTearOffMenuItem) return null;

        // Qt sets SENSITIVE (bit 24) for items that are not greyed out.
        // ENABLED (bit 8) may also be set; check both.
        bool enabled  = stateWords.Length > 0 && ((stateWords[0] & StateSensitive) != 0 || (stateWords[0] & StateEnabled) != 0);
        bool checked_ = stateWords.Length > 0 && (stateWords[0] & StateChecked) != 0;
        logger.LogTrace("[AT-SPI] node '{L}' role={R} stateW0=0x{S:X8} enabled={E}", label, nodeRole, stateWords.Length > 0 ? stateWords[0] : 0, enabled);
        bool isSep    = nodeRole == RoleSeparator || string.IsNullOrEmpty(label);

        int myId = counter.Value++;
        idMap[myId] = (busName, path.ToString());

        var node = new Dictionary<string, object?>
        {
            ["id"]      = myId,
            ["label"]   = isSep ? null : label,
            ["enabled"] = enabled,
        };

        if (isSep)
        {
            node["type"] = "separator";
            return node;
        }

        // ── Icon name + keyboard shortcut (optional, gated by RichMetadata config) ──
        // Adds ~2 parallel D-Bus round-trips per node. Disabled by default because
        // GetImageDescriptionAsync calls a D-Bus property (not a method) and may be slow.
        if (RichMetadata)
        {
            try
            {
                var imgProxy    = _atspiConnection!.CreateProxy<IAtSpiImage>(busName, path);
                var actionProxy = _atspiConnection!.CreateProxy<IAtSpiAction>(busName, path);
                var iconTask    = imgProxy.GetImageDescriptionAsync();
                var keyTask     = actionProxy.GetKeyBindingAsync(0);
                await Task.WhenAll(iconTask, keyTask);
                if (!string.IsNullOrEmpty(iconTask.Result))
                    node["icon-name"] = iconTask.Result;
                var shortcut = ParseAtSpiKeyBinding(keyTask.Result);
                if (shortcut != null)
                    node["shortcut"] = shortcut;
            }
            catch { /* interfaces not present on this node */ }
        }

        if (nodeRole == RoleCheckMenuItem)
        {
            node["toggle-type"]  = "checkmark";
            node["toggle-state"] = checked_ ? 1 : 0;
        }
        else if (nodeRole == RoleRadioMenuItem)
        {
            node["toggle-type"]  = "radio";
            node["toggle-state"] = checked_ ? 1 : 0;
        }

        // Fetch children for any role that can be a submenu: MENU, MENU_BAR, or MENU_ITEM.
        // Qt/KDE apps sometimes report top-level bar entries (File, Edit…) as RoleMenuItem (35)
        // instead of RoleMenu (33), so we must attempt child expansion for all three roles.
        if (nodeRole == RoleMenu || nodeRole == RoleMenuBar || nodeRole == RoleMenuItem)
        {
            try
            {
                var menuChildren = await acc.GetChildrenAsync();
                logger.LogTrace("[AT-SPI]     '{Label}' (role={R}) has {C} children", label, nodeRole, menuChildren.Length);

                // Qt wraps the actual items inside a single POPUP_MENU (role=41) child.
                // Flatten it so the items appear as direct children of this node.
                if (menuChildren.Length == 1)
                {
                    var (pmBus, pmPath) = menuChildren[0];
                    var pmAcc = _atspiConnection!.CreateProxy<IAtSpiAccessible>(pmBus, pmPath);
                    if (await pmAcc.GetRoleAsync() == RolePopupMenu)
                    {
                        menuChildren = await pmAcc.GetChildrenAsync();
                        logger.LogTrace("[AT-SPI]     '{Label}' POPUP_MENU wrapper flattened → {C} real children", label, menuChildren.Length);
                    }
                }
                var kids = new List<object?>();
                for (int ci = 0; ci < menuChildren.Length; ci++)
                {
                    if (!Debugger.IsAttached && ct.IsCancellationRequested) break;
                    var (cBus, cPath) = menuChildren[ci];
                    var child = await BuildMenuNodeAsync(cBus, cPath, idMap, counter, ct);
                    if (child != null) kids.Add(child);
                }
                if (kids.Count > 0)
                    node["children"] = kids;
                else
                    logger.LogTrace("[AT-SPI]     '{Label}' expanded but all {C} children returned null/empty", label, menuChildren.Length);
            }
            catch (Exception ex) { logger.LogTrace("[AT-SPI]     '{Label}' GetChildren threw: {M}", label, ex.Message); }
        }

        return node;
    }

    /// <summary>
    /// Converts an AT-SPI key binding string (e.g. "Ctrl+Shift+N" or "Ctrl+N;N")
    /// into the DBusMenu shortcut format expected by the C++ plugin: [["Control","Shift","N"]].
    /// Returns null if the string is empty or unparseable.
    /// </summary>
    private static object[][]? ParseAtSpiKeyBinding(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // AT-SPI may return semicolon-separated alternatives; use the first.
        var first = raw.Split(';')[0].Trim();
        if (string.IsNullOrEmpty(first)) return null;

        var parts = first.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var combo = new List<string>();
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i].Trim();
            if (i < parts.Length - 1) // modifier
            {
                combo.Add(p switch
                {
                    "Ctrl"  or "ctrl"  => "Control",
                    "Shift" or "shift" => "Shift",
                    "Alt"   or "alt"   => "Alt",
                    "Meta"  or "meta" or "Win" or "Super" => "Super",
                    _ => p
                });
            }
            else // key
            {
                combo.Add(p);
            }
        }
        return combo.Count > 0 ? new[] { combo.ToArray() } : null;
    }

    private async Task<string> GetNodeNameAsync(string busName, ObjectPath path)
    {
        try
        {
            // Use IAtSpiAccessible.GetAsync<T> — Tmds.DBus 0.x built-in property accessor.
            // This calls org.freedesktop.DBus.Properties.Get internally without generating
            // a separate Properties proxy class, avoiding the "Duplicate type name" collision
            // that occurs when two C# interfaces share the same [DBusInterface] attribute value.
            var acc  = _atspiConnection!.CreateProxy<IAtSpiAccessible>(busName, path);
            var name = await acc.GetAsync<string>("Name");
            logger.LogTrace("[AT-SPI] GetName({P}) → {V}", path, name);
            return name ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogDebug("[AT-SPI] GetName({P}) threw {T}: {M}", path, ex.GetType().Name, ex.Message);
            return string.Empty;
        }
    }

    public ValueTask DisposeAsync()
    {
        _atspiConnection?.Dispose();
        _atspiConnection = null;
        return ValueTask.CompletedTask;
    }
}
