using System.Text.Json;
using DBusService.DBus;
using Tmds.DBus;

namespace DBusService;

/// <summary>
/// Reads the menu model exported by GTK3/4 GtkApplication windows via <c>org.gtk.Menus</c>.
///
/// Background
/// ──────────
/// When <c>com.canonical.AppMenu.Registrar</c> becomes available on the session bus, GTK3 sets
/// <c>gtk-shell-shows-menubar = FALSE</c>, causing GtkApplication-based apps to:
///   1. Hide their in-app GtkMenuBar widget (so AT-SPI sees one fewer child on the app node).
///   2. Export the menu model via <c>org.gtk.Menus</c> on their session-bus D-Bus connection.
///
/// This class reads that export and converts it to the same JSON format that
/// <see cref="AtSpiMenuReader"/> produces, so the rest of the service can consume it uniformly.
///
/// Discovery
/// ─────────
/// GTK3 exports <c>org.gtk.Menus</c> at two known paths on native Wayland:
///   • <c>/org/appmenu/gtk/window/menus/menubar</c>  — appmenu-gtk-module integration path.
///   • <c>/{app_id_as_dbus_path}/menus/menubar</c>   — GtkApplication canonical path.
/// We try the first path (constant across all apps), then discover the canonical path by
/// introspecting common sub-paths from the GtkApplication D-Bus export.
///
/// Execution
/// ─────────
/// Menu items carry an <c>"action"</c> attribute (prefixed action name, e.g. "app.quit" or
/// "unity.-File-0"). Activating them calls <c>org.gtk.Actions.Activate</c> on the app's
/// D-Bus connection — no AT-SPI DoAction needed.
/// </summary>
public sealed class GtkMenuReader(ILogger logger)
{
    // ── Well-known discovery paths ──────────────────────────────────────────────
    // appmenu-gtk-module exports here on Wayland (no X11 XID needed).
    private static readonly string[] KnownMenubarPaths =
    [
        "/org/appmenu/gtk/window/menus/menubar",
        "/org/appmenu/gtk/menus/menubar",
    ];

