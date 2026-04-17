using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using DBusService.DBus;
using DBusService.Wayland;
using DBusService.X11;
using Tmds.DBus;

namespace DBusService;

public class Worker(ILogger<Worker> logger, GlobalMenuExporter exporter, IConfiguration configuration) : BackgroundService
{
    private const string RegistrarService = "com.canonical.AppMenu.Registrar";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Controls how much menu JSON is written to the log.
    private enum MenuLogMode { Full, Limited, None }
    private MenuLogMode GetMenuLogMode() =>
        configuration["GlobalMMMenu:MenuLogMode"] switch
        {
            "None"    => MenuLogMode.None,
            "Full"    => MenuLogMode.Full,
            _         => MenuLogMode.Limited,  // default: Limited
        };

    // Tracks which discovery method successfully served a menu for each X11 window.
    // Used to skip the full registrar/X11/PID/poll cycle on subsequent focus events.
    private enum MenuSourceType { DbusMenu, AtSpi, GtkMenu }
    private sealed record WindowMenuSource(
        MenuSourceType Type,
        string? Service      = null,   // DBus service name (DbusMenu fast path)
        string? Path         = null,   // DBus object path  (DbusMenu fast path)
        string? AtSpiBusName = null,   // AT-SPI bus unique name (AtSpi)
        string? DbusService  = null,   // DBus service used for icon merge (AtSpi windows)
        string? DbusPath     = null,   // DBus path used for icon merge (AtSpi windows)
        IReadOnlyDictionary<int, (string BusName, string Path)>? IdMap = null); // cached for instant re-focus

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── Build-identity banner — proves which binary is running ─────────────
        // Update this string whenever you want to confirm a fresh binary is loaded.
        logger.LogInformation("=== DBusService build: 2026-04-16-v26 (fix: dedup retry tasks + dispose AT-SPI ChildCount watchers) ===");

        using var connection = new Connection(Address.Session!);
        await connection.ConnectAsync();
        logger.LogInformation("Connected to D-Bus session bus");

        await connection.RegisterServiceAsync("com.kde.GlobalMMMenu");
        await connection.RegisterObjectAsync(exporter);
        logger.LogInformation("Registered com.kde.GlobalMMMenu on session bus");

        // D-Bus daemon proxy — used to find a window's bus name by PID when X11 props aren't set.
        var dbus = connection.CreateProxy<IFreedesktopDBus>("org.freedesktop.DBus", "/org/freedesktop/DBus");

        // ── Window monitor: detect X11 vs Wayland and create the right implementation ──
        // Check XDG_SESSION_TYPE first (set by the display manager); fall back to
        // checking WAYLAND_DISPLAY (set by the compositor itself).
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "";
        var isWayland   = sessionType.Equals("wayland", StringComparison.OrdinalIgnoreCase)
                       || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        logger.LogInformation("Session type: {S} → using {M} monitor",
            string.IsNullOrEmpty(sessionType) ? "(unknown)" : sessionType,
            isWayland ? "Wayland/KWin" : "X11");

        using IActiveWindowMonitor windowMonitor = isWayland
            ? new WaylandWindowMonitor(connection, logger)
            : new X11ActiveWindowMonitor();

        if (!windowMonitor.Connect())
        {
            logger.LogWarning(
                "Could not connect to window monitor — focus tracking is unavailable.");
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            return;
        }

        // Own com.canonical.AppMenu.Registrar so Qt/KDE apps call RegisterWindow with us.
        // We watch for NameOwnerChanged so if valapanel is running at startup and we can't
        // immediately take the name, we grab it the moment valapanel exits.
        var registrarImpl = new AppMenuRegistrarImpl();
        registrarImpl.Initialize(dbus, windowMonitor, logger);
        await connection.RegisterObjectAsync(registrarImpl);

        async Task TryAcquireRegistrarAsync()
        {
            try
            {
                await connection.RegisterServiceAsync(
                    RegistrarService,
                    ServiceRegistrationOptions.ReplaceExisting);
                logger.LogInformation("Acquired {Svc}", RegistrarService);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not acquire {Svc}: {M}", RegistrarService, ex.Message);
            }
        }

        // Watch: when the registrar name is released (newOwner == ""), claim it.
        // This handles the common case where valapanel holds the name at startup but exits later.
        await dbus.WatchNameOwnerChangedAsync(
            args =>
            {
                if (args.Name == RegistrarService && string.IsNullOrEmpty(args.NewOwner))
                {
                    logger.LogInformation("{Svc} owner released — attempting to acquire", RegistrarService);
                    _ = TryAcquireRegistrarAsync();
                }
            },
            ex => logger.LogDebug("NameOwnerChanged watch error: {M}", ex.Message));

        await TryAcquireRegistrarAsync();

