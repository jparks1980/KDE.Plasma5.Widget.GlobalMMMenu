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

            // ── Ensure accessibility is enabled ──────────────────────────────
            // Qt apps only load their AT-SPI bridge when org.a11y.Status.IsEnabled is true.
            // On a default KDE desktop with no screen reader, this is false, so every Qt app
            // reports "No AT-SPI connection found". Setting it to true broadcasts PropertiesChanged
            // which causes QSpiAccessibilityBridge to activate in all running Qt/KDE apps.
            // This is exactly what Orca (and any AT-SPI screen reader) does at startup.
            try
            {
                var status = sessionConn.CreateProxy<IAtSpiStatus>("org.a11y.Bus", new ObjectPath("/org/a11y/bus"));
                bool isEnabled = await status.GetAsync<bool>("IsEnabled");
                if (!isEnabled)
                {
                    await status.SetAsync("IsEnabled", true);
                    logger.LogInformation("[AT-SPI] IsEnabled was false — set to true. Qt apps will now load AT-SPI bridge.");
                }
                else
                {
                    logger.LogDebug("[AT-SPI] IsEnabled already true");
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug("[AT-SPI] Could not set IsEnabled (non-fatal): {M}", ex.Message);
            }

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
    /// Subscribes to PropertiesChanged on the AT-SPI application node for <paramref name="atspiBusName"/>
    /// and invokes <paramref name="onChildAdded"/> whenever ChildCount increases — i.e. whenever a new
    /// widget (such as a lazily-realized GtkMenuBar) is added to the accessibility tree.
    ///
    /// This is the event-driven complement to the polling retry loop.  GTK apps (e.g. HandBrake) may
    /// not realize their GtkMenuBar widget until well after the initial focus event; this subscription
    /// fires immediately when the menu bar appears, regardless of how long it takes.
    ///
    /// The method attaches to path <c>/org/a11y/atspi/accessible/1</c> which is the application node
    /// that is the direct parent of all top-level windows and the menu bar.
    ///
    /// Returns the IDisposable subscription (caller should keep it alive; dispose to unsubscribe), or
    /// null if the AT-SPI connection is unavailable.
    /// </summary>
    public async Task<IDisposable?> WatchAppNodeChildrenChangedAsync(
        string atspiBusName,
        Action onChildAdded,
        Action<Exception>? onError = null)
    {
        if (_atspiConnection == null) return null;
        try
        {
            var accessible = _atspiConnection.CreateProxy<IAtSpiAccessible>(
                atspiBusName, new ObjectPath("/org/a11y/atspi/accessible/1"));
            return await accessible.WatchPropertiesChangedAsync(
                changes =>
                {
                    // Fire only when ChildCount is in the changed set — avoids spurious triggers.
                    if (changes.Changed.Any(kv => kv.Key == "ChildCount"))
                        onChildAdded();
                },
                onError);
        }
        catch (Exception ex)
        {
            logger.LogDebug("[AT-SPI] WatchChildCount setup failed on {Bus}: {M}", atspiBusName, ex.Message);
            return null;
        }
    }

    /// <summary>
    // Cache: pid → AT-SPI bus connection name. Avoids a full ListNames scan on every focus event.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> _atspiBusConnCache = new();

    /// <summary>
    /// For Chromium-based apps (Brave, Chrome, Chromium) that expose all browser windows
    /// through a SINGLE AT-SPI connection with multiple FRAME children, determines the
    /// 1-based creation-order rank of the CURRENTLY FOCUSED window.
    ///
    /// Both AT-SPI accessible node IDs and Chromium's dbusmenu path suffix numbers are
    /// assigned monotonically in window-creation order. The rank of a window's node
    /// (sorted by node ID ascending) equals the rank of its dbusmenu path (sorted by
    /// path suffix number ascending). This correlation lets us pick the correct dbusmenu
    /// path for a focused window without probing each path.
    ///
    /// Strategy (in order):
    ///   1. Geometry — match KWin window position against AT-SPI component screen extents.
    ///      Works for any title (including "New Tab"). Uniquely identifies windows on
    ///      different monitors; falls through to title when multiple nodes share a position.
    ///   2. Title match — compare KWin caption against AT-SPI accessible Name. Works for
    ///      unique titles on the same monitor.
    ///   3. StateActive — last resort; unreliable due to async lag from Brave's bridge.
    ///
    /// Example (3 Brave windows, 2 previously closed):
    ///   AT-SPI: [/accessible/1="Search...", /accessible/2="Acorn...", /accessible/3="Pete..."]
    ///   DBus:   [/menu/1, /menu/4, /menu/5]  (gaps at 2,3 = closed windows)
    ///   "New Tab" on Monitor 2 → geometry matches only node 1 (x≈1920) → rank=1 → /menu/1
    /// </summary>
    /// <returns>1-based rank of the focused window, or 0 if not found / AT-SPI unavailable.</returns>
    public async Task<int> GetDbusmenuRankForWindowAsync(uint pid, string windowTitle, int gx, int gy, CancellationToken cancellationToken)
    {
        if (_atspiConnection == null || _atspiBusDaemon == null) return 0;

        // Find (or recall from cache) the AT-SPI connection for this PID.
        if (!_atspiBusConnCache.TryGetValue(pid, out var atspiBusConn))
        {
            try
            {
                var names = await _atspiBusDaemon.ListNamesAsync();
                var uniqueNames = names.Where(n => n.StartsWith(':')).ToArray();
                const int batchSize = 16;
                for (int i = 0; i < uniqueNames.Length && atspiBusConn == null; i += batchSize)
                {
                    if (cancellationToken.IsCancellationRequested) return 0;
                    var batch = uniqueNames.Skip(i).Take(batchSize);
                    var pidTasks = batch.Select(async name =>
                    {
                        try { return (name, Pid: await _atspiBusDaemon.GetConnectionUnixProcessIDAsync(name)); }
                        catch { return (name, Pid: 0u); }
                    });
                    foreach (var (name, p) in await Task.WhenAll(pidTasks))
                        if (p == pid) { atspiBusConn = name; break; }
                }
            }
            catch { return 0; }

            if (atspiBusConn == null) return 0;
            _atspiBusConnCache[pid] = atspiBusConn;
        }

        // Get root's child windows (FRAME-role accessible nodes).
        var rootProxy = _atspiConnection.CreateProxy<IAtSpiAccessible>(
            atspiBusConn, new ObjectPath("/org/a11y/atspi/accessible/root"));

        (string BusName, ObjectPath Path)[] children;
        try { children = await rootProxy.GetChildrenAsync(); }
        catch
        {
            // Connection may have died — evict cache so next call re-scans.
            _atspiBusConnCache.TryRemove(pid, out _);
            return 0;
        }

        if (children.Length == 0) return 0;

        // Sort children by their accessible node ID (trailing number in D-Bus object path).
        // Chromium assigns these IDs monotonically in window-creation order.
        var ranked = children
            .Select(c =>
            {
                var pathStr = c.Path.ToString();
                var sep = pathStr.LastIndexOf('/');
                var numStr = sep >= 0 ? pathStr[(sep + 1)..] : "";
                return (c.BusName, c.Path, NodeId: int.TryParse(numStr, out var n) ? n : int.MaxValue);
            })
            .OrderBy(x => x.NodeId)
            .ToArray();

        // Fetch Name, screen extents, and StateActive for all nodes concurrently.
        var nodeInfos = await Task.WhenAll(ranked.Select(async r =>
        {
            string name = "";
            int extX = int.MinValue, extY = int.MinValue;
            bool isActive = false;
            try
            {
                var acc = _atspiConnection.CreateProxy<IAtSpiAccessible>(r.BusName, r.Path);
                name = await acc.GetAsync<string>("Name");
                try { var s = await acc.GetStateAsync(); isActive = s.Length > 0 && (s[0] & StateActive) != 0; } catch { }
            }
            catch { }
            try
            {
                var comp = _atspiConnection.CreateProxy<IAtSpiComponent>(r.BusName, r.Path);
                var ext  = await comp.GetExtentsAsync(0); // coord_type=0 = XY_SCREEN
                extX = ext.X; extY = ext.Y;
            }
            catch { /* IComponent not always implemented */ }
            return (r.NodeId, Name: name, ExtX: extX, ExtY: extY, IsActive: isActive);
        }));

        // Log all nodes at Info level for diagnostics.
        for (int i = 0; i < nodeInfos.Length; i++)
        {
            var n = nodeInfos[i];
            logger.LogInformation(
                "[AT-SPI] Node {R}/{Total}: id={Id} name='{N}' pos=({X},{Y}) active={A}",
                i + 1, nodeInfos.Length, n.NodeId, n.Name, n.ExtX, n.ExtY, n.IsActive);
        }
        logger.LogInformation(
            "[AT-SPI] KWin geo=({GX},{GY}) title='{T}'", gx, gy, windowTitle);

        // Strategy 1: Geometry — match KWin window position against AT-SPI screen extents.
        // AT-SPI FRAME extents are screen-absolute. KWin reports the window outer frame position.
        // Allow 350px tolerance to cover title bars, decorations, and Wayland coordinate offsets.
        // When exactly one node matches, it uniquely identifies the focused window (cross-monitor).
        if (gx != 0 || gy != 0)
        {
            const int geoTolerance = 350;
            var geoMatches = nodeInfos
                .Select((n, i) => (n, Rank: i + 1))
                .Where(x => x.n.ExtX != int.MinValue
                         && Math.Abs(x.n.ExtX - gx) < geoTolerance
                         && Math.Abs(x.n.ExtY - gy) < geoTolerance)
                .ToArray();

            if (geoMatches.Length == 1)
            {
                logger.LogInformation(
                    "[AT-SPI] Rank by geometry: kwin=({GX},{GY}) atspi=({AX},{AY}) → rank={R}/{Total} node={N}",
                    gx, gy, geoMatches[0].n.ExtX, geoMatches[0].n.ExtY,
                    geoMatches[0].Rank, nodeInfos.Length, geoMatches[0].n.NodeId);
                return geoMatches[0].Rank;
            }
        }

        // Strategy 2: Title match — compare KWin caption against AT-SPI accessible Name.
        if (!string.IsNullOrEmpty(windowTitle))
        {
            for (int i = 0; i < nodeInfos.Length; i++)
            {
                var n = nodeInfos[i];
                if (!string.IsNullOrEmpty(n.Name) && WindowTitlesMatch(windowTitle, n.Name))
                {
                    logger.LogInformation(
                        "[AT-SPI] Rank by title: '{T}' ~ '{N}' → rank={R}/{Total} node={Id}",
                        windowTitle, n.Name, i + 1, nodeInfos.Length, n.NodeId);
                    return i + 1;
                }
            }
        }

        // Strategy 3: StateActive — unreliable due to async lag but used as last resort.
        for (int i = 0; i < nodeInfos.Length; i++)
        {
            if (nodeInfos[i].IsActive)
            {
                logger.LogInformation(
                    "[AT-SPI] Rank by StateActive: rank={R}/{Total} node={N}",
                    i + 1, nodeInfos.Length, nodeInfos[i].NodeId);
                return i + 1;
            }
        }

        logger.LogInformation(
            "[AT-SPI] No rank match for pid={P} title='{T}' kwin=({GX},{GY})", pid, windowTitle, gx, gy);
        return 0;
    }

    /// <summary>
    /// Returns true when two window title strings refer to the same window, tolerating
    /// browser-appended suffixes like " - Brave" or " - Google Chrome".
    /// </summary>
    private static bool WindowTitlesMatch(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        static string Strip(string s)
        {
            foreach (var suffix in new[] { " - Brave", " - Brave Browser", " - Chrome",
                                           " - Chromium", " - Firefox", " - Google Chrome" })
                if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return s[..^suffix.Length];
            return s;
        }
        return string.Equals(Strip(a), Strip(b), StringComparison.OrdinalIgnoreCase);
    }

    /// Builds a menu JSON string for the window belonging to the given OS process ID.
    /// Also returns an ID→(busName,path) map so the exporter can route ExecuteItem calls.
    /// Returns (null, empty) if the app has no menu bar or is not registered on AT-SPI.
    ///
    /// Multi-window apps fall into two categories:
    ///   A) ONE shared AT-SPI connection with multiple window children (some Qt apps).
    ///      → <see cref="GetMenuJsonFromConnectionAsync"/> handles window selection there.
    ///   B) MULTIPLE separate AT-SPI connections for the same PID (Brave, Chromium).
    ///      Each browser window registers its own AT-SPI connection even though all share
    ///      the same browser-process PID.  In this case we must pick the RIGHT connection —
    ///      the one belonging to the focused X11 window — before calling GetMenuJsonFromConnectionAsync.
    ///      <paramref name="preferredWindowId"/> and <paramref name="preferredWindowTitle"/> are used
    ///      to select the correct connection among all candidates.
    /// </summary>
    public async Task<(string? Json, Dictionary<int, (string BusName, string Path)> IdMap, string? AtSpiBusName)>
        GetMenuJsonForPidAsync(uint pid, CancellationToken cancellationToken,
                               uint preferredWindowId = 0, string? preferredWindowTitle = null,
                               int preferredGx = -1, int preferredGy = -1)
    {
        var empty = (Json: (string?)null, IdMap: new Dictionary<int, (string, string)>(), AtSpiBusName: (string?)null);
        if (_atspiConnection == null || _atspiBusDaemon == null)
            return empty;

        // ── Find ALL AT-SPI connections whose OS PID matches ─────────────────
        // Most apps have one connection; Chromium/Brave may have one per window.
        // We must collect all of them and then pick the right one for the focused window.
        var matchingConns = new List<string>();
        try
        {
            var names = await _atspiBusDaemon.ListNamesAsync();
            var uniqueNames = names.Where(n => n.StartsWith(':')).ToArray();

            const int batchSize = 16;
            for (int i = 0; i < uniqueNames.Length; i += batchSize)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var batch = uniqueNames.Skip(i).Take(batchSize);
                var pidTasks = batch.Select(async name =>
                {
                    try
                    {
                        var p = await _atspiBusDaemon.GetConnectionUnixProcessIDAsync(name);
                        return (name, Pid: p);
                    }
                    catch { return (name, Pid: 0u); }
                });
                var results = await Task.WhenAll(pidTasks);
                foreach (var (name, busNamePid) in results)
                    if (busNamePid == pid && busNamePid != 0)
                        matchingConns.Add(name);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("[AT-SPI] ListNames failed: {M}", ex.Message);
            return empty;
        }

        if (matchingConns.Count == 0)
        {
            logger.LogDebug("[AT-SPI] No AT-SPI connection found for pid={P}", pid);
            return empty;
        }

        // ── Single connection: fast path, no selection needed ─────────────────
        if (matchingConns.Count == 1)
        {
            var (json, idMap) = await GetMenuJsonFromConnectionAsync(
                matchingConns[0], cancellationToken, preferredWindowId, preferredWindowTitle, preferredGx, preferredGy);
            return (json, idMap, matchingConns[0]);
        }

        // ── Multiple connections for same PID (e.g. Brave with 3 windows) ────
        // Each connection likely has exactly ONE window in its accessible tree.
        // Pick the correct connection by trying three strategies in order:
        //   1. window-id attribute match (Chromium may expose X11 ID here)
        //   2. window title match (accessible Name vs preferred title)
        //   3. StateActive — the focused window's connection sets this flag
        // If none match, fall back to the first connection.
        logger.LogDebug(
            "[AT-SPI] pid={P}: {N} connections — selecting for window 0x{W:X8} '{T}' geo=({GX},{GY})",
            pid, matchingConns.Count, preferredWindowId, preferredWindowTitle ?? "", preferredGx, preferredGy);

        string? bestConn = null;

        // Strategies 1, 2 & 2.5: deterministic (no timing dependency).
        if (preferredWindowId != 0 || !string.IsNullOrEmpty(preferredWindowTitle) || (preferredGx >= 0 && preferredGy >= 0))
        {
            foreach (var conn in matchingConns)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    var connRoot = _atspiConnection.CreateProxy<IAtSpiAccessible>(
                        conn, new ObjectPath("/org/a11y/atspi/accessible/root"));
                    var connWindows = await connRoot.GetChildrenAsync();
                    foreach (var (wb, wp) in connWindows)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        try
                        {
                            var winAcc = _atspiConnection.CreateProxy<IAtSpiAccessible>(wb, wp);

                            // Strategy 1: window-id attribute (decimal or hex)
                            if (preferredWindowId != 0)
                            {
                                try
                                {
                                    var attrs = await winAcc.GetAttributesAsync();
                                    if (attrs.TryGetValue("window-id", out var wid))
                                    {
                                        bool parsed = uint.TryParse(wid, out var decId) && decId == preferredWindowId;
                                        if (!parsed)
                                            parsed = uint.TryParse(wid.TrimStart('0', 'x', 'X'),
                                                         System.Globalization.NumberStyles.HexNumber,
                                                         null, out var hexId) && hexId == preferredWindowId;
                                        if (parsed)
                                        {
                                            bestConn = conn;
                                            logger.LogDebug(
                                                "[AT-SPI] pid={P}: matched conn {C} by window-id={W}", pid, conn, wid);
                                            break;
                                        }
                                    }
                                }
                                catch { }
                            }
                            if (bestConn != null) break;

                            // Strategy 2: window title (accessible Name)
                            if (!string.IsNullOrEmpty(preferredWindowTitle))
                            {
                                try
                                {
                                    var name = await winAcc.GetAsync<string>("Name");
                                    if (!string.IsNullOrEmpty(name) &&
                                        name.Equals(preferredWindowTitle, StringComparison.OrdinalIgnoreCase))
                                    {
                                        bestConn = conn;
                                        logger.LogDebug(
                                            "[AT-SPI] pid={P}: matched conn {C} by title '{N}'", pid, conn, name);
                                        break;
                                    }
                                }
                                catch { }
                            }

                            if (bestConn != null) break;

                            // Strategy 2.5: geometry match — compare KWin c.geometry.x/y against
                            // AT-SPI component extents.  No timing dependency: the window's screen
                            // position is stable and independent of focus-event processing.
                            if (preferredGx >= 0 && preferredGy >= 0)
                            {
                                try
                                {
                                    var comp = _atspiConnection!.CreateProxy<IAtSpiComponent>(wb, wp);
                                    var ext  = await comp.GetExtentsAsync(0); // 0 = XY_SCREEN
                                    if (ext.X == preferredGx && ext.Y == preferredGy)
                                    {
                                        bestConn = conn;
                                        logger.LogDebug(
                                            "[AT-SPI] pid={P}: matched conn {C} by geometry ({GX},{GY})",
                                            pid, conn, preferredGx, preferredGy);
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                if (bestConn != null) break;
            }
        }

        // Strategy 3: StateActive (timing-dependent but often works on re-focus)
        if (bestConn == null)
        {
            foreach (var conn in matchingConns)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    var connRoot = _atspiConnection.CreateProxy<IAtSpiAccessible>(
                        conn, new ObjectPath("/org/a11y/atspi/accessible/root"));
                    var connWindows = await connRoot.GetChildrenAsync();
                    foreach (var (wb, wp) in connWindows)
                    {
                        try
                        {
                            var winAcc = _atspiConnection.CreateProxy<IAtSpiAccessible>(wb, wp);
                            var state  = await winAcc.GetStateAsync();
                            if (state.Length > 0 && (state[0] & StateActive) != 0)
                            {
                                bestConn = conn;
                                logger.LogDebug(
                                    "[AT-SPI] pid={P}: matched conn {C} by StateActive", pid, conn);
                                break;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                if (bestConn != null) break;
            }
        }

        // Fallback: first connection
        bestConn ??= matchingConns[0];

        var (bJson, bIdMap) = await GetMenuJsonFromConnectionAsync(
            bestConn, cancellationToken, preferredWindowId, preferredWindowTitle, preferredGx, preferredGy);
        return (bJson, bIdMap, bestConn);
    }

    // AT-SPI state constants for window-level states
    private const uint StateActive = 1u << 1;  // ATSPI_STATE_ACTIVE (index 1) — window is in the foreground

    /// <summary>
    /// Fallback for when a window PID is unavailable: scans all AT-SPI connections for a
    /// window whose state includes ACTIVE (currently in the foreground) and returns its menu bar.
    /// Used when <c>GetWindowPid</c> returns 0 (e.g. XWayland apps without _NET_WM_PID,
    /// or native Wayland apps whose KWin PID report is not yet available).
    /// </summary>
    public async Task<(string? Json, Dictionary<int, (string BusName, string Path)> IdMap, string? AtSpiBusName)>
        GetMenuJsonForActiveWindowAsync(CancellationToken cancellationToken)
    {
        var empty = (Json: (string?)null, IdMap: new Dictionary<int, (string, string)>(), AtSpiBusName: (string?)null);
        if (_atspiConnection == null || _atspiBusDaemon == null) return empty;

        string[] names;
        try { names = await _atspiBusDaemon.ListNamesAsync(); }
        catch (Exception ex) { logger.LogDebug("[AT-SPI] Active scan ListNames failed: {M}", ex.Message); return empty; }

        foreach (var busName in names)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!busName.StartsWith(':')) continue;

            try
            {
                var appRoot = _atspiConnection.CreateProxy<IAtSpiAccessible>(
                    busName, new ObjectPath("/org/a11y/atspi/accessible/root"));

                // Short timeout per connection so we don't stall on unresponsive apps.
                using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                checkCts.CancelAfter(TimeSpan.FromMilliseconds(400));
                var windowsTask = appRoot.GetChildrenAsync();
                await Task.WhenAny(windowsTask, Task.Delay(Timeout.Infinite, checkCts.Token));
                if (!windowsTask.IsCompletedSuccessfully) continue;

                foreach (var (winBus, winPath) in windowsTask.Result)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var winAcc = _atspiConnection.CreateProxy<IAtSpiAccessible>(winBus, winPath);

                    using var stateCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stateCts.CancelAfter(TimeSpan.FromMilliseconds(300));
                    var stateTask = winAcc.GetStateAsync();
                    await Task.WhenAny(stateTask, Task.Delay(Timeout.Infinite, stateCts.Token));
                    if (!stateTask.IsCompletedSuccessfully) continue;

                    var stateWords = stateTask.Result;
                    if (stateWords.Length == 0 || (stateWords[0] & StateActive) == 0) continue;

                    // This window is ACTIVE — it has foreground focus.
                    logger.LogDebug("[AT-SPI] Active scan: ACTIVE window found on {Bus}", busName);
                    var (json, idMap) = await GetMenuJsonFromConnectionAsync(busName, cancellationToken);
                    if (!string.IsNullOrEmpty(json) && json != "{}")
                    {
                        logger.LogInformation("[AT-SPI] Active scan succeeded for {Bus} ({N} items)", busName, idMap.Count);
                        return (json, idMap, busName);
                    }
                }
            }
            catch { /* connection not accessible or has no AT-SPI root — skip */ }
        }

        logger.LogDebug("[AT-SPI] Active scan: no ACTIVE window with menu found");
        return empty;
    }

    /// <summary>
    /// Builds a menu JSON string directly from a known AT-SPI bus name.
    /// Walks: app root → window children → finds ROLE_MENU_BAR → serializes tree.
    /// Also returns the ID→(busName,path) map needed for execution routing.
    ///
    /// For multi-window apps (Brave, Chromium) that share one AT-SPI bus connection,
    /// <paramref name="preferredWindowId"/> and <paramref name="preferredWindowTitle"/>
    /// are used to pick the correct window's accessible tree so that idMap entries point
    /// to the focused window's objects (ensuring ExecuteItem/DoAction fires on the right window).
    ///
    /// Matching strategies in priority order:
    ///   1. "window-id" attribute (Chromium/Brave sets the X11 window ID here — reliable)
    ///   2. Window title (AT-SPI accessible Name vs X11 WM_NAME — universal, no timing dependency)
    ///   3. StateActive bit (timing-dependent — app must have processed the focus event already)
    ///   4. First window with a menu bar (single-window apps / ultimate fallback)
    /// </summary>
    public async Task<(string? Json, Dictionary<int, (string BusName, string Path)> IdMap)>
        GetMenuJsonFromConnectionAsync(string atspiBusName, CancellationToken cancellationToken,
                                       uint preferredWindowId = 0, string? preferredWindowTitle = null,
                                       int preferredGx = -1, int preferredGy = -1)
    {
        var empty = (Json: (string?)null, IdMap: new Dictionary<int, (string, string)>());
        if (_atspiConnection == null) return empty;

        try
        {
            var appRoot = _atspiConnection.CreateProxy<IAtSpiAccessible>(
                atspiBusName, new ObjectPath("/org/a11y/atspi/accessible/root"));

            var windows = await appRoot.GetChildrenAsync();
            logger.LogDebug("[AT-SPI] {Bus}: root has {N} app node(s)", atspiBusName, windows.Length);

            if (windows.Length > 1)
            {
                // ── Strategies 1 & 2: deterministic matching (no timing dependency) ─────────
                // For multi-window apps where all windows share one AT-SPI connection, we need to
                // pick the window whose accessible tree corresponds to the focused X11 window so
                // that every idMap entry points to that window's AT-SPI objects.  StateActive is
                // unreliable here because the X11 focus event arrives at our service at the same
                // time as it arrives at the app, creating a race between our scan and the app
                // updating its AT-SPI state.
                if (preferredWindowId != 0 || !string.IsNullOrEmpty(preferredWindowTitle) || (preferredGx >= 0 && preferredGy >= 0))
                {
                    foreach (var (winBus, winPath) in windows)
                    {
                        if (cancellationToken.IsCancellationRequested) return empty;
                        try
                        {
                            var winAcc = _atspiConnection.CreateProxy<IAtSpiAccessible>(winBus, winPath);
                            bool matched = false;

                            // Strategy 1: "window-id" attribute — Chromium/Brave stores the X11
                            // window ID as a decimal string here.  No timing dependency.
                            if (preferredWindowId != 0 && !matched)
                            {
                                try
                                {
                                    var attrs = await winAcc.GetAttributesAsync();
                                    if (attrs.TryGetValue("window-id", out var wid) &&
                                        uint.TryParse(wid, out var atSpiWid) &&
                                        atSpiWid == preferredWindowId)
                                    {
                                        matched = true;
                                        logger.LogDebug(
                                            "[AT-SPI] {Bus}: matched 0x{W:X8} by window-id attribute",
                                            atspiBusName, preferredWindowId);
                                    }
                                }
                                catch { /* attribute read failed — try next strategy */ }
                            }

                            // Strategy 2: window title — AT-SPI accessible Name vs X11 WM_NAME.
                            // Works for any app; fails only when two windows have identical titles.
                            if (!string.IsNullOrEmpty(preferredWindowTitle) && !matched)
                            {
                                try
                                {
                                    var name = await winAcc.GetAsync<string>("Name");
                                    if (!string.IsNullOrEmpty(name) &&
                                        name.Equals(preferredWindowTitle, StringComparison.OrdinalIgnoreCase))
                                    {
                                        matched = true;
                                        logger.LogDebug(
                                            "[AT-SPI] {Bus}: matched window by title '{T}'",
                                            atspiBusName, preferredWindowTitle);
                                    }
                                }
                                catch { /* name read failed — try next strategy */ }
                            }

                            // Strategy 2.5: geometry — compare KWin c.geometry.x/y with AT-SPI extents.
                            // Timing-independent: window position is stable regardless of focus state.
                            // Bridges the gap for multi-window Brave/Chromium where title matching fails
                            // (e.g. all tabs show "New Tab - Brave") and StateActive is stale.
                            if (preferredGx >= 0 && preferredGy >= 0 && !matched)
                            {
                                try
                                {
                                    var comp = _atspiConnection!.CreateProxy<IAtSpiComponent>(winBus, winPath);
                                    var ext  = await comp.GetExtentsAsync(0); // 0 = XY_SCREEN
                                    if (ext.X == preferredGx && ext.Y == preferredGy)
                                    {
                                        matched = true;
                                        logger.LogDebug(
                                            "[AT-SPI] {Bus}: matched window by geometry ({GX},{GY})",
                                            atspiBusName, preferredGx, preferredGy);
                                    }
                                }
                                catch { /* component read failed — try next strategy */ }
                            }

                            if (!matched) continue;

                            var winChildren = await winAcc.GetChildrenAsync();
                            var result = await FindMenuBarInChildrenAsync(
                                atspiBusName, winChildren, depth: 0, cancellationToken);
                            if (result != null) return result.Value;
                        }
                        catch { /* unresponsive window node — skip */ }
                    }
                }

                // ── Strategy 3: StateActive (timing-dependent fallback) ───────────────────
                // The app may not have processed the focus event yet, but try anyway —
                // this still catches re-focus events where the app's state is already set.
                foreach (var (winBus, winPath) in windows)
                {
                    if (cancellationToken.IsCancellationRequested) return empty;
                    try
                    {
                        var winAcc = _atspiConnection.CreateProxy<IAtSpiAccessible>(winBus, winPath);
                        var state  = await winAcc.GetStateAsync();
                        if (state.Length == 0 || (state[0] & StateActive) == 0) continue;

                        var winChildren = await winAcc.GetChildrenAsync();
                        var result = await FindMenuBarInChildrenAsync(
                            atspiBusName, winChildren, depth: 0, cancellationToken);
                        if (result != null)
                        {
                            logger.LogDebug(
                                "[AT-SPI] {Bus}: using StateActive window {P}'s menu bar", atspiBusName, winPath);
                            return result.Value;
                        }
                    }
                    catch { /* unresponsive window node — skip */ }
                }
            }

            // ── Strategy 4: first window with a menu bar ─────────────────────────────────
            // Used for single-window apps, or when no other strategy matched.
            foreach (var (winBus, winPath) in windows)
            {
                if (cancellationToken.IsCancellationRequested) return empty;
                var winAcc = _atspiConnection.CreateProxy<IAtSpiAccessible>(winBus, winPath);
                var winChildren = await winAcc.GetChildrenAsync();
                logger.LogDebug("[AT-SPI] {Bus}: app node {P} has {N} children", atspiBusName, winPath, winChildren.Length);

                // Search up to 2 levels deep for the menu bar.
                // Qt apps place it as a direct child of the window, but GTK apps
                // (e.g. HandBrake) may nest it inside an intermediate container.
                var result = await FindMenuBarInChildrenAsync(
                    atspiBusName, winChildren, depth: 0, cancellationToken);
                if (result != null) return result.Value;
                logger.LogDebug("[AT-SPI] {Bus}: no menu bar found in {P}'s {N} children", atspiBusName, winPath, winChildren.Length);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("[AT-SPI] GetMenuJson failed for {Bus}: {M}", atspiBusName, ex.Message);
        }
        return empty;
    }

    /// <summary>
    /// Recursively searches children for a ROLE_MENU_BAR node, up to maxDepth=1 extra level
    /// below the window frame (so total depth from window = 2 levels).
    /// </summary>
    private async Task<(string? Json, Dictionary<int, (string BusName, string Path)> IdMap)?>
        FindMenuBarInChildrenAsync(
            string atspiBusName,
            (string Bus, ObjectPath Path)[] children,
            int depth,
            CancellationToken cancellationToken)
    {
        const int MaxExtraDepth = 1; // 0 = direct window children, 1 = one level into containers
        foreach (var (cBus, cPath) in children)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            var cAcc = _atspiConnection!.CreateProxy<IAtSpiAccessible>(cBus, cPath);
            uint role;
            try { role = await cAcc.GetRoleAsync(); }
            catch { continue; }

            if (role == RoleMenuBar)
            {
                var idMap   = new Dictionary<int, (string BusName, string Path)>();
                var counter = new IdCounter();
                var rootNode = await BuildMenuBarNodeAsync(cBus, cPath, idMap, counter, cancellationToken);
                if (rootNode == null) continue;

                var json = JsonSerializer.Serialize(rootNode, new JsonSerializerOptions { WriteIndented = true });
                logger.LogDebug("[AT-SPI] Built menu JSON for {Bus} ({Len} chars, {N} items)", atspiBusName, json.Length, idMap.Count);
                return (json, idMap);
            }

            // Recurse into container-like nodes only (panel/filler/frame/window)
            // to avoid scanning deeply into the full widget tree.
            if (depth < MaxExtraDepth && IsContainerRole(role))
            {
                try
                {
                    var grandchildren = await cAcc.GetChildrenAsync();
                    var found = await FindMenuBarInChildrenAsync(
                        atspiBusName, grandchildren, depth + 1, cancellationToken);
                    if (found != null) return found;
                }
                catch { /* container not accessible — skip */ }
            }
        }
        return null;
    }

    /// <summary>Returns true for AT-SPI roles that can contain a menu bar as a child.</summary>
    private static bool IsContainerRole(uint role) =>
        role is 39  // FILLER / PANEL
             or 20  // FRAME (nested window-in-window or GtkBox top frame)
             or 29  // GLASS_PANE
             or 14; // DESKTOP_FRAME

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

        // ── Keyboard shortcut (optional, gated by RichMetadata config) ──────────────
        // Only shortcut is available via AT-SPI for Qt menu items — Qt's AT-SPI bridge
        // does not expose QAction icon names through any AT-SPI interface.
        if (RichMetadata)
        {
            try
            {
                var actionProxy = _atspiConnection!.CreateProxy<IAtSpiAction>(busName, path);
                var keyBinding  = await actionProxy.GetKeyBindingAsync(0);
                var shortcut    = ParseAtSpiKeyBinding(keyBinding);
                if (shortcut != null)
                    node["shortcut"] = shortcut;
            }
            catch { /* Action interface not present on this node */ }
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
        if (combo.Count == 0) return null;

        // Filter out Alt-only shortcuts — those are menu mnemonics (e.g. Alt+F for the
        // File menu), not user-facing keyboard shortcuts. Only keep bindings that include
        // at least one of: Ctrl, Shift, Meta/Super.
        var modifiers = combo.Take(combo.Count - 1).ToList();
        if (modifiers.Count > 0 && modifiers.All(m => m == "Alt"))
            return null;

        return new[] { combo.ToArray() };
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

    /// <summary>
    /// Second-pass enrichment: walks the already-built idMap and fetches icon-name + shortcut
    /// for every non-separator node in parallel batches, then re-serializes the JSON tree.
    /// Call this after UpdateAtSpi() has already pushed the fast (no-icon) menu to the widget.
    /// Returns null if enrichment added nothing new.
    /// </summary>
    public async Task<string?> EnrichMenuJsonAsync(
        string fastJson,
        Dictionary<int, (string BusName, string Path)> idMap,
        CancellationToken ct)
    {
        if (_atspiConnection == null || idMap.Count == 0) return null;

        JsonElement root;
        try { root = JsonDocument.Parse(fastJson).RootElement; }
        catch { return null; }

        // Only enrich non-separator nodes — separators don’t have Action/Image interfaces
        // and attempting to call them generates a D-Bus error per node.
        var nonSepIds = new HashSet<int>();
        CollectEnrichableIds(root, nonSepIds);
        var entries = idMap.Where(kvp => nonSepIds.Contains(kvp.Key)).ToList();
        if (entries.Count == 0) return null;

        var enrichments = new Dictionary<int, (string? Icon, object[][]? Shortcut)>();

        // Fetch icon+shortcut in parallel batches of 8 to avoid flooding the AT-SPI socket.
        const int batchSize = 8;
        for (int i = 0; i < entries.Count && !ct.IsCancellationRequested; i += batchSize)
        {
            var batch = entries.Skip(i).Take(batchSize);
            var tasks = batch.Select(async kvp =>
            {
                var (id, (busName, path)) = kvp;
                try
                {
                    // Qt's AT-SPI bridge does not expose QAction icon names through any
                    // AT-SPI interface — icons are simply unavailable on the AT-SPI path.
                    // Fetch shortcuts only via org.a11y.atspi.Action.
                    var actionProxy = _atspiConnection!.CreateProxy<IAtSpiAction>(busName, new ObjectPath(path));
                    var keyBinding  = await actionProxy.GetKeyBindingAsync(0);
                    var shortcut    = ParseAtSpiKeyBinding(keyBinding);
                    if (shortcut != null)
                        return (id, Icon: (string?)null, Shortcut: shortcut);
                }
                catch { }
                return (id, Icon: (string?)null, Shortcut: (object[][]?)null);
            });
            foreach (var r in await Task.WhenAll(tasks))
            {
                if (r.Shortcut != null)
                    enrichments[r.id] = (Icon: null, r.Shortcut);
            }
        }

        if (enrichments.Count == 0) return null;
        logger.LogDebug("[AT-SPI] Enriched {N}/{T} nodes with shortcuts", enrichments.Count, idMap.Count);

        // Re-walk the JSON and inject the enrichment fields.
        var merged = MergeEnrichments(root, enrichments);
        return JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void CollectEnrichableIds(JsonElement el, HashSet<int> ids)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        // Skip separators — they have no Action or Image interfaces.
        if (el.TryGetProperty("type", out var t) && t.GetString() == "separator") return;
        if (el.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id) && id > 0)
            ids.Add(id);
        if (el.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                CollectEnrichableIds(child, ids);
    }

    private static object? MergeEnrichments(JsonElement el, Dictionary<int, (string? Icon, object[][]? Shortcut)> enrichments)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        var dict = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String  => prop.Value.GetString(),
                JsonValueKind.Number  => prop.Value.TryGetInt32(out var i) ? (object)i : prop.Value.GetDouble(),
                JsonValueKind.True    => true,
                JsonValueKind.False   => false,
                JsonValueKind.Null    => null,
                JsonValueKind.Array   => prop.Name == "children"
                    ? prop.Value.EnumerateArray().Select(c => MergeEnrichments(c, enrichments)).ToList()
                    : (object?)prop.Value.ToString(),
                _                     => prop.Value.ToString(),
            };

        if (dict.TryGetValue("id", out var idObj) && idObj is int nodeId
            && enrichments.TryGetValue(nodeId, out var e))
        {
            if (e.Icon != null)     dict["icon-name"] = e.Icon;
            if (e.Shortcut != null) dict["shortcut"]  = e.Shortcut;
        }

        // Heuristic icon fallback: Qt's AT-SPI bridge never exposes icon names, so guess
        // from the label using the FreeDesktop icon naming spec. Only applied when no real
        // icon was already provided (e.g. dbusmenu path).
        if (!dict.ContainsKey("icon-name")
            && dict.TryGetValue("label", out var labelObj) && labelObj is string lbl)
        {
            var guessed = GuessIconFromLabel(lbl);
            if (guessed != null) dict["icon-name"] = guessed;
        }

        return dict;
    }

    /// <summary>
    /// Normalises a label string for lookup: strips mnemonic markers (<c>&amp;</c> for AT-SPI,
    /// <c>_</c> for DBus), trailing ellipsis, and surrounding whitespace, then lowercases.
    /// </summary>
    private static string NormalizeLabel(string? label)
    {
        if (label == null) return string.Empty;
        return label
            .Replace("_", "")       // strip DBus mnemonic markers
            .Replace("&", "")       // strip AT-SPI mnemonic markers
            .Replace("\u2026", "")  // strip unicode ellipsis …
            .TrimEnd('.')           // strip trailing ASCII dots
            .Trim()
            .ToLowerInvariant();
    }

    /// <summary>
    /// Normalises a menu item label and looks it up in the standard FreeDesktop icon table.
    /// Returns null when no match is found.
    /// </summary>
    private static string? GuessIconFromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var key = NormalizeLabel(label);
        return string.IsNullOrEmpty(key) ? null : s_labelIconMap.TryGetValue(key, out var icon) ? icon : null;
    }

    // Holds the DBus-side data for a single menu node: icons plus already-converted children
    // (converted eagerly so we don't hold dangling JsonElement refs after the doc is disposed).
    // ItemId is the dbusmenu integer item ID (>= 0); -1 means no id was present.
    private sealed record DbusNodeData(
        string?       IconName,
        string?       IconData,
        List<object?>? Children,
        int           ItemId = -1);

    /// <summary>
    /// Merges DBus icon and submenu data onto an AT-SPI menu JSON tree.
    /// Nodes are matched by normalised label. Two kinds of enrichment are applied:
    /// <list type="bullet">
    ///   <item><c>icon-name</c> / <c>icon-data</c> — copied when the AT-SPI node has none.</item>
    ///   <item>Missing submenu <c>children</c> — injected when AT-SPI shows a leaf but DBus has
    ///     children (Qt's AT-SPI bridge only populates lazy submenus after they are opened;
    ///     DBus's AboutToShow + GetLayout triggers that population up-front).</item>
    /// </list>
    /// When <paramref name="idMapToUpdate"/> is provided, matched items have their routing entry
    /// updated to <c>("dbusmenu", dbusItemId)</c> so that <see cref="GlobalMenuExporter"/>.ExecuteItemAsync
    /// routes clicks through the per-window dbusmenu proxy instead of AT-SPI.
    /// Returns the merged JSON string when at least one field was added; otherwise null.
    /// </summary>
    public static string? MergeDbusIconsIntoAtSpiJson(
        string atspiJson, string dbusJson,
        Dictionary<int, (string BusName, string Path)>? idMapToUpdate = null)
    {
        JsonElement atspiRoot;
        // Keep the documents alive until we are done reading JsonElements from them.
        using var atspiDoc = JsonDocument.Parse(atspiJson);
        using var dbusDoc  = JsonDocument.Parse(dbusJson);
        atspiRoot = atspiDoc.RootElement;
        var dbusRoot = dbusDoc.RootElement;

        // Build label → DbusNodeData from the flat DBus tree.
        // Children are converted to object? dicts immediately so they don't reference
        // JsonElement objects that would become invalid after dbusDoc is disposed.
        var nodeMap = new Dictionary<string, DbusNodeData>(StringComparer.Ordinal);
        CollectDbusNodes(dbusRoot, nodeMap);
        if (nodeMap.Count == 0) return null;

        bool anyAdded = false;
        var merged = MergeDbusDataIntoNode(atspiRoot, nodeMap, ref anyAdded, idMapToUpdate);
        if (!anyAdded) return null;

        return JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Walks a DBus menu JSON element and collects normalised-label → <see cref="DbusNodeData"/>
    /// mappings. Children are converted eagerly to avoid holding dangling JsonElement refs.
    /// </summary>
    private static void CollectDbusNodes(JsonElement el, Dictionary<string, DbusNodeData> map)
    {
        if (el.ValueKind != JsonValueKind.Object) return;

        var label    = el.TryGetProperty("label",     out var lp) ? lp.GetString() : null;
        var iconName = el.TryGetProperty("icon-name", out var ip) ? ip.GetString() : null;
        var iconData = el.TryGetProperty("icon-data", out var dp) ? dp.GetString() : null;

        List<object?>? kids = null;
        if (el.TryGetProperty("children", out var childProp) && childProp.ValueKind == JsonValueKind.Array)
        {
            kids = [];
            foreach (var child in childProp.EnumerateArray())
            {
                var converted = ConvertDbusElement(child);
                if (converted != null) kids.Add(converted);
            }
            if (kids.Count == 0) kids = null;
        }

        int itemId = el.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var idVal) ? idVal : -1;
        if (!string.IsNullOrEmpty(label) && (iconName != null || iconData != null || kids != null || itemId >= 0))
        {
            var key = NormalizeLabel(label);
            if (!string.IsNullOrEmpty(key) && !map.ContainsKey(key))
                map[key] = new DbusNodeData(iconName, iconData, kids, itemId);
        }

        // Recurse for icons/children at deeper levels.
        if (el.TryGetProperty("children", out var recProp) && recProp.ValueKind == JsonValueKind.Array)
            foreach (var child in recProp.EnumerateArray())
                CollectDbusNodes(child, map);
    }

    /// <summary>
    /// Converts a DBus-side <see cref="JsonElement"/> to a plain <c>object?</c> dictionary
    /// that can be serialised with <see cref="JsonSerializer"/>.
    /// Called while the owning <see cref="JsonDocument"/> is still alive.
    /// </summary>
    private static object? ConvertDbusElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Name == "children" && prop.Value.ValueKind == JsonValueKind.Array)
            {
                var kids = new List<object?>();
                foreach (var child in prop.Value.EnumerateArray())
                    kids.Add(ConvertDbusElement(child));
                dict[prop.Name] = kids;
            }
            else
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? (object)i : prop.Value.GetDouble(),
                    JsonValueKind.True   => true,
                    JsonValueKind.False  => false,
                    JsonValueKind.Null   => null,
                    _                   => prop.Value.ToString(),
                };
            }
        }
        return dict;
    }

    /// <summary>
    /// Recursively re-serialises an AT-SPI JSON element, injecting DBus icon fields and
    /// missing submenu children from <paramref name="nodeMap"/> where labels match.
    /// </summary>
    private static object? MergeDbusDataIntoNode(
        JsonElement el,
        Dictionary<string, DbusNodeData> nodeMap,
        ref bool anyAdded,
        Dictionary<int, (string BusName, string Path)>? idMapToUpdate)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        var dict = new Dictionary<string, object?>();
        foreach (var prop in el.EnumerateObject())
        {
            // Children must be handled with an explicit loop — ref params cannot be
            // captured by the lambda inside a LINQ .Select() call.
            if (prop.Name == "children" && prop.Value.ValueKind == JsonValueKind.Array)
            {
                var kids = new List<object?>();
                foreach (var child in prop.Value.EnumerateArray())
                    kids.Add(MergeDbusDataIntoNode(child, nodeMap, ref anyAdded, idMapToUpdate));
                dict[prop.Name] = kids;
            }
            else
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? (object)i : prop.Value.GetDouble(),
                    JsonValueKind.True   => true,
                    JsonValueKind.False  => false,
                    JsonValueKind.Null   => null,
                    _                   => prop.Value.ToString(),
                };
            }
        }

        if (dict.TryGetValue("label", out var labelObj) && labelObj is string lbl)
        {
            var key = NormalizeLabel(lbl);
            if (!string.IsNullOrEmpty(key) && nodeMap.TryGetValue(key, out var nd))
            {
                // ── Inject icons when AT-SPI has none ────────────────────────
                if (!dict.ContainsKey("icon-name") && nd.IconName != null)
                    { dict["icon-name"] = nd.IconName; anyAdded = true; }
                if (!dict.ContainsKey("icon-data") && nd.IconData != null)
                    { dict["icon-data"] = nd.IconData; anyAdded = true; }

                // ── Inject missing submenu children ──────────────────────────
                // Qt's AT-SPI bridge only populates lazy submenus after they are
                // opened. DBus AboutToShow triggers that population, so if the
                // AT-SPI node has no children but DBus does, copy them here.
                // The injected children keep their DBus integer IDs — execution
                // falls through to GlobalMenuExporter._activeMenu.EventAsync().
                if (!dict.ContainsKey("children") && nd.Children is { Count: > 0 } dbKids)
                    { dict["children"] = dbKids; anyAdded = true; }

                // ── Remap execution routing to dbusmenu ──────────────────────
                // AT-SPI window selection is unreliable on Wayland for multi-window
                // apps (Brave/Chromium): the X11 frame window ID we track via KWin
                // doesn't match the client window ID that Chromium's AT-SPI bridge
                // exposes, so strategy-1 (window-id attribute) never matches, and
                // StateActive (strategy-3) has a race with the focus event.
                // Solution: when we have a dbusmenu path that is per-window (assigned
                // atomically via _claimedMenuPaths), remap the AT-SPI synthetic ID
                // to use the dbusmenu proxy for execution.  GlobalMenuExporter checks
                // for BusName=="dbusmenu" and routes via _activeMenu.EventAsync().
                if (idMapToUpdate != null && nd.ItemId >= 0
                    && dict.TryGetValue("id", out var idObj) && idObj is int atspiId
                    && idMapToUpdate.ContainsKey(atspiId))
                {
                    idMapToUpdate[atspiId] = ("dbusmenu", nd.ItemId.ToString());
                    anyAdded = true;
                }
            }
        }

        return dict;
    }

    // Maps normalised lower-case menu labels to FreeDesktop standard icon names.
    // Only covers universally-recognised actions; app-specific items are left without icons.
    private static readonly Dictionary<string, string> s_labelIconMap = new(StringComparer.Ordinal)
    {
        // ── File ──────────────────────────────────────────────────────────────
        { "new",                    "document-new" },
        { "new file",               "document-new" },
        { "new document",           "document-new" },
        { "new window",             "window-new" },
        { "new tab",                "tab-new" },
        { "open",                   "document-open" },
        { "open file",              "document-open" },
        { "open folder",            "folder-open" },
        { "open location",          "document-open" },
        { "open recent",            "document-open-recent" },
        { "save",                   "document-save" },
        { "save as",                "document-save-as" },
        { "save all",               "document-save-all" },
        { "save a copy",            "document-save-as" },
        { "save copy as",           "document-save-as" },
        { "export",                 "document-export" },
        { "import",                 "document-import" },
        { "revert",                 "document-revert" },
        { "revert to saved",        "document-revert" },
        { "print",                  "document-print" },
        { "print preview",          "document-print-preview" },
        { "page setup",             "document-page-setup" },
        { "close",                  "document-close" },
        { "close window",           "window-close" },
        { "close tab",              "tab-close" },
        { "close all",              "document-close" },
        { "quit",                   "application-exit" },
        { "exit",                   "application-exit" },
        // ── Edit ──────────────────────────────────────────────────────────────
        { "undo",                   "edit-undo" },
        { "redo",                   "edit-redo" },
        { "cut",                    "edit-cut" },
        { "copy",                   "edit-copy" },
        { "copy as",                "edit-copy" },
        { "paste",                  "edit-paste" },
        { "paste special",          "edit-paste-special" },
        { "paste in place",         "edit-paste" },
        { "delete",                 "edit-delete" },
        { "remove",                 "list-remove" },
        { "clear",                  "edit-clear" },
        { "select all",             "edit-select-all" },
        { "select none",            "edit-select-none" },
        { "deselect all",           "edit-select-none" },
        { "invert selection",       "edit-select-invert" },
        { "find",                   "edit-find" },
        { "find next",              "go-down-search" },
        { "find previous",          "go-up-search" },
        { "find and replace",       "edit-find-replace" },
        { "replace",                "edit-find-replace" },
        { "preferences",            "preferences-system" },
        { "settings",               "configure" },
        { "configure",              "configure" },
        { "options",                "configure" },
        { "properties",             "document-properties" },
        { "rename",                 "edit-rename" },
        { "move to trash",          "user-trash" },
        { "move to recycle bin",    "user-trash" },
        // ── View ──────────────────────────────────────────────────────────────
        { "zoom in",                "zoom-in" },
        { "zoom out",               "zoom-out" },
        { "actual size",            "zoom-original" },
        { "original size",          "zoom-original" },
        { "zoom to fit",            "zoom-fit-best" },
        { "fit page",               "zoom-fit-best" },
        { "fit best",               "zoom-fit-best" },
        { "fit width",              "zoom-fit-width" },
        { "full screen",            "view-fullscreen" },
        { "fullscreen",             "view-fullscreen" },
        { "leave full screen",      "view-restore" },
        { "refresh",                "view-refresh" },
        { "reload",                 "view-refresh" },
        { "show toolbar",           "view-show-toolbar" },
        { "show statusbar",         "view-show-statusbar" },
        { "show status bar",        "view-show-statusbar" },
        { "show hidden files",      "view-hidden" },
        { "hidden files",           "view-hidden" },
        { "sort ascending",         "view-sort-ascending" },
        { "sort descending",        "view-sort-descending" },
        // ── Go / Navigate ─────────────────────────────────────────────────────
        { "back",                   "go-previous" },
        { "go back",                "go-previous" },
        { "forward",                "go-next" },
        { "go forward",             "go-next" },
        { "home",                   "go-home" },
        { "go home",                "go-home" },
        { "up",                     "go-up" },
        { "go up",                  "go-up" },
        { "next",                   "go-next" },
        { "previous",               "go-previous" },
        { "first",                  "go-first" },
        { "last",                   "go-last" },
        // ── Bookmarks ─────────────────────────────────────────────────────────
        { "add bookmark",           "bookmark-new" },
        { "bookmark this page",     "bookmark-new" },
        { "bookmark this folder",   "bookmark-new" },
        { "edit bookmarks",         "bookmarks-organize" },
        { "manage bookmarks",       "bookmarks-organize" },
        { "show bookmarks",         "bookmarks-organize" },
        // ── Tools ─────────────────────────────────────────────────────────────
        { "terminal",               "utilities-terminal" },
        { "open terminal",          "utilities-terminal" },
        { "open terminal here",     "utilities-terminal" },
        { "calculator",             "accessories-calculator" },
        { "run command",            "system-run" },
        { "scripts",                "system-run" },
        { "macro",                  "system-run" },
        { "macros",                 "system-run" },
        { "plugins",                "preferences-plugin" },
        { "extensions",             "preferences-plugin" },
        { "add-ons",                "preferences-plugin" },
        // ── Help ──────────────────────────────────────────────────────────────
        { "help",                   "help-contents" },
        { "help contents",          "help-contents" },
        { "open handbook",          "help-contents" },
        { "handbook",               "help-contents" },
        { "user guide",             "help-contents" },
        { "user manual",            "help-contents" },
        { "documentation",          "help-contents" },
        { "online help",            "help-contextual" },
        { "contextual help",        "help-contextual" },
        { "keyboard shortcuts",     "help-keybord-shortcuts" },
        { "what's this",            "help-whatsthis" },
        { "what's this?",           "help-whatsthis" },
        { "whats this",             "help-whatsthis" },
        { "report bug",             "tools-report-bug" },
        { "report a bug",           "tools-report-bug" },
        { "report a problem",       "tools-report-bug" },
        { "check for updates",      "system-software-update" },
        { "update",                 "system-software-update" },
        { "about",                  "help-about" },
        { "about qt",               "help-about" },
        { "about kde",              "help-about" },
        { "donate",                 "help-donate" },
        // ── Window ────────────────────────────────────────────────────────────
        { "minimize",               "window-minimize" },
        { "maximise",               "window-maximize" },
        { "maximize",               "window-maximize" },
        { "restore",                "window-restore" },
        { "split view",             "view-split-left-right" },
        { "split",                  "view-split-left-right" },
    };

    public ValueTask DisposeAsync()
    {
        _atspiConnection?.Dispose();
        _atspiConnection = null;
        return ValueTask.CompletedTask;
    }
}