    // ── Public API ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the <c>org.gtk.Menus</c> endpoint for the given PID's session-bus connection,
    /// reads the full menu tree, and returns it as our standard JSON format + an ID map that
    /// carries the (sessionBusName, menubarPath, actionName) needed to activate items later.
    ///
    /// The &lt;paramref name="idMap"/&gt; maps each menu-item integer ID to a <c>GtkMenuAction</c>
    /// record (stored as a JSON-encoded string so it fits the existing
    /// <c>Dictionary&lt;int,(string BusName, string Path)&gt;</c> contract used by callers).
    /// Concretely, the Path field encodes the action name; BusName is the session-bus unique name.
    /// </summary>
    public async Task<(string? Json, Dictionary<int, (string BusName, string Path)> IdMap, string? SessionBusName)>
        GetMenuJsonForConnectionAsync(
            Connection sessionConnection,
            IFreedesktopDBus sessionDbus,
            uint pid,
            CancellationToken cancellationToken)
    {
        // ── PRODUCTION: discover + read org.gtk.Menus ────────────────────────
        // Discovery strategy (all attempts return fast failures for wrong paths):
        //   1. Known appmenu-gtk-module paths (fast fail if module not installed)
        //   2. Well-known D-Bus names owned by target PID → derive GtkApplication
        //      path: "fr.handbrake.ghb" → "/fr/handbrake/ghb/menus/menubar"
        //   3. C# Introspect at "/" on each matching connection (worked in scan
        //      even when busctl returned Access denied)
        //   4. Process name heuristics from /proc/{pid}/comm

        var empty = (Json: (string?)null, IdMap: new Dictionary<int, (string, string)>(), SessionBusName: (string?)null);

        string[] allNames;
        try { allNames = await sessionDbus.ListNamesAsync(); }
        catch (Exception ex) { logger.LogDebug("[GtkMenu] ListNames failed for pid={P}: {M}", pid, ex.Message); return empty; }

        var uniqueNames = allNames.Where(n => n.StartsWith(':')).ToArray();

        // ── Find unique connections owned by target PID ───────────────────────
        var targetConns = new List<string>();
        const int pidBatch = 20;
        for (int i = 0; i < uniqueNames.Length; i += pidBatch)
        {
            if (cancellationToken.IsCancellationRequested) return empty;
            var slice = uniqueNames.Skip(i).Take(pidBatch);
            var pidTasks = slice.Select(async n =>
            {
                try   { return (n, Pid: await sessionDbus.GetConnectionUnixProcessIDAsync(n)); }
                catch { return (n, Pid: 0u); }
            });
            foreach (var (n, p) in await Task.WhenAll(pidTasks))
                if (p == pid) targetConns.Add(n);
        }

        if (targetConns.Count == 0)
        {
            logger.LogDebug("[GtkMenu] No session bus connection found for pid={P}", pid);
            return empty;
        }

        // ── Find well-known names owned by target PID ─────────────────────────
        // GtkApplication registers a well-known name (e.g. "fr.handbrake.ghb").
        // Use GetNameOwnerAsync (resolves wk→unique name via dbus-daemon, no inter-process
        // call) and check the result against targetConns — much more reliable than
        // GetConnectionUnixProcessIDAsync on well-known names.
        var wkNames = allNames.Where(n => !n.StartsWith(':')).ToArray();
        var wkForPid = new List<string>();
        for (int i = 0; i < wkNames.Length; i += pidBatch)
        {
            if (cancellationToken.IsCancellationRequested) return empty;
            var slice = wkNames.Skip(i).Take(pidBatch);
            var wkTasks = slice.Select(async n =>
            {
                try
                {
                    var owner = await sessionDbus.GetNameOwnerAsync(n);
                    return (n, InTarget: targetConns.Contains(owner));
                }
                catch { return (n, InTarget: false); }
            });
            foreach (var (n, inTarget) in await Task.WhenAll(wkTasks))
                if (inTarget) wkForPid.Add(n);
        }
        if (wkForPid.Count > 0)
            logger.LogDebug("[GtkMenu] pid={P} owns well-known names: [{N}]", pid, string.Join(", ", wkForPid));

        // ── Build the ordered candidate path list ─────────────────────────────
        // Derive GtkApplication paths from well-known names.
        // "fr.handbrake.ghb" → "/fr/handbrake/ghb/menus/menubar"
        var derivedPaths = wkForPid
            .Select(wk => "/" + wk.Replace('.', '/') + "/menus/menubar")
            .ToArray();

        // Process comm name heuristic: /proc/{pid}/comm → exe basename
        string[] commPaths = Array.Empty<string>();
        try
        {
            var comm = System.IO.File.ReadAllText($"/proc/{pid}/comm").Trim();
            // Try exe name as the last component: /{prefix}/{comm}/menus/menubar for common prefixes
            commPaths = new[] { "fr", "io", "org", "com", "app", "net", "de" }
                .Select(tld => $"/{tld}/{comm}/menus/menubar")
                .ToArray();
            logger.LogDebug("[GtkMenu] pid={P} comm='{C}' → heuristic paths: [{H}]", pid, comm,
                string.Join(", ", commPaths.Take(3)) + "...");
        }
        catch { /* /proc read failed — continue */ }

        // ── Probe each connection with all candidate paths ──────────────────
        string? menuBusName = null;
        string? menuPath    = null;

        foreach (var conn in targetConns)
        {
            if (cancellationToken.IsCancellationRequested) break;

            // Priority: known module paths → derived well-known paths → introspect → comm heuristics
            var candidatePaths = KnownMenubarPaths.Concat(derivedPaths).Distinct().ToArray();
            foreach (var candidate in candidatePaths)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (await ProbeGtkMenusAsync(sessionConnection, conn, candidate, cancellationToken))
                {
                    menuBusName = conn;
                    menuPath    = candidate;
                    break;
                }
            }
            if (menuBusName != null) break;

            // Introspect-based discovery on the matching connection
            var discovered = await DiscoverGtkMenuBarPathAsync(sessionConnection, conn, cancellationToken);
            if (discovered != null)
            {
                menuBusName = conn;
                menuPath    = discovered;
                break;
            }

            // Comm-heuristic paths as last resort
            foreach (var candidate in commPaths)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (await ProbeGtkMenusAsync(sessionConnection, conn, candidate, cancellationToken))
                {
                    menuBusName = conn;
                    menuPath    = candidate;
                    break;
                }
            }
            if (menuBusName != null) break;
        }

        if (menuBusName == null || menuPath == null)
        {
            logger.LogDebug("[GtkMenu] No org.gtk.Menus endpoint found for pid={P}", pid);
            return empty;
        }

        logger.LogInformation("[GtkMenu] Found org.gtk.Menus for pid={P} on {Bus} at {Path}", pid, menuBusName, menuPath);

        // ── Read the full menu tree ───────────────────────────────────────────
        try
        {
            var idMap    = new Dictionary<int, (string BusName, string Path)>();
            var counter  = new IdCounter();
            var gtkMenus = sessionConnection.CreateProxy<IGtkMenus>(menuBusName, new ObjectPath(menuPath));
            var rootNode = await ReadMenuBarAsync(gtkMenus, menuBusName, menuPath, idMap, counter, cancellationToken);
            if (rootNode == null) return empty;

            var json = JsonSerializer.Serialize(rootNode, new JsonSerializerOptions { WriteIndented = true });
            logger.LogInformation("[GtkMenu] Built menu JSON for pid={P} ({Len} chars, {N} items)", pid, json.Length, idMap.Count);
            return (json, idMap, menuBusName);
        }
        catch (Exception ex)
        {
            logger.LogDebug("[GtkMenu] Build failed for pid={P}: {M}", pid, ex.Message);
            return empty;
        }
    }

    /// <summary>
    /// Executes a GtkMenu action stored in the idMap.  The Path field encodes the action name;
    /// BusName is the session-bus service that owns the org.gtk.Actions interface.
    /// </summary>
    public async Task ExecuteItemAsync(
        Connection sessionConnection,
        string busName,
        string encodedAction,
        CancellationToken cancellationToken)
    {
        // Decode "actionPath|actionName" stored in the Path field of the id map.
        var sep = encodedAction.IndexOf('|');
        if (sep < 0) return;
        var actionsPath = encodedAction[..sep];
        var actionName  = encodedAction[(sep + 1)..];

        try
        {
            var gtkActions = sessionConnection.CreateProxy<IGtkActions>(busName, new ObjectPath(actionsPath));
            await gtkActions.ActivateAsync(actionName, [], new Dictionary<string, object>());
            logger.LogDebug("[GtkMenu] Activated action '{A}' at {Bus}{P}", actionName, busName, actionsPath);
        }
        catch (Exception ex)
        {
            logger.LogDebug("[GtkMenu] Activate failed for '{A}': {M}", actionName, ex.Message);
        }
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    private sealed class IdCounter { public int Value = 1; }

    /// <summary>
    /// Tries calling <c>org.gtk.Menus.Start([0])</c> on the given path and returns true
    /// if the call succeeds (object exists and implements the interface).
    /// Uses Task.WhenAny for ALL calls to prevent hangs on unresponsive connections.
    /// </summary>
    private async Task<bool> ProbeGtkMenusAsync(
        Connection connection, string busName, string path, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        try
        {
            logger.LogDebug("[GtkMenu] Probing {Bus} at {Path}", busName, path);
            var proxy = connection.CreateProxy<IGtkMenus>(busName, new ObjectPath(path));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(800));

            var probeTask = proxy.StartAsync([0u]);
            await Task.WhenAny(probeTask, Task.Delay(Timeout.Infinite, cts.Token));
            if (!probeTask.IsCompletedSuccessfully)
            {
                logger.LogDebug("[GtkMenu] Probe timed out or failed on {Bus} at {Path}", busName, path);
                return false;
            }

            // Unsubscribe — fire-and-forget with short timeout so a hang doesn't block us.
            var endTask = proxy.EndAsync([0u]);
            await Task.WhenAny(endTask, Task.Delay(500, ct));

            logger.LogDebug("[GtkMenu] Probe succeeded on {Bus} at {Path}", busName, path);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug("[GtkMenu] Probe exception on {Bus} at {Path}: {M}", busName, path, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Tries to discover the GtkApplication's <c>/menus/menubar</c> path by recursively
    /// walking the D-Bus introspect tree up to 3 levels deep.
    /// GTK uses the app-id converted to a path — e.g. "fr.handbrake.ghb" →
    /// "/fr/handbrake/ghb/menus/menubar" — so we need 3 levels of recursion from root
    /// to find it when the app ID isn't known ahead of time.
    /// </summary>
    private async Task<string?> DiscoverGtkMenuBarPathAsync(
        Connection connection, string busName, CancellationToken ct)
    {
        return await WalkIntrospectAsync(connection, busName, "/", 0, ct);
    }

    private async Task<string?> WalkIntrospectAsync(
        Connection connection, string busName, string objectPath, int depth, CancellationToken ct)
    {
        const int maxDepth = 3; // fr.handbrake.ghb = 3 path components
        if (depth > maxDepth || ct.IsCancellationRequested) return null;

        // At depth >= 1, try this path as a GtkApplication base before going deeper.
        if (depth >= 1)
        {
            var candidate = objectPath.TrimEnd('/') + "/menus/menubar";
            if (await ProbeGtkMenusAsync(connection, busName, candidate, ct))
                return candidate;
        }

        if (depth == maxDepth) return null;

        // Introspect to find child nodes.
        string xml;
        try
        {
            var introspectable = connection.CreateProxy<IIntrospectable>(busName, new ObjectPath(objectPath));
            var introspectTask = introspectable.IntrospectAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));
            await Task.WhenAny(introspectTask, Task.Delay(Timeout.Infinite, cts.Token));
            if (!introspectTask.IsCompletedSuccessfully)
            {
                if (depth == 0)
                    logger.LogDebug("[GtkMenu] Introspect hung/failed on {Bus} — skipping discovery", busName);
                return null;
            }
            xml = introspectTask.Result;
        }
        catch { return null; }

        // Walk each child node recursively.
        var prefix = objectPath.TrimEnd('/');
        foreach (System.Text.RegularExpressions.Match match in
            System.Text.RegularExpressions.Regex.Matches(xml, @"<node name=""([^""]+)"""))
        {
            if (ct.IsCancellationRequested) return null;
            var childName = match.Groups[1].Value;
            // Skip obviously invalid names (dashes make invalid ObjectPaths, dots are sub-services).
            if (childName.Length == 0 || childName.Contains('.') || childName.Contains('-')) continue;
            var childPath = prefix + "/" + childName;
            var result = await WalkIntrospectAsync(connection, busName, childPath, depth + 1, ct);
            if (result != null) return result;
        }
        return null;
    }

    /// <summary>
    /// Reads the full menu tree from <c>org.gtk.Menus</c> by recursively subscribing to all
    /// referenced submenu subscription IDs and converts to our standard JSON node dictionary.
    /// </summary>
    private async Task<Dictionary<string, object?>?> ReadMenuBarAsync(
        IGtkMenus gtkMenus,
        string busName,
        string menuPath,
        Dictionary<int, (string BusName, string Path)> idMap,
        IdCounter counter,
        CancellationToken ct)
    {
        // The action executor needs the actions path — by convention it's the parent of /menus/menubar.
        // e.g. /fr/handbrake/ghb/menus/menubar → /fr/handbrake/ghb
        // /org/appmenu/gtk/window/menus/menubar → /org/appmenu/gtk/window
        var lastMenusIdx = menuPath.LastIndexOf("/menus/", StringComparison.Ordinal);
        var actionsBasePath = lastMenusIdx >= 0 ? menuPath[..lastMenusIdx] : menuPath;

        // Cache for section data, keyed by (subscription_id, section_index).
        var sectionCache = new Dictionary<(uint, uint), IDictionary<string, object>[]>();
        var subscribed   = new HashSet<uint>();

        // Subscribe to root (ID=0) first.  Collect all referenced submenus recursively.
        await SubscribeAsync(gtkMenus, [0u], sectionCache, subscribed, ct);

        // Build the root bar node.
        var rootChildren = await BuildItemsAsync(
            gtkMenus, 0u, sectionCache, subscribed,
            busName, actionsBasePath, idMap, counter, ct);

        if (rootChildren == null || rootChildren.Count == 0) return null;

        // Clean up subscriptions — fire-and-forget with short timeout; a hang must not block callers.
        var endCleanup = gtkMenus.EndAsync(subscribed.ToArray());
        await Task.WhenAny(endCleanup, Task.Delay(500, ct));

        return new Dictionary<string, object?>
        {
            ["id"]       = 0,
            ["label"]    = "Root",
            ["children"] = (object?)rootChildren,
        };
    }

    /// <summary>
    /// Calls <c>Start</c> for an array of subscription IDs, merging results into
    /// <paramref name="sectionCache"/> and updating <paramref name="subscribed"/>.
    /// </summary>
    private async Task SubscribeAsync(
        IGtkMenus gtkMenus,
        uint[] ids,
        Dictionary<(uint, uint), IDictionary<string, object>[]> sectionCache,
        HashSet<uint> subscribed,
        CancellationToken ct)
    {
        var newIds = ids.Where(id => !subscribed.Contains(id)).ToArray();
        if (newIds.Length == 0) return;

        (uint SubId, uint Section, IDictionary<string, object>[] Items)[] sections;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            sections = await gtkMenus.StartAsync(newIds);
        }
        catch (Exception ex)
        {
            logger.LogDebug("[GtkMenu] Start({Ids}) failed: {M}", string.Join(",", newIds), ex.Message);
            return;
        }

        foreach (var id in newIds) subscribed.Add(id);

        foreach (var (subId, section, items) in sections)
            sectionCache[(subId, section)] = items;
    }

    /// <summary>
    /// Recursively builds menu item nodes for all sections within subscription
    /// <paramref name="subscriptionId"/>, following ":submenu" and ":section" references.
    /// </summary>
    private async Task<List<object?>?> BuildItemsAsync(
        IGtkMenus gtkMenus,
        uint subscriptionId,
        Dictionary<(uint, uint), IDictionary<string, object>[]> sectionCache,
        HashSet<uint> subscribed,
        string busName,
        string actionsBasePath,
        Dictionary<int, (string BusName, string Path)> idMap,
        IdCounter counter,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;

        // Collect all sections for this subscription (there may be multiple section indices).
        var relevantSections = sectionCache
            .Where(kv => kv.Key.Item1 == subscriptionId)
            .OrderBy(kv => kv.Key.Item2)
            .ToList();

        if (relevantSections.Count == 0) return null;

        var result = new List<object?>();
        bool firstSection = true;

        foreach (var ((_, _), items) in relevantSections)
        {
            if (!firstSection && result.Count > 0)
                result.Add(BuildSeparatorNode(counter, idMap, busName, actionsBasePath));
            firstSection = false;

            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;

                // ── :section reference — inline the linked section ───────────────
                // Tmds.DBus 0.x deserializes (uu) struct variants as object[], not ValueTuple.
                if (item.TryGetValue(":section", out var sectionRef) && TryGetUUPair(sectionRef, out var secTuple))
                {
                    var (refSubId, refSecIdx) = secTuple;
                    if (refSubId != subscriptionId)
                    {
                        await SubscribeAsync(gtkMenus, [refSubId], sectionCache, subscribed, ct);
                        if (sectionCache.TryGetValue((refSubId, refSecIdx), out var linkedItems))
                        {
                            // Inline the linked section's items.
                            foreach (var lItem in linkedItems)
                            {
                                var lNode = await BuildSingleItemAsync(
                                    gtkMenus, lItem, sectionCache, subscribed,
                                    busName, actionsBasePath, idMap, counter, ct);
                                if (lNode != null) result.Add(lNode);
                            }
                        }
                    }
                    continue;
                }

                var node = await BuildSingleItemAsync(
                    gtkMenus, item, sectionCache, subscribed,
                    busName, actionsBasePath, idMap, counter, ct);
                if (node != null) result.Add(node);
            }
        }

        return result.Count > 0 ? result : null;
    }

    private async Task<Dictionary<string, object?>?> BuildSingleItemAsync(
        IGtkMenus gtkMenus,
        IDictionary<string, object> item,
        Dictionary<(uint, uint), IDictionary<string, object>[]> sectionCache,
        HashSet<uint> subscribed,
        string busName,
        string actionsBasePath,
        Dictionary<int, (string BusName, string Path)> idMap,
        IdCounter counter,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;

        // Extract label (stripping GTK mnemonic underscore for display).
        string? rawLabel = item.TryGetValue("label", out var lv) ? lv as string : null;
        string? label    = rawLabel != null ? rawLabel.Replace("_", "") : null;

        // Determine action name (for execution).
        string? actionName = item.TryGetValue("action", out var av) ? av as string : null;

        int myId = counter.Value++;

        // ── :submenu reference ───────────────────────────────────────────────
        // Tmds.DBus 0.x deserializes (uu) struct variants as object[], not ValueTuple.
        if (item.TryGetValue(":submenu", out var submenuRef) && TryGetUUPair(submenuRef, out var subTuple))
        {
            var (subSubId, subSecIdx) = subTuple;
            await SubscribeAsync(gtkMenus, [subSubId], sectionCache, subscribed, ct);

            var children = await BuildItemsAsync(
                gtkMenus, subSubId, sectionCache, subscribed,
                busName, actionsBasePath, idMap, counter, ct);

            // Also try "submenu-action" as the action key if no direct action is given.
            if (actionName == null && item.TryGetValue("submenu-action", out var sav))
                actionName = sav as string;

            RegisterAction(myId, busName, actionsBasePath, actionName, idMap);

            var node = new Dictionary<string, object?>
            {
                ["id"]      = myId,
                ["label"]   = label ?? "Menu",
                ["enabled"] = true,
            };
            if (children?.Count > 0) node["children"] = (object?)children;
            return node;
        }

        // ── Leaf action item ─────────────────────────────────────────────────
        if (label != null)
        {
            RegisterAction(myId, busName, actionsBasePath, actionName, idMap);
            return new Dictionary<string, object?>
            {
                ["id"]      = myId,
                ["label"]   = label,
                ["enabled"] = true,
            };
        }

        // Separator (no label, no submenu).
        return BuildSeparatorNode(counter, idMap, busName, actionsBasePath);
    }

    /// <summary>
    /// Handles both ValueTuple&lt;uint,uint&gt; (ideal) and object[] (actual Tmds.DBus 0.x output
    /// for D-Bus struct variants) when reading (uu) values from org.gtk.Menus item dicts.
    /// </summary>
    private static bool TryGetUUPair(object? val, out (uint, uint) result)
    {
        if (val is ValueTuple<uint, uint> vt) { result = vt; return true; }
        if (val is object[] arr && arr.Length >= 2 && arr[0] is uint u1 && arr[1] is uint u2)
        {
            result = (u1, u2);
            return true;
        }
        result = default;
        return false;
    }

    private static Dictionary<string, object?> BuildSeparatorNode(
        IdCounter counter,
        Dictionary<int, (string BusName, string Path)> idMap,
        string busName, string actionsBasePath)
    {
        int sepId = counter.Value++;
        idMap[sepId] = (busName, actionsBasePath + "|");
        return new Dictionary<string, object?>
        {
            ["id"]   = sepId,
            ["type"] = "separator",
        };
    }

    /// <summary>
    /// Stores the action execution info in the id map.
    /// Path encoding: "{actionsBasePath}|{actionName}" — parsed by <see cref="ExecuteItemAsync"/>.
    /// </summary>
    private static void RegisterAction(
        int id,
        string busName,
        string actionsBasePath,
        string? actionName,
        Dictionary<int, (string BusName, string Path)> idMap)
    {
        // Strip the action prefix ("app.", "win.", "unity.") leaving just the name for Activate.
        string? bareAction = actionName;
        if (bareAction != null)
        {
            var dotIdx = bareAction.IndexOf('.');
            if (dotIdx >= 0) bareAction = bareAction[(dotIdx + 1)..];
        }
        idMap[id] = (busName, actionsBasePath + "|" + (bareAction ?? string.Empty));
    }
}