        // Channel carries window ID + X11 KDE appmenu properties read at focus time.
        // The X11 event thread reads menu props directly from _KDE_NET_WM_APPMENU_SERVICE_NAME
        // and _KDE_NET_WM_APPMENU_OBJECT_PATbrave://flagsH — the same properties the native KDE global
        // menu applet uses. These work for ALL windows regardless of registrar restarts.
        var channel = Channel.CreateBounded<(uint WindowId, string? Service, string? Path)>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = true,
            });

        windowMonitor.ActiveWindowChanged += info => channel.Writer.TryWrite(info);

        var monitorTask  = Task.Run(() => windowMonitor.RunEventLoop(stoppingToken), stoppingToken);

        IDisposable? layoutSub   = null;
        IDisposable? propertySub = null;
        CancellationTokenSource? windowCts = null;

        // Cache of last known-good menu JSON keyed by window ID.
        // ConcurrentDictionary so the background prefetch worker can write to it safely.
        var menuCache    = new ConcurrentDictionary<uint, string>();
        // Cache of the fastest-known discovery route per window ID.
        // Once we confirm a window uses DBus or AT-SPI, we skip the full discovery loop
        // on subsequent focus events and go straight to the confirmed provider.
        var windowSources = new ConcurrentDictionary<uint, WindowMenuSource>();
        // Windows currently running a retry task — prevents duplicate tasks when a window
        // is re-focused before the previous retry loop finishes (Chromium/Brave pattern).
        var retryInFlight = new ConcurrentDictionary<uint, byte>();
        // Active AT-SPI ChildCount watchers keyed by window ID — must be disposed when
        // resolved or when the service stops to avoid permanent D-Bus subscriptions.
        var atspiWatchers = new ConcurrentDictionary<uint, IDisposable>();
        var prefetchEnabled = configuration.GetValue<bool>("GlobalMMMenu:EnablePrefetch", false);
        // AT-SPI reader — used as last resort for apps that have no dbusmenu object
        // (e.g. started before the registrar, or Qt builds that skip dbusmenu init).
        await using var atspi = new AtSpiMenuReader(logger);
        var atspiAvailable = await atspi.ConnectAsync();
        atspi.RichMetadata = configuration.GetValue<bool>("GlobalMMMenu:AtSpiRichMetadata", false);

        // GtkMenu reader — for GTK3/4 native Wayland apps that export org.gtk.Menus.
        // When com.canonical.AppMenu.Registrar is acquired, GTK3 hides its in-app GtkMenuBar
        // and exports the menu model at well-known paths on its session-bus D-Bus connection.
        // AT-SPI sees one fewer child (menu bar hidden), but org.gtk.Menus is always accessible.
        var gtkMenuReader = new GtkMenuReader(logger);

        var prefetchTask = prefetchEnabled
            ? Task.Run(() => PrefetchMenusAsync(connection, dbus, windowMonitor, registrarImpl, menuCache, windowSources, atspi, stoppingToken), stoppingToken)
            : Task.CompletedTask;
        if (!prefetchEnabled)
            logger.LogInformation("[Prefetch] Background menu discovery disabled (EnablePrefetch=false)");

        // ── Watchdog: detect and recover from a stuck-with-no-menu state ─────────
        // If the currently active window has produced no menu for ≥ 30 s, evict its
        // cached state and re-queue it into the discovery channel so the full cold-
        // path cycle runs again.  This recovers silently when D-Bus objects become
        // stale (e.g. kded5-appmenu restart, session bus hiccup) without requiring
        // a full service restart.
        var watchdogTask = Task.Run(async () =>
        {
            // Initial grace period: let startup discovery settle.
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { return; }

            uint trackedWindow = 0;
            int  noMenuTicks   = 0;
            const int TicksBeforeReset = 2; // 2 × 15 s = 30 s empty → reset

            while (!stoppingToken.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
                catch (OperationCanceledException) { return; }

                var activeId = windowMonitor.GetActiveWindow();
                if (activeId == 0) { trackedWindow = 0; noMenuTicks = 0; continue; }

                // Reset counter when focus moves to a different window.
                if (activeId != trackedWindow) { trackedWindow = activeId; noMenuTicks = 0; continue; }

                // Menu is present — nothing to do.
                var current = exporter.LastMenuJson;
                if (!string.IsNullOrEmpty(current) && current != "{}") { noMenuTicks = 0; continue; }

                noMenuTicks++;
                if (noMenuTicks < TicksBeforeReset)
                {
                    logger.LogDebug("[Watchdog] 0x{W:X8}: no menu (tick {T}/{N})", activeId, noMenuTicks, TicksBeforeReset);
                    continue;
                }

                logger.LogInformation(
                    "[Watchdog] 0x{W:X8}: no menu for ~{S}s — clearing cached state and retrying discovery",
                    activeId, (noMenuTicks + 1) * 15);
                noMenuTicks = 0; // reset so we wait another full cycle before trying again

                // Evict all cached state so the cold-path runs unconditionally.
                menuCache.TryRemove(activeId, out _);
                windowSources.TryRemove(activeId, out _);
                registrarImpl.RemoveRegistration(activeId);
                windowMonitor.ClearWindowMenuInfo((IntPtr)activeId);

                // Prod kded5-appmenu: it may hold showRequest data for this window
                // that it never re-sent after our registrar came online.
                try
                {
                    var kap = connection.CreateProxy<IKAppMenu>("org.kde.kappmenu", new ObjectPath("/KAppMenu"));
                    await kap.reconfigureAsync();
                    logger.LogDebug("[Watchdog] reconfigure() sent to kded5-appmenu");
                }
                catch (Exception ex) { logger.LogDebug("[Watchdog] reconfigure() skipped: {M}", ex.Message); }

                // Re-queue the active window — the main loop will run full discovery.
                channel.Writer.TryWrite((activeId, null, null));
            }
        }, stoppingToken);

        // ── Subscribe to org.kde.kappmenu.showRequest ─────────────────────────────
        // Native Wayland KDE apps (Konsole, Dolphin, etc.) use the Wayland appmenu
        // protocol instead of D-Bus RegisterWindow. kded5-appmenu bridges that protocol
        // and fires showRequest with the fully-resolved service+path whenever a window
        // with a known menu gains focus. This is the ONLY reliable path for native
        // Wayland apps that started before our service and never called RegisterWindow.
        //
        // When showRequest fires we cache the result against the current active window
        // so the normal channel loop can pick it up or display it directly if the channel
        // has already processed the focus event without finding a menu.
        IDisposable? kappMenuSub = null;
        try
        {
            var kappmenu = connection.CreateProxy<IKAppMenu>("org.kde.kappmenu", new ObjectPath("/KAppMenu"));
            kappMenuSub = await kappmenu.WatchShowRequestAsync(
                args =>
                {
                    var svc  = args.Service;
                    var path = args.MenuObjectPath.ToString();
                    if (string.IsNullOrEmpty(svc) || string.IsNullOrEmpty(path) || path == "/") return;

                    var activeId = windowMonitor.GetActiveWindow();
                    if (activeId == 0) return;

                    logger.LogInformation("[KAppMenu] showRequest → {S} {P} (active=0x{W:X8})", svc, path, activeId);

                    // Store in window monitor / registrar so the main loop fast-paths on re-focus.
                    windowMonitor.SetWindowMenuInfo((IntPtr)activeId, svc, path);
                    registrarImpl.StoreResolved(activeId, svc, path);

                    // If the main loop already gave up on this window (no menu found),
                    // immediately serve the menu from kded5-appmenu's data.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var ct = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            ct.CancelAfter(TimeSpan.FromSeconds(5));
                            var (dbJson, dbMenu) = await FetchDbusMenuJsonAsync(connection, svc, path, ct.Token);
                            if (dbJson != null && dbMenu != null)
                            {
                                exporter.Update(dbJson, dbMenu);
                                menuCache[activeId] = dbJson;
                                windowSources[activeId] = new WindowMenuSource(MenuSourceType.DbusMenu, Service: svc, Path: path);
                                logger.LogInformation("[KAppMenu] Served menu for 0x{W:X8} via showRequest", activeId);
                            }
                        }
                        catch (Exception ex) { logger.LogDebug("[KAppMenu] showRequest fetch failed: {M}", ex.Message); }
                    }, stoppingToken);
                },
                ex => logger.LogDebug("[KAppMenu] showRequest watch error: {M}", ex.Message));
            logger.LogInformation("[KAppMenu] Subscribed to org.kde.kappmenu.showRequest");

            // Trigger kded5-appmenu to re-read its state and re-call RegisterWindow
            // for all windows it already knows about. This covers apps (Konsole, Dolphin,
            // etc.) that started BEFORE our service and registered with the previous
            // registrar instance — without this they would never appear in our registrar.
            try
            {
                await kappmenu.reconfigureAsync();
                logger.LogInformation("[KAppMenu] reconfigure() sent — kded5-appmenu will re-register known windows");
            }
            catch (Exception rex)
            {
                logger.LogDebug("[KAppMenu] reconfigure() failed (non-fatal): {M}", rex.Message);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("[KAppMenu] org.kde.kappmenu not available (non-fatal): {M}", ex.Message);
        }

        try
        {
            await foreach (var (windowId, x11Service, x11Path) in channel.Reader.ReadAllAsync(stoppingToken))
            {
                // Cancel any background work (e.g. LayoutUpdated re-fetch) from the previous window.
                windowCts?.Cancel();
                windowCts?.Dispose();
                windowCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                layoutSub?.Dispose();   layoutSub   = null;
                propertySub?.Dispose(); propertySub = null;

                logger.LogInformation("Active window → 0x{WindowId:X8}  [{Name}]",
                    windowId, windowMonitor.GetWindowName((IntPtr)windowId) ?? "?");
                exporter.Update("{}", null);

                // Resolve service + path (priority order):
                // 1. Live registrar registry — populated via RegisterWindow in the current session.
                //    Always checked first: X11 props may carry a stale bus name from a previous run.
                // 2. X11 window properties — used when registrar has no complete entry.
                // 3. PID-based discovery — last resort, probes /com/canonical/menu/<windowId>
                var service = x11Service;
                var path    = x11Path;

                // ── Fast path: window with a previously confirmed menu source ─────────────
                // If we already know how this window exposes its menu, skip the full
                // registrar / X11-prop / PID-scan / 2-second-poll cycle entirely.
                if (windowSources.TryGetValue(windowId, out var knownSrc))
                {
                    if (knownSrc.Type == MenuSourceType.AtSpi && atspiAvailable
                        && !string.IsNullOrEmpty(knownSrc.AtSpiBusName))
                    {
                        // ── Icon cache hit: serve fully-enriched menu instantly ───────────────
                        // After the first focus the merged+enriched JSON (icons + shortcuts +
                        // lazy submenus) is stored in menuCache and the idMap is stored in
                        // windowSources. On re-focus we serve from cache immediately — zero
                        // extra D-Bus round-trips on the focus thread — then refresh the
                        // AT-SPI tree + re-merge in the background to pick up any changes.
                        if (menuCache.TryGetValue(windowId, out var cachedFull) && knownSrc.IdMap != null)
                        {
                            var cachedIdMap = new Dictionary<int, (string BusName, string Path)>(knownSrc.IdMap);
                            IDbusMenu? cachedProxy = null;
                            if (!string.IsNullOrEmpty(knownSrc.DbusService) && !string.IsNullOrEmpty(knownSrc.DbusPath))
                                cachedProxy = connection.CreateProxy<IDbusMenu>(
                                    knownSrc.DbusService!, new ObjectPath(knownSrc.DbusPath!));
                            exporter.UpdateAtSpi(cachedFull, atspi, cachedIdMap, cachedProxy);
                            logger.LogInformation("  0x{W:X8}: fast-path AT-SPI (icons cached)", windowId);

                            // Background: refresh AT-SPI + re-merge DBus to catch menu structure changes.
                            var fWinId = windowId; var fSrc = knownSrc;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    using var rCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                    rCts.CancelAfter(TimeSpan.FromSeconds(15));
                                    var (freshJson, freshMap) = await atspi.GetMenuJsonFromConnectionAsync(
                                        fSrc.AtSpiBusName!, rCts.Token);
                                    if (string.IsNullOrEmpty(freshJson) || freshJson == "{}") return;

                                    if (!string.IsNullOrEmpty(fSrc.DbusService) && !string.IsNullOrEmpty(fSrc.DbusPath))
                                    {
                                        using var mCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                        mCts.CancelAfter(TimeSpan.FromSeconds(10));
                                        var (dbJson, dbProxy) = await FetchDbusMenuJsonAsync(
                                            connection, fSrc.DbusService!, fSrc.DbusPath!, mCts.Token);
                                        if (dbJson != null && dbProxy != null)
                                        {
                                            var merged = AtSpiMenuReader.MergeDbusIconsIntoAtSpiJson(freshJson, dbJson);
                                            if (merged != null)
                                            {
                                                exporter.UpdateAtSpi(merged, atspi, freshMap, dbProxy);
                                                menuCache[fWinId] = merged;
                                                using var eCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                                eCts.CancelAfter(TimeSpan.FromSeconds(20));
                                                var enriched = await atspi.EnrichMenuJsonAsync(merged, freshMap, eCts.Token);
                                                if (enriched != null)
                                                {
                                                    exporter.UpdateAtSpi(enriched, atspi, freshMap, dbProxy);
                                                    menuCache[fWinId] = enriched;
                                                }
                                                windowSources[fWinId] = fSrc with { IdMap = freshMap };
                                                logger.LogDebug("  0x{W:X8}: fast-path icon cache refreshed", fWinId);
                                                return;
                                            }
                                        }
                                    }
                                    // No DBus — enrich shortcuts only.
                                    using var eCts2 = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                    eCts2.CancelAfter(TimeSpan.FromSeconds(20));
                                    var enrichedOnly = await atspi.EnrichMenuJsonAsync(freshJson, freshMap, eCts2.Token);
                                    var finalOnly = enrichedOnly ?? freshJson;
                                    exporter.UpdateAtSpi(finalOnly, atspi, freshMap);
                                    menuCache[fWinId] = finalOnly;
                                    windowSources[fWinId] = fSrc with { IdMap = freshMap };
                                }
                                catch (Exception ex)
                                {
                                    logger.LogDebug("  0x{W:X8}: fast-path icon-cache refresh failed: {M}", fWinId, ex.Message);
                                }
                            }, stoppingToken);
                            continue;
                        }

                        // No icon cache yet — do a full AT-SPI scan and background DBus merge.
                        using var fastCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        fastCts.CancelAfter(TimeSpan.FromSeconds(15));
                        try
                        {
                            var (fJson, fMap) = await atspi.GetMenuJsonFromConnectionAsync(knownSrc.AtSpiBusName!, fastCts.Token);
                            if (!string.IsNullOrEmpty(fJson) && fJson != "{}")
                            {
                                logger.LogInformation("  0x{W:X8}: fast-path AT-SPI (bus={B})", windowId, knownSrc.AtSpiBusName);
                                exporter.UpdateAtSpi(fJson, atspi, fMap);
                                menuCache[windowId] = fJson;
                                var fEnrichJson = fJson; var fEnrichMap = fMap; var fWinId2 = windowId;
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        using var eCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                        eCts.CancelAfter(TimeSpan.FromSeconds(30));
                                        var enriched = await atspi.EnrichMenuJsonAsync(fEnrichJson, fEnrichMap, eCts.Token);
                                        if (enriched != null)
                                        {
                                            exporter.UpdateAtSpi(enriched, atspi, fEnrichMap);
                                            menuCache[fWinId2] = enriched;
                                            logger.LogDebug("  0x{W:X8}: fast-path AT-SPI shortcuts enriched", fWinId2);
                                        }
                                    }
                                    catch (Exception ex) { logger.LogDebug("  0x{W:X8}: fast-path enrichment failed: {M}", fWinId2, ex.Message); }
                                }, stoppingToken);
                                continue;
                            }
                        }
                        catch
                        {
                            // AT-SPI bus name is stale (app restarted) — discard and re-discover.
                            windowSources.TryRemove(windowId, out _);
                        }
                    }
                    else if (knownSrc.Type == MenuSourceType.DbusMenu
                             && !string.IsNullOrEmpty(knownSrc.Service)
                             && !string.IsNullOrEmpty(knownSrc.Path))
                    {
                        // Pre-seed service/path so PID-scan, 2-second poll, and AT-SPI fallback
                        // blocks are all skipped (they only run when service/path are empty).
                        service = knownSrc.Service!;
                        path    = knownSrc.Path!;
                        logger.LogInformation("  0x{W:X8}: fast-path DBus (cached {S})", windowId, service);
                    }
                    else if (knownSrc.Type == MenuSourceType.GtkMenu)
                    {
                        // GTK3/4 org.gtk.Menus — re-read each time (GMenuModel is live).
                        var gtkBus = knownSrc.AtSpiBusName ?? string.Empty;
                        if (!string.IsNullOrEmpty(gtkBus) && menuCache.TryGetValue(windowId, out var cachedGtk))
                        {
                            // Re-serve cached JSON instantly then re-read in background for changes.
                            // Re-use the existing gtkMenuReader instance (no state).
                            var pid2 = windowMonitor.GetWindowPid((IntPtr)windowId);
                            if (pid2 != 0)
                            {
                                using var stCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                stCts.CancelAfter(TimeSpan.FromSeconds(5));
                                var (freshGtkJson, freshGtkIdMap, _) = await gtkMenuReader.GetMenuJsonForConnectionAsync(
                                    connection, dbus, pid2, stCts.Token);
                                if (!string.IsNullOrEmpty(freshGtkJson) && freshGtkJson != "{}")
                                {
                                    exporter.UpdateGtkMenu(freshGtkJson, gtkMenuReader, connection, freshGtkIdMap);
                                    menuCache[windowId] = freshGtkJson;
                                    logger.LogInformation("  0x{W:X8}: fast-path GtkMenu (refreshed)", windowId);
                                    continue;
                                }
                            }
                            // Stale — discard and fall through to full discovery.
                            windowSources.TryRemove(windowId, out _);
                        }
                    }
                }

                // ── AT-SPI first: show menu structure immediately ────────────────────────────
                // AT-SPI is always available for Qt/KDE apps and returns the menu tree without
                // needing DBus registrar registration. We display it right away, then try DBus
                // in the background to enrich it with real icon-name / icon-data fields.
                // If DBus is unavailable or times out, EnrichMenuJsonAsync applies shortcuts and
                // the built-in FreeDesktop label-icon table as a fallback.
                if (atspiAvailable)
                {
                    var atspiPid = windowMonitor.GetWindowPid((IntPtr)windowId);
                    if (atspiPid != 0)
                    {
                        using var atspiFirstCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        atspiFirstCts.CancelAfter(TimeSpan.FromSeconds(15));
                        string? firstScanBusName = null; // captured even when scan returns no menu
                        try
                        {
                            var (atspiJson, atspiIdMap, atspiBusName) =
                                await atspi.GetMenuJsonForPidAsync(atspiPid, atspiFirstCts.Token);
                            firstScanBusName = atspiBusName; // keep for event-driven retry below
                            if (!string.IsNullOrEmpty(atspiJson) && atspiJson != "{}")
                            {
                                logger.LogInformation(
                                    "  0x{W:X8}: AT-SPI menu (pid={P}) — showing immediately, fetching DBus icons in background",
                                    windowId, atspiPid);
                                exporter.UpdateAtSpi(atspiJson, atspi, atspiIdMap);
                                menuCache[windowId] = atspiJson;
                                if (!string.IsNullOrEmpty(atspiBusName))
                                    windowSources[windowId] = new WindowMenuSource(MenuSourceType.AtSpi, AtSpiBusName: atspiBusName);

                                // Background: find the DBus menu, fetch it, merge its icons onto the
                                // AT-SPI JSON structure. Falls back to shortcut + label-icon-table
                                // enrichment when no DBus menu is found or icons are missing.
                                var bgAtSpiJson = atspiJson;
                                var bgIdMap     = atspiIdMap;
                                var bgWinId     = windowId;
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        using var bgCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                        bgCts.CancelAfter(TimeSpan.FromSeconds(10));

                                        // Check registrar first (fastest), then fall back to PID scan.
                                        string? dbSvc = null, dbPath = null;
                                        var (rsi, rpi) = registrarImpl.TryGetMenu(bgWinId);
                                        if (!string.IsNullOrEmpty(rsi) && !string.IsNullOrEmpty(rpi) && rpi != "/")
                                        {
                                            dbSvc  = rsi;
                                            dbPath = rpi;
                                        }
                                        else
                                        {
                                            (dbSvc, dbPath) = await FindMenuByPidAsync(
                                                connection, dbus, windowMonitor, registrarImpl, bgWinId, null, bgCts.Token);
                                        }

                                        if (!string.IsNullOrEmpty(dbSvc) && !string.IsNullOrEmpty(dbPath) && dbPath != "/")
                                        {
                                            // Fetch the full DBus layout with 3-depth AboutToShow priming
                                            // so lazy submenus (Dolphin "Create New" etc.) are populated.
                                            var (dbJson, dbMenu) = await FetchDbusMenuJsonAsync(
                                                connection, dbSvc, dbPath, bgCts.Token);
                                            if (dbJson != null && dbMenu != null)
                                            {
                                                var merged = AtSpiMenuReader.MergeDbusIconsIntoAtSpiJson(bgAtSpiJson, dbJson);
                                                if (merged != null)
                                                {
                                                    exporter.UpdateAtSpi(merged, atspi, bgIdMap, dbMenu);
                                                    menuCache[bgWinId] = merged;
                                                    logger.LogDebug(
                                                        "  0x{W:X8}: DBus data merged onto AT-SPI menu ({S})", bgWinId, dbSvc);

                                                    // Enrich with shortcuts — use a fresh CTS since bgCts may
                                                    // be near its 10 s limit after the DBus fetch above.
                                                    using var eCts2 = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                                    eCts2.CancelAfter(TimeSpan.FromSeconds(20));
                                                    var enrichedMerged = await atspi.EnrichMenuJsonAsync(merged, bgIdMap, eCts2.Token);
                                                    if (enrichedMerged != null)
                                                    {
                                                        exporter.UpdateAtSpi(enrichedMerged, atspi, bgIdMap, dbMenu);
                                                        menuCache[bgWinId] = enrichedMerged;
                                                        logger.LogDebug("  0x{W:X8}: merged menu enriched with shortcuts", bgWinId);
                                                    }
                                                    // Store DBus location + idMap so re-focus serves the
                                                    // fully-enriched menu from cache with zero D-Bus calls.
                                                    windowSources[bgWinId] = new WindowMenuSource(
                                                        MenuSourceType.AtSpi,
                                                        AtSpiBusName: atspiBusName,
                                                        DbusService: dbSvc,
                                                        DbusPath: dbPath,
                                                        IdMap: bgIdMap);
                                                    return;
                                                }
                                            }
                                            logger.LogDebug(
                                                "  0x{W:X8}: DBus menu found but no data to merge — using label table", bgWinId);
                                        }
                                        else
                                        {
                                            logger.LogDebug(
                                                "  0x{W:X8}: no DBus menu found for icon enrichment", bgWinId);
                                        }

                                        // No DBus icons available — enrich with shortcuts + FreeDesktop label-icon table.
                                        using var eCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                        eCts.CancelAfter(TimeSpan.FromSeconds(20));
                                        var enriched = await atspi.EnrichMenuJsonAsync(bgAtSpiJson, bgIdMap, eCts.Token);
                                        if (enriched != null)
                                        {
                                            exporter.UpdateAtSpi(enriched, atspi, bgIdMap);
                                            menuCache[bgWinId] = enriched;
                                            logger.LogDebug("  0x{W:X8}: AT-SPI menu enriched with shortcuts/icons", bgWinId);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        logger.LogDebug("  0x{W:X8}: background icon enrichment failed: {M}", bgWinId, ex.Message);
                                    }
                                }, stoppingToken);

                                continue; // AT-SPI menu is displayed; skip DBus discovery below.
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug("  0x{W:X8}: AT-SPI first-try failed: {M}", windowId, ex.Message);
                        }

                        // ── org.gtk.Menus fallback for native Wayland GTK3/4 apps ─────────────────
                        // GTK3 hides the in-app GtkMenuBar (making AT-SPI see 1 fewer child) but
                        // exports the same menu via org.gtk.Menus on the session bus.
                        // Run in background — MUST NOT block the main event loop.
                        if (!windowSources.ContainsKey(windowId))
                        {
                            var gtkWinId = windowId;
                            var gtkPid   = atspiPid;
                            _ = Task.Run(async () =>
                            {
                                logger.LogDebug("  0x{W:X8}: probing org.gtk.Menus in background (pid={P})", gtkWinId, gtkPid);
                                try
                                {
                                    using var gtkCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                    gtkCts.CancelAfter(TimeSpan.FromSeconds(5));
                                    var (gtkJson, gtkIdMap, gtkBus) = await gtkMenuReader.GetMenuJsonForConnectionAsync(
                                        connection, dbus, gtkPid, gtkCts.Token);
                                    if (string.IsNullOrEmpty(gtkJson) || gtkJson == "{}") return;
                                    if (windowSources.ContainsKey(gtkWinId)) return;
                                    logger.LogInformation("  0x{W:X8}: org.gtk.Menus menu (pid={P}) — showing", gtkWinId, gtkPid);
                                    exporter.UpdateGtkMenu(gtkJson, gtkMenuReader, connection, gtkIdMap);
                                    menuCache[gtkWinId] = gtkJson;
                                    if (!string.IsNullOrEmpty(gtkBus))
                                        windowSources[gtkWinId] = new WindowMenuSource(MenuSourceType.GtkMenu, AtSpiBusName: gtkBus);
                                }
                                catch (Exception gtkEx)
                                {
                                    logger.LogDebug("  0x{W:X8}: org.gtk.Menus probe failed: {M}", gtkWinId, gtkEx.Message);
                                }
                            }, stoppingToken);
                        }
                        // Qt apps: bridge loads ~1 s after service start.
                        // GTK apps (e.g. HandBrake): the GtkMenuBar widget may take several seconds
                        // to appear in the AT-SPI tree after GTK lazy-realizes it.
                        // Poll for up to 30 s in increasing intervals — covers both cases without
                        // flooding the bus on apps that genuinely have no menu.
                        // Only fires once per window — retryInFlight prevents duplicate tasks
                        // when the window is re-focused while a retry loop is already running
                        // (Chromium/Brave re-focuses while late-RegisterWindow is pending).
                        if (!windowSources.ContainsKey(windowId) && retryInFlight.TryAdd(windowId, 0))
                        {
                            var retryWinId  = windowId;
                            var retryPid    = atspiPid;
                            var retryBusName = firstScanBusName; // may be null if first scan threw
                            logger.LogDebug("  0x{W:X8}: scheduling AT-SPI retry task (pid={P})", windowId, atspiPid);
                            _ = Task.Run(async () =>
                            {
                            try
                            {
                                // Delays: 500ms × 6, then 2s × 12 = 3s + 24s = 27s total, ~18 probes.
                                var delays = Enumerable.Repeat(500, 6).Concat(Enumerable.Repeat(2000, 12));
                                int attempt = 0;
                                foreach (var delayMs in delays)
                                {
                                    attempt++;
                                    try { await Task.Delay(delayMs, stoppingToken); }
                                    catch (OperationCanceledException) { return; }

                                    // Stop if a newer focus event already resolved this window.
                                    if (windowSources.ContainsKey(retryWinId)) return;

                                    logger.LogDebug("  0x{W:X8}: AT-SPI retry #{A} (pid={P})", retryWinId, attempt, retryPid);
                                    try
                                    {
                                        using var rCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                        rCts.CancelAfter(TimeSpan.FromSeconds(10));
                                        var (rJson, rMap, rBus) = await atspi.GetMenuJsonForPidAsync(retryPid, rCts.Token);
                                        if (string.IsNullOrEmpty(rJson) || rJson == "{}")
                                        {
                                            // AT-SPI returned nothing — also check the DBus registrar.
                                            // Chromium/Brave calls RegisterWindow several seconds after their
                                            // window gets focus; by the time we reach this retry loop the app
                                            // may have finally registered.  Check the direct registry first
                                            // (fast), then fall back to a full PID scan every 3rd attempt
                                            // to handle the common case where Chromium's Qt-internal winId
                                            // doesn't match the X11 frame window ID we track.
                                            var (retrySvc, retryPath) = registrarImpl.TryGetMenu(retryWinId);
                                            if (string.IsNullOrEmpty(retrySvc) && attempt % 3 == 0)
                                            {
                                                using var dbsCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                                dbsCts.CancelAfter(TimeSpan.FromSeconds(8));
                                                (retrySvc, retryPath) = await FindMenuByPidAsync(
                                                    connection, dbus, windowMonitor, registrarImpl,
                                                    retryWinId, null, dbsCts.Token);
                                            }

                                            if (!string.IsNullOrEmpty(retrySvc) && !string.IsNullOrEmpty(retryPath) && retryPath != "/")
                                            {
                                                logger.LogInformation("  0x{W:X8}: DBus registrar entry found on retry #{A} — fetching ({S})", retryWinId, attempt, retrySvc);
                                                using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                                fetchCts.CancelAfter(TimeSpan.FromSeconds(10));
                                                var (dbJson, dbMenu) = await FetchDbusMenuJsonAsync(connection, retrySvc, retryPath, fetchCts.Token);
                                                if (dbJson != null && dbMenu != null)
                                                {
                                                    exporter.Update(dbJson, dbMenu);
                                                    menuCache[retryWinId] = dbJson;
                                                    windowSources[retryWinId] = new WindowMenuSource(MenuSourceType.DbusMenu, Service: retrySvc, Path: retryPath);
                                                    windowMonitor.SetWindowMenuInfo((IntPtr)retryWinId, retrySvc, retryPath);
                                                    logger.LogInformation("  0x{W:X8}: DBus menu served via late RegisterWindow (retry #{A})", retryWinId, attempt);
                                                    return;
                                                }
                                            }

                                            logger.LogDebug("  0x{W:X8}: retry #{A} — still no menu", retryWinId, attempt);
                                            continue;
                                        }

                                        logger.LogInformation("  0x{W:X8}: AT-SPI widget appeared (retry {A}) — showing menu", retryWinId, attempt);
                                        exporter.UpdateAtSpi(rJson, atspi, rMap);
                                        menuCache[retryWinId] = rJson;
                                        if (!string.IsNullOrEmpty(rBus))
                                            windowSources[retryWinId] = new WindowMenuSource(MenuSourceType.AtSpi, AtSpiBusName: rBus);

                                        // Enrich with shortcuts in a further background step.
                                        using var eCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                        eCts.CancelAfter(TimeSpan.FromSeconds(20));
                                        var enriched = await atspi.EnrichMenuJsonAsync(rJson, rMap, eCts.Token);
                                        if (enriched != null)
                                        {
                                            exporter.UpdateAtSpi(enriched, atspi, rMap);
                                            menuCache[retryWinId] = enriched;
                                        }
                                        return;
                                    }
                                    catch (Exception ex) { logger.LogDebug("  0x{W:X8}: AT-SPI retry #{A} exception: {M}", retryWinId, attempt, ex.Message); }
                                }
                                logger.LogDebug("  0x{W:X8}: AT-SPI retry exhausted after {A} attempts — no menu found", retryWinId, attempt);

                                // ── Event-driven fallback: subscribe to AT-SPI PropertiesChanged ──────────
                                // GTK apps (e.g. HandBrake) may realize their GtkMenuBar widget lazily —
                                // well after the 27 s polling window above.  WatchAppNodeChildrenChangedAsync
                                // fires the INSTANT ChildCount increases on the application node, so we
                                // catch the menu bar appearing regardless of timing.
                                if (retryBusName != null && !windowSources.ContainsKey(retryWinId))
                                {
                                    logger.LogDebug("  0x{W:X8}: subscribing to AT-SPI ChildCount watcher on {Bus}", retryWinId, retryBusName);
                                    try
                                    {
                                        var watcher = await atspi.WatchAppNodeChildrenChangedAsync(
                                            retryBusName,
                                            () => _ = Task.Run(async () =>
                                            {
                                                // Auto-dispose watcher after first fire — one-shot.
                                                if (atspiWatchers.TryRemove(retryWinId, out var fired)) fired.Dispose();
                                                if (windowSources.ContainsKey(retryWinId)) return;
                                                logger.LogInformation("  0x{W:X8}: AT-SPI ChildCount changed — scanning after lazy realization", retryWinId);
                                                try
                                                {
                                                    using var wCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                                    wCts.CancelAfter(TimeSpan.FromSeconds(10));
                                                    var (wJson, wMap, wBus) = await atspi.GetMenuJsonForPidAsync(retryPid, wCts.Token);
                                                    if (string.IsNullOrEmpty(wJson) || wJson == "{}") { logger.LogDebug("  0x{W:X8}: ChildCount event — scan still no menu", retryWinId); return; }
                                                    logger.LogInformation("  0x{W:X8}: AT-SPI lazy menu appeared (event-driven) — showing menu", retryWinId);
                                                    exporter.UpdateAtSpi(wJson, atspi, wMap);
                                                    menuCache[retryWinId] = wJson;
                                                    if (!string.IsNullOrEmpty(wBus))
                                                        windowSources[retryWinId] = new WindowMenuSource(MenuSourceType.AtSpi, AtSpiBusName: wBus);
                                                    // Enrich with shortcuts in a further background step.
                                                    using var weCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                                    weCts.CancelAfter(TimeSpan.FromSeconds(20));
                                                    var wEnriched = await atspi.EnrichMenuJsonAsync(wJson, wMap, weCts.Token);
                                                    if (wEnriched != null) { exporter.UpdateAtSpi(wEnriched, atspi, wMap); menuCache[retryWinId] = wEnriched; }
                                                }
                                                catch (Exception wEx) { logger.LogDebug("  0x{W:X8}: ChildCount event scan failed: {M}", retryWinId, wEx.Message); }
                                            }, stoppingToken),
                                            ex => logger.LogDebug("  0x{W:X8}: ChildCount watcher error: {M}", retryWinId, ex.Message));
                                        if (watcher != null)
                                            atspiWatchers[retryWinId] = watcher;
                                    }
                                    catch (Exception wex) { logger.LogDebug("  0x{W:X8}: ChildCount watcher setup failed: {M}", retryWinId, wex.Message); }
                                }
                            }
                            finally
                            {
                                retryInFlight.TryRemove(retryWinId, out _);
                            }
                            }, stoppingToken);
                        }
                    }
                    else
                    {
                        // PID unavailable (e.g. native Wayland or XWayland without _NET_WM_PID).
                        // Fall back to scanning all AT-SPI connections for the window that is
                        // currently in ACTIVE state — this finds the focused app without needing PID.
                        logger.LogDebug("  0x{W:X8}: PID=0, trying AT-SPI active-window scan", windowId);
                        using var activeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        activeCts.CancelAfter(TimeSpan.FromSeconds(10));
                        try
                        {
                            var (activeJson, activeIdMap, activeBusName) =
                                await atspi.GetMenuJsonForActiveWindowAsync(activeCts.Token);
                            if (!string.IsNullOrEmpty(activeJson) && activeJson != "{}")
                            {
                                logger.LogInformation(
                                    "  0x{W:X8}: AT-SPI active-scan menu — showing immediately",
                                    windowId);
                                exporter.UpdateAtSpi(activeJson, atspi, activeIdMap);
                                menuCache[windowId] = activeJson;
                                if (!string.IsNullOrEmpty(activeBusName))
                                    windowSources[windowId] = new WindowMenuSource(MenuSourceType.AtSpi, AtSpiBusName: activeBusName);

                                // Background DBus-icon merge (same pipeline as PID path).
                                var bgJson2  = activeJson;
                                var bgMap2   = activeIdMap;
                                var bgWinId2 = windowId;
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        using var bgCts2 = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                        bgCts2.CancelAfter(TimeSpan.FromSeconds(15));
                                        using var eCts3 = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                        eCts3.CancelAfter(TimeSpan.FromSeconds(20));
                                        var enriched2 = await atspi.EnrichMenuJsonAsync(bgJson2, bgMap2, eCts3.Token);
                                        if (enriched2 != null)
                                        {
                                            exporter.UpdateAtSpi(enriched2, atspi, bgMap2);
                                            menuCache[bgWinId2] = enriched2;
                                            logger.LogDebug("  0x{W:X8}: active-scan menu enriched with shortcuts", bgWinId2);
                                        }
                                    }
                                    catch (Exception ex2) { logger.LogDebug("  0x{W:X8}: active-scan enrichment failed: {M}", bgWinId2, ex2.Message); }
                                }, stoppingToken);

                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug("  0x{W:X8}: AT-SPI active-scan failed: {M}", windowId, ex.Message);
                        }
                    }
                }

                // ── AT-SPI unavailable or returned no menu — fall back to DBus discovery ───────
                // Always check the live registrar first. A fully-resolved entry is always preferred
                // over X11 props, which may carry a stale unique bus name (":1.XXXX") from a previous
                // service run — using a dead bus name triggers a ServiceUnknown error downstream.
                var (rs, rp) = registrarImpl.TryGetMenu(windowId);
                if (!string.IsNullOrEmpty(rs) && !string.IsNullOrEmpty(rp) && rp != "/")
                {
                    service = rs;
                    path    = rp;
                    logger.LogInformation("  0x{W:X8}: menu from registrar ({S})", windowId, rs);
                }
                else if (!string.IsNullOrEmpty(service) && !string.IsNullOrEmpty(path) && path != "/")
                {
                    // X11 props are present; registrar has no complete entry.
                    logger.LogInformation("  0x{W:X8}: menu from X11 props (registrar: {RegStatus})",
                        windowId, string.IsNullOrEmpty(rp) ? "no entry" : $"path={rp} service still resolving");
                }
                else if (!string.IsNullOrEmpty(rp) && rp != "/")
                {
                    // Registrar has a path but service name resolution is still in progress.
                    path    = rp;
                    service = rs;  // null — will trigger PID discovery below
                    logger.LogInformation("  0x{W:X8}: registrar path known ({P}), service still resolving", windowId, rp);
                }
                else
                {
                    // Log all registered window IDs to help diagnose ID mismatches.
                    var regIds = registrarImpl.GetAllRegistrations();
                    if (regIds.Length > 0)
                        logger.LogInformation("  0x{W:X8}: not in registrar. Registered: [{Ids}]",
                            windowId, string.Join(", ", regIds.Select(r => $"0x{r.WindowId:X8}=>{r.Path}")));
                    else
                        logger.LogInformation("  0x{W:X8}: not in registrar (registrar is empty)", windowId);
                }

                // PID discovery: used when registrar has no entry, or when it has a path but
                // the async service resolution hasn't completed yet (pass the known path).
                if (string.IsNullOrEmpty(service) || string.IsNullOrEmpty(path) || path == "/")
                {
                    var knownPath = (string.IsNullOrEmpty(path) || path == "/") ? null : path;
                    var (ds, dp) = await FindMenuByPidAsync(connection, dbus, windowMonitor, registrarImpl, windowId, knownPath, windowCts!.Token);
                    if (!string.IsNullOrEmpty(ds) && !string.IsNullOrEmpty(dp))
                    {
                        service = ds;
                        path    = dp;
                        // Write X11 props back so next focus resolves instantly without PID scan.
                        windowMonitor.SetWindowMenuInfo((IntPtr)windowId, service, path);
                        logger.LogDebug("  0x{W:X8}: menu found via PID discovery ({S}) — props restored", windowId, service);
                    }
                }

                if (string.IsNullOrEmpty(service) || string.IsNullOrEmpty(path) || path == "/")
                {
                    logger.LogDebug("  0x{W:X8}: no menu on first check — polling up to 2 s", windowId);

                    // New/detached windows (e.g. browser tabs torn off) may not have
                    // registered their menu yet. Poll every 250 ms for up to 2 seconds
                    // without touching the rest of the loop — a new focus event cancels
                    // this naturally when the channel receives the next window ID.
                    using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    pollCts.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        while (!pollCts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(250, pollCts.Token);

                            // Check if the channel already has a newer window waiting —
                            // no point continuing to poll for a window we've left.
                            if (channel.Reader.TryPeek(out _)) break;

                            // Re-read X11 props first (fastest path).
                            var (ps, pp) = windowMonitor.GetWindowMenuInfo((IntPtr)windowId);
                            if (!string.IsNullOrEmpty(ps) && !string.IsNullOrEmpty(pp) && pp != "/")
                            {
                                service = ps;
                                path    = pp;
                                logger.LogDebug("  0x{W:X8}: menu appeared via X11 poll", windowId);
                                break;
                            }

                            // Also check our registrar registry (apps re-register after NameOwnerChanged).
                            var (pollRs, pollRp) = registrarImpl.TryGetMenu(windowId);
                            if (!string.IsNullOrEmpty(pollRs) && !string.IsNullOrEmpty(pollRp) && pollRp != "/")
                            {
                                service = pollRs;
                                path    = pollRp;
                                logger.LogDebug("  0x{W:X8}: menu appeared via registrar poll", windowId);
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) { /* poll timeout or stop — fall through */ }

                    if (string.IsNullOrEmpty(service) || string.IsNullOrEmpty(path) || path == "/")
                    {
                        // AT-SPI was already tried first (above) and returned no menu,
                        // so there is no point retrying it here. Serve cached JSON so the
                        // bar is not blank, or log that no menu was found.
                        if (menuCache.TryGetValue(windowId, out var cachedJson))
                        {
                            logger.LogInformation("  0x{W:X8}: no live menu found — serving cached menu", windowId);
                            exporter.Update(cachedJson, null);
                        }
                        else
                        {
                            logger.LogDebug("  0x{W:X8}: no menu after polling", windowId);
                        }
                        continue;
                    }
                }

                logger.LogInformation("  Menu: service={S}  path={P}", service, path);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromSeconds(3));

                var menuPath   = new ObjectPath(path);
                var processTask = FetchMenuAsync(
                    connection, service, menuPath, stoppingToken, windowCts!.Token,
                    sub => layoutSub   = sub,
                    sub => propertySub = sub);

                await Task.WhenAny(processTask, Task.Delay(Timeout.Infinite, cts.Token));

                if (!processTask.IsCompleted)
                {
                    logger.LogWarning("  Menu fetch timed out for {S}", service);
                    continue;
                }

                try { await processTask; }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (StaleMenuPathException sme)
                {
                    logger.LogDebug("  0x{W:X8}: stale menu path ({S}) — attempting re-discovery", windowId, sme.Service);
                    // Remove cached source so next focus re-runs full discovery.
                    windowSources.TryRemove(windowId, out _);

                    // Serve cached menu immediately so the bar isn't blank during re-discovery.
                    if (menuCache.TryGetValue(windowId, out var cachedJson))
                    {
                        logger.LogDebug("  0x{W:X8}: serving cached menu while re-discovering", windowId);
                        exporter.Update(cachedJson, null);
                    }

                    // Clear stale entries so they're not reused on next focus.
                    registrarImpl.RemoveRegistration(windowId);
                    windowMonitor.ClearWindowMenuInfo((IntPtr)windowId);

                    // Brief settle delay: Qt/Chromium apps call RegisterWindow quickly after
                    // NameOwnerChanged but the GDBus object may not be exported yet. Waiting
                    // a moment before probing avoids a false-negative on re-discovery.
                    await Task.Delay(500, stoppingToken);

                    // After settle delay, check registrar first — app may have re-called RegisterWindow.
                    var (freshRs, freshRp) = registrarImpl.TryGetMenu(windowId);
                    string? ns, np;
                    if (!string.IsNullOrEmpty(freshRs) && !string.IsNullOrEmpty(freshRp))
                    {
                        ns = freshRs;
                        np = freshRp;
                        logger.LogDebug("  0x{W:X8}: app re-registered via registrar during settle delay", windowId);
                    }
                    else
                    {
                        // Fall back to PID scan (knownPath=null → tries default format).
                        (ns, np) = await FindMenuByPidAsync(connection, dbus, windowMonitor, registrarImpl, windowId, null, windowCts!.Token);
                    }
                    if (!string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(np))
                    {
                        logger.LogInformation("  0x{W:X8}: re-discovered menu → {S} {P}", windowId, ns, np);
                        windowMonitor.SetWindowMenuInfo((IntPtr)windowId, ns, np);
                        layoutSub?.Dispose();   layoutSub   = null;
                        propertySub?.Dispose(); propertySub = null;
                        using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        retryCts.CancelAfter(TimeSpan.FromSeconds(3));
                        var retryTask = FetchMenuAsync(connection, ns, new ObjectPath(np), stoppingToken, windowCts!.Token,
                            sub => layoutSub = sub, sub => propertySub = sub);
                        await Task.WhenAny(retryTask, Task.Delay(Timeout.Infinite, retryCts.Token));
                        if (retryTask.IsCompleted)
                            try { await retryTask; } catch (Exception rex) { logger.LogDebug("  Re-fetch after re-discovery failed: {M}", rex.Message); }
                    }
                    else
                    {
                        logger.LogDebug("  0x{W:X8}: re-discovery also failed — cached menu remains", windowId);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "  Error fetching menu for {S} {P}", service, path);
                }

                // Cache the current menu JSON after every successful fetch so it's available
                // as a fallback on future focus events where live fetch fails.
                var liveJson = exporter.LastMenuJson;
                if (liveJson != "{}" && !string.IsNullOrEmpty(liveJson))
                {
                    menuCache[windowId] = liveJson;
                    // Remember this window uses DBus so next focus skips full discovery.
                    windowSources[windowId] = new WindowMenuSource(MenuSourceType.DbusMenu, Service: service, Path: path);
                }
            }
        }
        finally
        {
            kappMenuSub?.Dispose();
            windowCts?.Cancel();
            windowCts?.Dispose();
            layoutSub?.Dispose();
            propertySub?.Dispose();
            // Dispose all outstanding AT-SPI ChildCount watchers to release D-Bus subscriptions.
            foreach (var w in atspiWatchers.Values) w.Dispose();
            atspiWatchers.Clear();
            channel.Writer.TryComplete();
            await monitorTask.ConfigureAwait(false);
            await prefetchTask.ConfigureAwait(false);
            await watchdogTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Background worker that pre-discovers menus for all open windows before the user focuses them.
    /// Populates the registrar, X11 props, and menu cache so the main loop can respond instantly.
    /// Runs continuously with gentle delays so it never floods D-Bus.
    /// </summary>
    private async Task PrefetchMenusAsync(
        Connection connection,
        IFreedesktopDBus dbus,
        IActiveWindowMonitor windowMonitor,
        AppMenuRegistrarImpl registrar,
        ConcurrentDictionary<uint, string> menuCache,
        ConcurrentDictionary<uint, WindowMenuSource> windowSources,
        AtSpiMenuReader atspi,
        CancellationToken stoppingToken)
    {
        // Wait for startup to fully settle before scanning.
        await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
        logger.LogInformation("[Prefetch] Background menu discovery started");

        while (!stoppingToken.IsCancellationRequested)
        {
            // ── Phase 1: Connection-first scan ───────────────────────────────
            // Walk all D-Bus connections, introspect each one's root to find menu
            // root nodes (/MenuBar, /com/canonical/menu), and match to X11 windows
            // by PID.  This catches apps that never called RegisterWindow (e.g.
            // started before the registrar was running).
            await ScanConnectionsForMenusAsync(connection, dbus, windowMonitor, registrar, stoppingToken);

            // ── Phase 2: Window-first scan ───────────────────────────────────
            // For windows still unresolved after the connection scan, fall back to
            // the heavier PID-based probe that tries many candidate paths.
            var windows  = windowMonitor.GetAllClientWindows();
            int discovered = 0;

            foreach (var windowId in windows)
            {
                if (stoppingToken.IsCancellationRequested) break;

                // Skip windows already fully resolved (service + path known).
                var (rs, rp) = registrar.TryGetMenu(windowId);
                if (!string.IsNullOrEmpty(rs) && !string.IsNullOrEmpty(rp)) continue;

                // Skip windows that already have X11 appmenu props (main loop fast-path works).
                var (xs, xp) = windowMonitor.GetWindowMenuInfo((IntPtr)windowId);
                if (!string.IsNullOrEmpty(xs) && !string.IsNullOrEmpty(xp)) continue;

                // Skip windows whose icon cache is already fully built — the main loop
                // will use the cache on focus and the fast-path background refresh will
                // keep it up-to-date on its own.
                if (windowSources.TryGetValue(windowId, out var existingSrc)
                    && existingSrc.IdMap != null) continue;

                // Gentle delay between windows so we don't flood D-Bus.
                await Task.Delay(300, stoppingToken);

                using var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                discoveryCts.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    var (service, path) = await FindMenuByPidAsync(
                        connection, dbus, windowMonitor, registrar, windowId, null, discoveryCts.Token);
                    if (string.IsNullOrEmpty(service) || string.IsNullOrEmpty(path))
                        continue;

                    // Store DBus results so the main loop finds them on focus.
                    registrar.StoreResolved(windowId, service, path);
                    windowMonitor.SetWindowMenuInfo((IntPtr)windowId, service, path);
                    discovered++;
                    logger.LogInformation("[Prefetch] 0x{W:X8}: discovered {S} {P}", windowId, service, path);

                    // ── Icon-cache warm: AT-SPI + DBus merge + shortcut enrichment ──────────
                    // Run the same pipeline as the cold path so that when the user first
                    // focuses this window the fully-enriched menu (icons + lazy submenus +
                    // shortcuts) is already in cache and served instantly.
                    using var warmCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    warmCts.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        var pid = windowMonitor.GetWindowPid((IntPtr)windowId);
                        if (pid == 0) goto skipIconCache;

                        // Step 1: AT-SPI tree.
                        var (atspiJson, atspiIdMap, atspiBusName) =
                            await atspi.GetMenuJsonForPidAsync(pid, warmCts.Token);
                        if (string.IsNullOrEmpty(atspiJson) || atspiJson == "{}") goto skipIconCache;

                        // Step 2: DBus full layout (3-depth AboutToShow pass).
                        var (dbJson, _) = await FetchDbusMenuJsonAsync(connection, service, path, warmCts.Token);
                        if (dbJson == null) goto skipIconCache;

                        // Step 3: Merge icons + lazy submenu children.
                        var merged = AtSpiMenuReader.MergeDbusIconsIntoAtSpiJson(atspiJson, dbJson);
                        var postMerge = merged ?? atspiJson;

                        // Step 4: Enrich with keyboard shortcuts.
                        var enriched = await atspi.EnrichMenuJsonAsync(postMerge, atspiIdMap, warmCts.Token);
                        var final = enriched ?? postMerge;

                        menuCache[windowId] = final;

                        // Store the source record with the full idMap so re-focus hits cache.
                        var src = new WindowMenuSource(
                            MenuSourceType.AtSpi,
                            AtSpiBusName: atspiBusName,
                            DbusService:  service,
                            DbusPath:     path,
                            IdMap:        atspiIdMap);
                        windowSources[windowId] = src;

                        logger.LogInformation(
                            "[Prefetch] 0x{W:X8}: icon cache warmed ({Len} chars)", windowId, final.Length);
                        skipIconCache:;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug("[Prefetch] 0x{W:X8}: icon-cache warm failed: {M}", windowId, ex.Message);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogDebug("[Prefetch] 0x{W:X8}: discovery timed out", windowId);
                }
                catch (Exception ex)
                {
                    logger.LogDebug("[Prefetch] 0x{W:X8}: discovery error: {M}", windowId, ex.Message);
                }
            }

            if (discovered > 0)
                logger.LogInformation("[Prefetch] Scan complete — {N} new menu(s) pre-loaded", discovered);

            // Shorter rescan if we found new menus (more may be pending); longer if all resolved.
            await Task.Delay(TimeSpan.FromSeconds(discovered > 0 ? 5 : 15), stoppingToken);
        }
    }

    /// <summary>
    /// Returns true if a faulted AboutToShow probe exception still proves the menu object
    /// exists — i.e. the error is a dbusmenu-level error, not a missing-object error.
    /// </summary>
    private static bool IsKnownMenuError(AggregateException? ex)
    {
        var inner = ex?.InnerException as DBusException;
        if (inner == null) return false;
        // UnknownObject / ServiceUnknown = path doesn't exist — not a menu error.
        if (inner.ErrorName == "org.freedesktop.DBus.Error.UnknownObject" ||
            inner.ErrorName == "org.freedesktop.DBus.Error.ServiceUnknown")
            return false;
        // UnknownMethod from a path that genuinely doesn't exist (e.g. the error
        // message says "Object does not exist at path") — treat as not found.
        if (inner.ErrorName == "org.freedesktop.DBus.Error.UnknownMethod")
        {
            // If the message explicitly says the com.canonical.dbusmenu interface
            // exists but this specific method/signature isn't present, the object IS
            // there — just an older/different dbusmenu API version (e.g. VS Code/Electron).
            if (inner.Message.Contains("com.canonical.dbusmenu"))
                return true;
            // Otherwise (e.g. "Object does not exist at path …") treat as not found.
            return false;
        }
        return true;
    }

    /// <summary>
    /// Introspects /com/canonical/menu on the given connection and returns all child object paths.
    /// This is how we discover the actual Qt window ID format used by an app without guessing.
    /// </summary>
    private async Task<string[]> DiscoverMenuPathsAsync(Connection connection, string busName, uint windowId, CancellationToken cancellationToken)
    {
        try
        {
            var intro = connection.CreateProxy<IIntrospectable>(busName, new ObjectPath("/com/canonical/menu"));
            var xml = await intro.IntrospectAsync();
            // Parse child node names from the introspect XML.
            // Each <node name="XXXX"/> under the root is a registered menu object.
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var children = doc.Root?
                .Elements("node")
                .Select(n => n.Attribute("name")?.Value)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => "/com/canonical/menu/" + n)
                .ToArray() ?? [];

            // Sort: put paths that contain the window ID (in any format) first.
            var windowIdHex = $"{windowId:x}";
            var windowIdDec = $"{windowId}";
            return children
                .OrderByDescending(p => p.EndsWith(windowIdHex, StringComparison.OrdinalIgnoreCase)
                                     || p.EndsWith(windowIdDec))
                .ToArray()!;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Connection-first menu scan: enumerates all D-Bus connections, introspects each one's
    /// root to detect menu root nodes ("/MenuBar", "/com/canonical/menu"), probes found paths,
    /// and matches them to X11 windows by PID. This catches apps that have menus but haven't
    /// called RegisterWindow (e.g. started before the registrar, or use a non-standard path).
    /// </summary>
    private async Task ScanConnectionsForMenusAsync(
        Connection connection,
        IFreedesktopDBus dbus,
        IActiveWindowMonitor windowMonitor,
        AppMenuRegistrarImpl registrar,
        CancellationToken stoppingToken)
    {
        // Build PID → windows map once so we don't scan connections with no matching windows.
        var windowsByPid = new Dictionary<uint, List<uint>>();
        foreach (var wid in windowMonitor.GetAllClientWindows())
        {
            var pid = windowMonitor.GetWindowPid((IntPtr)wid);
            if (pid == 0) continue;
            if (!windowsByPid.TryGetValue(pid, out var wlist))
                windowsByPid[pid] = wlist = [];
            wlist.Add(wid);
        }
        if (windowsByPid.Count == 0) return;

        string[] names;
        try { names = await dbus.ListNamesAsync(); }
        catch { return; }

        int found = 0;
        foreach (var name in names)
        {
            if (stoppingToken.IsCancellationRequested) break;
            if (!name.StartsWith(':')) continue;

            uint connPid;
            try { connPid = await dbus.GetConnectionUnixProcessIDAsync(name); }
            catch { continue; }
            if (!windowsByPid.TryGetValue(connPid, out var matchedWindows)) continue;

            // All matched windows fully resolved already? Skip this connection.
            if (matchedWindows.All(wid =>
            {
                var (rs, rp) = registrar.TryGetMenu(wid);
                return !string.IsNullOrEmpty(rs) && !string.IsNullOrEmpty(rp);
            })) continue;

            // Introspect the connection root to identify which menu protocol it uses.
            System.Collections.Generic.HashSet<string> rootNodes;
            try
            {
                var intro = connection.CreateProxy<IIntrospectable>(name, new ObjectPath("/"));
                using var iCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                iCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                var iTask = intro.IntrospectAsync();
                await Task.WhenAny(iTask, Task.Delay(Timeout.Infinite, iCts.Token));
                if (!iTask.IsCompletedSuccessfully) { await Task.Delay(20, stoppingToken); continue; }

                var doc = System.Xml.Linq.XDocument.Parse(iTask.Result);
                rootNodes = doc.Root?
                    .Elements("node")
                    .Select(n => n.Attribute("name")?.Value)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            }
            catch { await Task.Delay(20, stoppingToken); continue; }

            // Build candidate paths based on detected root nodes.
            var candidates = new List<string>();
            if (rootNodes.Contains("MenuBar"))
                candidates.AddRange(["/MenuBar/1", "/MenuBar/2", "/MenuBar/3"]);
            if (rootNodes.Contains("com"))
            {
                var canonical = await DiscoverMenuPathsAsync(connection, name, 0, stoppingToken);
                candidates.AddRange(canonical);
            }
            if (candidates.Count == 0) { await Task.Delay(20, stoppingToken); continue; }

            // Probe candidates until one responds.
            string? foundPath = null;
            foreach (var candidatePath in candidates)
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    var menuProxy = connection.CreateProxy<IDbusMenu>(name, new ObjectPath(candidatePath));
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    probeCts.CancelAfter(TimeSpan.FromMilliseconds(800));
                    var probeTask = menuProxy.AboutToShowAsync(0);
                    await Task.WhenAny(probeTask, Task.Delay(Timeout.Infinite, probeCts.Token));
                    if (probeTask.IsCompletedSuccessfully || (probeTask.IsFaulted && IsKnownMenuError(probeTask.Exception)))
                    {
                        foundPath = candidatePath;
                        break;
                    }
                }
                catch { /* path not valid */ }
            }
            if (foundPath == null) { await Task.Delay(20, stoppingToken); continue; }

            // Store the discovered menu for every unresolved matching window.
            foreach (var wid in matchedWindows)
            {
                var (rs, rp) = registrar.TryGetMenu(wid);
                if (!string.IsNullOrEmpty(rs) && !string.IsNullOrEmpty(rp)) continue;

                registrar.StoreResolved(wid, name, foundPath);
                windowMonitor.SetWindowMenuInfo((IntPtr)wid, name, foundPath);
                found++;
                logger.LogInformation("[Scan] 0x{W:X8}: discovered via connection scan → {S} {P}", wid, name, foundPath);
            }

            await Task.Delay(30, stoppingToken); // gentle pacing
        }

        if (found > 0)
            logger.LogInformation("[Scan] Connection scan complete — {N} new menu(s) discovered", found);
    }

    /// <summary>
    /// Last-resort menu discovery: find the app's D-Bus bus name by matching _NET_WM_PID,
    /// then probe the standard Qt/KDE menu object path for that window.
    /// Handles windows that started before the registrar was running and never got their
    /// _KDE_NET_WM_APPMENU_* X11 props written.
    /// </summary>
    private async Task<(string? Service, string? Path)> FindMenuByPidAsync(
        Connection connection,
        IFreedesktopDBus dbus,
        IActiveWindowMonitor windowMonitor,
        AppMenuRegistrarImpl registrar,
        uint windowId,
        string? knownMenuPath,
        CancellationToken cancellationToken)
    {
        var pid = windowMonitor.GetWindowPid((IntPtr)windowId);
        if (pid == 0)
        {
            logger.LogInformation("  0x{W:X8}: PID discovery skipped — _NET_WM_PID not set on window", windowId);
            return (null, null);
        }

        string[] names;
        try { names = await dbus.ListNamesAsync(); }
        catch { return (null, null); }

        // Candidate paths to try on connections matching this PID.
        // If we have a known path (from registrar or X11 props), prefer that.
        // Otherwise build a guess list — but introspection will be tried first (see below).
        string[]? guessPaths = null;
        if (!string.IsNullOrEmpty(knownMenuPath) && knownMenuPath != "/")
        {
            guessPaths = [knownMenuPath];
        }

        // ── Parallel PID lookup ───────────────────────────────────────────────
        // Build name→pid in batches of 20 to avoid flooding dbus-daemon while still
        // dramatically reducing latency compared to sequential O(n) queries.
        var uniqueNames = names.Where(n => n.StartsWith(':')).ToArray();
        var matchingNames = new List<string>();
        const int pidBatchSize = 20;
        for (int i = 0; i < uniqueNames.Length; i += pidBatchSize)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var batch = uniqueNames.Skip(i).Take(pidBatchSize);
            var pidTasks = batch.Select(async n =>
            {
                try   { return (n, Pid: await dbus.GetConnectionUnixProcessIDAsync(n)); }
                catch { return (n, Pid: 0u); }
            });
            foreach (var (n, connPid) in await Task.WhenAll(pidTasks))
            {
                if (connPid == pid) matchingNames.Add(n);
            }
        }

        bool pidFound = matchingNames.Count > 0;
        foreach (var name in matchingNames)
        {
            if (cancellationToken.IsCancellationRequested) break;

            logger.LogInformation("  0x{W:X8}: PID={Pid} — probing connection {N}", windowId, pid, name);

            // ── Build candidate list ──────────────────────────────────────────
            // Priority order:
            // 1. Known path (caller-supplied, e.g. from X11 props)
            // 2. Registrar paths already resolved to this service name
            // 3. Unresolved registrar paths — Qt apps register with QWindow::winId(),
            //    which has no _NET_WM_PID so ResolveServiceAsync leaves them with
            //    Service=null. We've confirmed this connection's PID matches, so try them.
            // 4. Introspection (discover real exports on /com/canonical/menu)
            // 5. Format guesses (hex/decimal)
            var candidatePaths = guessPaths ?? [];
            if (guessPaths == null)
            {
                var resolvedPaths    = registrar.GetPathsForService(name)
                                           .OrderBy(r => Math.Abs((long)r.RegisteredWindowId - (long)windowId))
                                           .Select(r => r.Path);
                var unresolvedPaths  = registrar.GetUnresolvedPaths();

                if (unresolvedPaths.Length > 0)
                    logger.LogInformation("  0x{W:X8}: trying {C} unresolved registrar path(s) on {N}: [{P}]",
                        windowId, unresolvedPaths.Length, name, string.Join(", ", unresolvedPaths));

                var introspectTask = DiscoverMenuPathsAsync(connection, name, windowId, cancellationToken);
                using var introspectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                introspectCts.CancelAfter(TimeSpan.FromMilliseconds(800));
                await Task.WhenAny(introspectTask, Task.Delay(Timeout.Infinite, introspectCts.Token));
                var introspectedPaths = introspectTask.IsCompletedSuccessfully ? introspectTask.Result : [];

                if (introspectedPaths.Length > 0)
                    logger.LogInformation("  0x{W:X8}: introspect found {C} path(s) on {N}: [{P}]",
                        windowId, introspectedPaths.Length, name, string.Join(", ", introspectedPaths));

                var fallbackPaths = new[]
                {
                    // KDE/Qt KMainWindow apps (Dolphin, Kate, Konsole, etc.)
                    "/MenuBar/1",
                    "/MenuBar/2",
                    "/MenuBar/3",
                    // Qt/Chromium/Brave apps via com.canonical.dbusmenu
                    $"/com/canonical/menu/{windowId:x}",
                    $"/com/canonical/menu/{windowId}",
                    $"/com/canonical/menu/{windowId:X}",
                };

                candidatePaths = resolvedPaths
                    .Concat(unresolvedPaths)
                    .Concat(introspectedPaths)
                    .Concat(fallbackPaths)
                    .Distinct()
                    .ToArray();
            }

            // ── Probe each candidate ──────────────────────────────────────────
            // CRITICAL: GetLayoutAsync has no CancellationToken overload — wrap in
            // Task.WhenAny so the timeout is actually enforced.
            foreach (var candidatePath in candidatePaths)
            {
                try
                {
                    // Use AboutToShowAsync(0) as a lightweight probe.
                    // Qt dbusmenu objects do NOT implement org.freedesktop.DBus.Introspectable —
                    // Introspect returns UnknownObject. But AboutToShow returns a simple bool,
                    // so Tmds.DBus has no deserialization issues. If the path doesn't exist we get
                    // a DBusException immediately; if it does we get true/false quickly.
                    var menuProxy = connection.CreateProxy<IDbusMenu>(name, new ObjectPath(candidatePath));
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    probe.CancelAfter(TimeSpan.FromMilliseconds(1500));
                    var probeTask = menuProxy.AboutToShowAsync(0);
                    await Task.WhenAny(probeTask, Task.Delay(Timeout.Infinite, probe.Token));
                    if (probeTask.IsCompletedSuccessfully || probeTask.IsFaulted && IsKnownMenuError(probeTask.Exception))
                    {
                        // Either got a valid bool response, OR got a dbusmenu-specific error
                        // (e.g. invalid id) which still proves the object exists at this path.
                        if (probeTask.IsCompletedSuccessfully)
                            logger.LogInformation("  0x{W:X8}: PID discovery found menu on {N} at {P} (AboutToShow={R})", windowId, name, candidatePath, probeTask.Result);
                        else
                            logger.LogInformation("  0x{W:X8}: PID discovery found menu on {N} at {P} (object exists, err={E})", windowId, name, candidatePath, probeTask.Exception?.InnerException?.Message);
                        registrar.StoreResolved(windowId, name, candidatePath);
                        return (name, candidatePath);
                    }
                    if (!probeTask.IsCompleted)
                        logger.LogInformation("  0x{W:X8}: probe timed out on {N} at {P}", windowId, name, candidatePath);
                    else
                        logger.LogInformation("  0x{W:X8}: probe failed on {N} at {P}: {E}", windowId, name, candidatePath, probeTask.Exception?.InnerException?.Message);
                }
                catch (Exception ex)
                {
                    logger.LogInformation("  0x{W:X8}: probe exception on {N} at {P}: {E}", windowId, name, candidatePath, ex.Message);
                }
            }

        }

        if (!pidFound)
            logger.LogInformation("  0x{W:X8}: PID discovery — no D-Bus connection found for pid={Pid}", windowId, pid);
        else
            logger.LogInformation("  0x{W:X8}: PID discovery — connections found for pid={Pid} but no menu path responded", windowId, pid);

        // ── GMenuProxy fallback (GTK apps using GMenuModel on Wayland) ───────────
        // GTK3/4 apps using GMenuModel export menus via org.gtk.Menus — a different
        // protocol than com.canonical.dbusmenu.  KDE's gmenudbusmenuproxy bridges
        // them: it creates a dbusmenu proxy at /MenuBar/N on its own service and
        // calls RegisterWindow(kwinInternalId, "/MenuBar/N").
        //
        // KWin assigns sequential integers (1, 2, 3…) to windows internally; these
        // are much smaller than our synthetic 0x80000000|pid IDs and never collide
        // with X11 window IDs.  So TryGetMenu(ourSyntheticId) never finds the entry.
        //
        // On X11 the appmenu-gtk-module writes _KDE_NET_WM_APPMENU_* atoms directly
        // on each window — the X11 monitor reads these at focus time, bypassing the
        // registrar entirely.  That is why the same GTK app works on X11 but not
        // Wayland without this fallback.
        //
        // When probing found the app's own bus connections but found no dbusmenu there,
        // check if gmenudbusmenuproxy registered a small-ID entry we can use.
        if (pidFound && names.Contains("org.kde.plasma.gmenu_dbusmenu_proxy"))
        {
            // Collect paths gmenudbusmenuproxy registered under small KWin internal IDs.
            // Filter for window IDs that could not possibly be our synthetic 0x80000000|pid
            // values or real X11 IDs (which are typically in the same range).
            const uint kwinIdMax = 0x00010000u;
            var gmenuCandidates = registrar.GetAllRegistrations()
                .Where(r => r.WindowId > 0 && r.WindowId < kwinIdMax)
                .Select(r => r.Path)
                .Distinct()
                .ToArray();

            // Also try sequential /MenuBar/N guesses if nothing was found in the registrar
            // (e.g. gmenudbusmenuproxy registered before our service started).
            if (gmenuCandidates.Length == 0)
                gmenuCandidates = Enumerable.Range(1, 10).Select(n => $"/MenuBar/{n}").ToArray();

            logger.LogInformation(
                "  0x{W:X8}: GMenuProxy fallback — probing {C} path(s) on gmenudbusmenuproxy",
                windowId, gmenuCandidates.Length);

            foreach (var gPath in gmenuCandidates)
            {
                try
                {
                    var gProxy = connection.CreateProxy<IDbusMenu>(
                        "org.kde.plasma.gmenu_dbusmenu_proxy", new ObjectPath(gPath));
                    using var gCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    gCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                    var gTask = gProxy.AboutToShowAsync(0);
                    await Task.WhenAny(gTask, Task.Delay(Timeout.Infinite, gCts.Token));
                    if (gTask.IsCompletedSuccessfully || (gTask.IsFaulted && IsKnownMenuError(gTask.Exception)))
                    {
                        logger.LogInformation(
                            "  0x{W:X8}: GMenuProxy menu found at gmenudbusmenuproxy:{P}", windowId, gPath);
                        registrar.StoreResolved(windowId, "org.kde.plasma.gmenu_dbusmenu_proxy", gPath);
                        return ("org.kde.plasma.gmenu_dbusmenu_proxy", gPath);
                    }
                }
                catch { /* path not present on gmenudbusmenuproxy */ }
            }

            logger.LogDebug("  0x{W:X8}: GMenuProxy fallback — no responding path found", windowId);
        }

        return (null, null);
    }

    private async Task FetchMenuAsync(
        Connection connection,
        string service,
        ObjectPath menuPath,
        CancellationToken stoppingToken,
        CancellationToken windowToken,
        Action<IDisposable?> setLayoutSub,
        Action<IDisposable?> setPropertySub)
    {
        var menu = connection.CreateProxy<IDbusMenu>(service, menuPath);

        await LogMenuJsonAsync(menu, service, stoppingToken);

        setLayoutSub(await menu.WatchLayoutUpdatedAsync(
            args =>
            {
                if (windowToken.IsCancellationRequested) return;  // window already changed
                logger.LogInformation(
                    "  [{Service}] Menu layout updated (revision={Revision}) — re-fetching",
                    service, args.Revision);
                _ = Task.Run(async () =>
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, windowToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(3));
                    try { await FetchAndLogMenuAsync(menu, service, stoppingToken, cts.Token); }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested && !windowToken.IsCancellationRequested)
                    {
                        logger.LogDebug("  [{Service}] Re-fetch after LayoutUpdated failed: {M}", service, ex.Message);
                    }
                });
            },
            ex => logger.LogWarning(
                "  [{Service}] LayoutUpdated signal error: {Message}",
                service, ex.Message)));

        setPropertySub(await menu.WatchItemsPropertiesUpdatedAsync(
            args => logger.LogDebug(
                "  [{Service}] Menu item properties updated — {Count} item(s) changed",
                service, args.UpdatedProps.Length),
            ex => logger.LogDebug(
                "  [{Service}] ItemsPropertiesUpdated signal error: {Message}",
                service, ex.Message)));
    }

    private async Task LogMenuJsonAsync(IDbusMenu menu, string service, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        var fetchTask = FetchAndLogMenuAsync(menu, service, stoppingToken, cts.Token);
        await Task.WhenAny(fetchTask, Task.Delay(Timeout.Infinite, cts.Token));

        if (!fetchTask.IsCompleted)
        {
            logger.LogWarning("  [{Service}] Menu fetch timed out — continuing", service);
            return;
        }

        try { await fetchTask; }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (StaleMenuPathException) { throw; }  // propagate so caller can attempt re-discovery
        catch (Exception ex) { logger.LogWarning(ex, "  [{Service}] Failed to fetch menu layout", service); }
    }

    private async Task FetchAndLogMenuAsync(IDbusMenu menu, string service, CancellationToken stoppingToken, CancellationToken timeoutToken)
    {
        try
        {
        // ── Stage 1: fire AboutToShow(0) — fire-and-forget so it can never hang the loop ──
        _ = menu.AboutToShowAsync(0).ContinueWith(t =>
        {
            if (t.IsFaulted)
                logger.LogDebug("  [{Service}] AboutToShow(0) faulted: {Msg}", service, t.Exception!.InnerException?.Message);
        }, TaskScheduler.Default);

        // Give the app a moment to populate the root level.
        await Task.Delay(30, stoppingToken);

        // ── Stage 2: shallow layout to get top-level child IDs ───────────────
        var (_, shallowLayout) = await menu.GetLayoutAsync(0, 1, []);

        // ── Stage 3: fire AboutToShow for each top-level child — fire-and-forget ──
        foreach (var raw in shallowLayout.Children)
        {
            if (raw is ValueTuple<int, IDictionary<string, object>, object[]> child)
            {
                var id = child.Item1;
                _ = menu.AboutToShowAsync(id).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        logger.LogDebug("  [{Service}] AboutToShow({Id}) faulted: {Msg}",
                            service, id, t.Exception!.InnerException?.Message);
                }, TaskScheduler.Default);
            }
        }

        if (shallowLayout.Children.Length > 0)
            await Task.Delay(30, stoppingToken);

        // ── Stage 4: full deep layout ─────────────────────────────────────────
        var (_, layout) = await menu.GetLayoutAsync(0, -1, []);
        var tree = BuildMenuNode(layout.Id, layout.Properties, layout.Children);
        var json = JsonSerializer.Serialize(tree, JsonOptions);
        var mode = GetMenuLogMode();
        if (mode != MenuLogMode.None)
        {
            var display = mode == MenuLogMode.Limited && json.Length > 250
                ? json[..250].ReplaceLineEndings(" ") + "..."
                : json;
            logger.LogInformation("  [{Service}] Menu:\n{Json}", service, display);
        }
        exporter.Update(json, menu);
        } // end try
        catch (DBusException dbe) when (
            (dbe.ErrorName == "org.freedesktop.DBus.Error.UnknownMethod" && dbe.Message.Contains("Object does not exist")) ||
            dbe.ErrorName == "org.freedesktop.DBus.Error.ServiceUnknown" ||
            dbe.ErrorName == "org.freedesktop.DBus.Error.UnknownObject")
        {
            throw new StaleMenuPathException(service, dbe);
        }
    }

    /// <summary>Thrown when a menu fetch fails because the app's D-Bus object no longer exists at the expected path.</summary>
    private sealed class StaleMenuPathException(string service, Exception inner) : Exception(inner.Message, inner)
    {
        public string Service { get; } = service;
    }

    // Tmds.DBus deserializes each dbusmenu layout node as ValueTuple<int, IDictionary<string,object>, object[]>
    private static Dictionary<string, object?> BuildMenuNode(int id, IDictionary<string, object> props, object[] rawChildren)
    {
        var node = new Dictionary<string, object?> { ["id"] = id };
        foreach (var (k, v) in props)
        {
            // icon-data is a byte[] — serialise as base64 so JSON can carry it
            if (k == "icon-data" && v is byte[] bytes)
                node[k] = Convert.ToBase64String(bytes);
            else
                node[k] = v;
        }

        if (rawChildren.Length > 0)
        {
            var kids = new List<object?>();
            foreach (var raw in rawChildren)
            {
                if (raw is ValueTuple<int, IDictionary<string, object>, object[]> child)
                    kids.Add(BuildMenuNode(child.Item1, child.Item2, child.Item3));
            }
            if (kids.Count > 0)
                node["children"] = kids;
        }

        return node;
    }

    /// <summary>
    /// Primes a DBus menu with a three-depth <c>AboutToShow</c> pass so that lazily-populated
    /// submenus (e.g. Dolphin "Create New", "Open Recent") are fully populated before the
    /// deep layout is fetched. Returns the serialised JSON and the proxy, or (null, null) on
    /// any error (service vanished, timeout, etc.).
    /// </summary>
    private static async Task<(string? Json, IDbusMenu? Proxy)> FetchDbusMenuJsonAsync(
        Connection connection, string service, string path, CancellationToken ct)
    {
        // Collects all IDs at every depth of the given raw children array.
        static IEnumerable<int> AllIds(object[] children)
        {
            foreach (var r in children)
                if (r is ValueTuple<int, IDictionary<string, object>, object[]> n)
                {
                    yield return n.Item1;
                    foreach (var id in AllIds(n.Item3))
                        yield return id;
                }
        }
        try
        {
            var proxy = connection.CreateProxy<IDbusMenu>(service, new ObjectPath(path));
            _ = proxy.AboutToShowAsync(0);
            await Task.Delay(50, ct);

            var (_, shallow) = await proxy.GetLayoutAsync(0, 1, []);
            foreach (var raw in shallow.Children)
                if (raw is ValueTuple<int, IDictionary<string, object>, object[]> c)
                    _ = proxy.AboutToShowAsync(c.Item1);
            if (shallow.Children.Length > 0)
                await Task.Delay(50, ct);

            var (_, depth2) = await proxy.GetLayoutAsync(0, 2, []);
            foreach (var id in AllIds(depth2.Children))
                _ = proxy.AboutToShowAsync(id);
            if (depth2.Children.Length > 0)
                await Task.Delay(50, ct);

            var (_, deep) = await proxy.GetLayoutAsync(0, -1, []);
            var tree = BuildMenuNode(deep.Id, deep.Properties, deep.Children);
            var json = JsonSerializer.Serialize(tree, JsonOptions);
            return (json, proxy);
        }
        catch { return (null, null); }
    }
}
