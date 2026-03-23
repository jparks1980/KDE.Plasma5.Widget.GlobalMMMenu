using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using DBusService.DBus;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── Build-identity banner — proves which binary is running ─────────────
        // Update this string whenever you want to confirm a fresh binary is loaded.
        logger.LogInformation("=== DBusService build: 2026-03-23-v5 (AT-SPI RoleMenuItem expansion fix) ===");

        using var connection = new Connection(Address.Session!);
        await connection.ConnectAsync();
        logger.LogInformation("Connected to D-Bus session bus");

        Console.WriteLine(configuration["GlobalMMMenu:MenuLogMode"]);

        await connection.RegisterServiceAsync("com.kde.GlobalMMMenu");
        await connection.RegisterObjectAsync(exporter);
        logger.LogInformation("Registered com.kde.GlobalMMMenu on session bus");

        // D-Bus daemon proxy — used to find a window's bus name by PID when X11 props aren't set.
        var dbus = connection.CreateProxy<IFreedesktopDBus>("org.freedesktop.DBus", "/org/freedesktop/DBus");

        // ── X11 active-window monitor ─────────────────────────────────────────
        using var x11 = new X11ActiveWindowMonitor();
        if (!x11.Connect())
        {
            logger.LogWarning(
                "Could not connect to X11 display — window focus monitoring is unavailable.");
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            return;
        }

        // Own com.canonical.AppMenu.Registrar so Qt/KDE apps call RegisterWindow with us.
        // We watch for NameOwnerChanged so if valapanel is running at startup and we can't
        // immediately take the name, we grab it the moment valapanel exits.
        var registrarImpl = new AppMenuRegistrarImpl();
        registrarImpl.Initialize(dbus, x11, logger);
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
        // and _KDE_NET_WM_APPMENU_OBJECT_PATH — the same properties the native KDE global
        // menu applet uses. These work for ALL windows regardless of registrar restarts.
        var channel = Channel.CreateBounded<(uint WindowId, string? Service, string? Path)>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = true,
            });

        x11.ActiveWindowChanged += info => channel.Writer.TryWrite(info);

        var x11Task      = Task.Run(() => x11.RunEventLoop(stoppingToken), stoppingToken);

        IDisposable? layoutSub   = null;
        IDisposable? propertySub = null;
        CancellationTokenSource? windowCts = null;

        // Cache of last known-good menu JSON keyed by window ID.
        // ConcurrentDictionary so the background prefetch worker can write to it safely.
        var menuCache    = new ConcurrentDictionary<uint, string>();
        var prefetchEnabled = configuration.GetValue<bool>("GlobalMMMenu:EnablePrefetch", false);
        var prefetchTask = prefetchEnabled
            ? Task.Run(() => PrefetchMenusAsync(connection, dbus, x11, registrarImpl, menuCache, stoppingToken), stoppingToken)
            : Task.CompletedTask;
        if (!prefetchEnabled)
            logger.LogInformation("[Prefetch] Background menu discovery disabled (EnablePrefetch=false)");

        // AT-SPI reader — used as last resort for apps that have no dbusmenu object
        // (e.g. started before the registrar, or Qt builds that skip dbusmenu init).
        await using var atspi = new AtSpiMenuReader(logger);
        var atspiAvailable = await atspi.ConnectAsync();
        atspi.RichMetadata = configuration.GetValue<bool>("GlobalMMMenu:AtSpiRichMetadata", false);

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
                    windowId, x11.GetWindowName((IntPtr)windowId) ?? "?");
                exporter.Update("{}", null);

                // Resolve service + path (priority order):
                // 1. Live registrar registry — populated via RegisterWindow in the current session.
                //    Always checked first: X11 props may carry a stale bus name from a previous run.
                // 2. X11 window properties — used when registrar has no complete entry.
                // 3. PID-based discovery — last resort, probes /com/canonical/menu/<windowId>
                var service = x11Service;
                var path    = x11Path;

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
                    var (ds, dp) = await FindMenuByPidAsync(connection, dbus, x11, registrarImpl, windowId, knownPath, windowCts!.Token);
                    if (!string.IsNullOrEmpty(ds) && !string.IsNullOrEmpty(dp))
                    {
                        service = ds;
                        path    = dp;
                        // Write X11 props back so next focus resolves instantly without PID scan.
                        x11.SetWindowMenuInfo((IntPtr)windowId, service, path);
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
                            var (ps, pp) = x11.GetWindowMenuInfo((IntPtr)windowId);
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
                        // ── AT-SPI fallback ──────────────────────────────────────────────
                        // For apps that have no dbusmenu object (started before registrar,
                        // or Qt build that skips dbusmenu init), read the menu via the
                        // accessibility tree. Works unconditionally for all Qt/KDE apps.
                        if (atspiAvailable)
                        {
                            var pid = x11.GetWindowPid((IntPtr)windowId);
                            if (pid != 0)
                            {
                                using var atspiCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                                atspiCts.CancelAfter(TimeSpan.FromSeconds(15));
                                try
                                {
                                    var (atspiJson, atspiIdMap) = await atspi.GetMenuJsonForPidAsync(pid, atspiCts.Token);
                                    if (!string.IsNullOrEmpty(atspiJson) && atspiJson != "{}")
                                    {
                                        logger.LogInformation("  0x{W:X8}: menu from AT-SPI (pid={P})", windowId, pid);
                                        var atspiMode = GetMenuLogMode();
                                        if (atspiMode != MenuLogMode.None)
                                        {
                                            var display = atspiMode == MenuLogMode.Limited && atspiJson.Length > 250
                                                ? atspiJson[..250].ReplaceLineEndings(" ") + "..."
                                                : atspiJson;
                                            logger.LogInformation("  [AT-SPI] Menu:\n{Json}", display);
                                        }
                                        exporter.UpdateAtSpi(atspiJson, atspi, atspiIdMap);
                                        menuCache[windowId] = atspiJson;
                                        continue;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger.LogDebug("  0x{W:X8}: AT-SPI fallback failed: {M}", windowId, ex.Message);
                                }
                            }
                        }

                        // Last resort: serve cached menu so the bar isn't blank.
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

                    // Serve cached menu immediately so the bar isn't blank during re-discovery.
                    if (menuCache.TryGetValue(windowId, out var cachedJson))
                    {
                        logger.LogDebug("  0x{W:X8}: serving cached menu while re-discovering", windowId);
                        exporter.Update(cachedJson, null);
                    }

                    // Clear stale entries so they're not reused on next focus.
                    registrarImpl.RemoveRegistration(windowId);
                    x11.ClearWindowMenuInfo((IntPtr)windowId);

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
                        (ns, np) = await FindMenuByPidAsync(connection, dbus, x11, registrarImpl, windowId, null, windowCts!.Token);
                    }
                    if (!string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(np))
                    {
                        logger.LogInformation("  0x{W:X8}: re-discovered menu → {S} {P}", windowId, ns, np);
                        x11.SetWindowMenuInfo((IntPtr)windowId, ns, np);
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
                    menuCache[windowId] = liveJson;
            }
        }
        finally
        {
            windowCts?.Cancel();
            windowCts?.Dispose();
            layoutSub?.Dispose();
            propertySub?.Dispose();
            channel.Writer.TryComplete();
            await x11Task.ConfigureAwait(false);
            await prefetchTask.ConfigureAwait(false);
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
        X11ActiveWindowMonitor x11,
        AppMenuRegistrarImpl registrar,
        ConcurrentDictionary<uint, string> menuCache,
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
            await ScanConnectionsForMenusAsync(connection, dbus, x11, registrar, stoppingToken);

            // ── Phase 2: Window-first scan ───────────────────────────────────
            // For windows still unresolved after the connection scan, fall back to
            // the heavier PID-based probe that tries many candidate paths.
            var windows  = x11.GetAllClientWindows();
            int discovered = 0;

            foreach (var windowId in windows)
            {
                if (stoppingToken.IsCancellationRequested) break;

                // Skip windows already fully resolved (service + path known).
                var (rs, rp) = registrar.TryGetMenu(windowId);
                if (!string.IsNullOrEmpty(rs) && !string.IsNullOrEmpty(rp)) continue;

                // Skip windows that already have X11 appmenu props (main loop fast-path works).
                var (xs, xp) = x11.GetWindowMenuInfo((IntPtr)windowId);
                if (!string.IsNullOrEmpty(xs) && !string.IsNullOrEmpty(xp)) continue;

                // Gentle delay between windows so we don't flood D-Bus.
                await Task.Delay(300, stoppingToken);

                using var discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                discoveryCts.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    var (service, path) = await FindMenuByPidAsync(
                        connection, dbus, x11, registrar, windowId, null, discoveryCts.Token);
                    if (string.IsNullOrEmpty(service) || string.IsNullOrEmpty(path))
                        continue;

                    // Store results so the main loop finds them instantly on focus.
                    registrar.StoreResolved(windowId, service, path);
                    x11.SetWindowMenuInfo((IntPtr)windowId, service, path);
                    discovered++;
                    logger.LogInformation("[Prefetch] 0x{W:X8}: discovered {S} {P}", windowId, service, path);

                    // Pre-fetch the menu JSON to warm the cache using the same
                    // AboutToShow + GetLayout sequence as the main loop.
                    using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    fetchCts.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        var menu = connection.CreateProxy<IDbusMenu>(service, new ObjectPath(path));
                        _ = menu.AboutToShowAsync(0);  // fire-and-forget to trigger lazy populate
                        await Task.Delay(50, stoppingToken);

                        var shallowTask = menu.GetLayoutAsync(0, 1, []);
                        await Task.WhenAny(shallowTask, Task.Delay(Timeout.Infinite, fetchCts.Token));
                        if (!shallowTask.IsCompletedSuccessfully) goto skipCache;

                        foreach (var raw in shallowTask.Result.Layout.Children)
                        {
                            if (raw is ValueTuple<int, IDictionary<string, object>, object[]> child)
                                _ = menu.AboutToShowAsync(child.Item1);
                        }
                        await Task.Delay(50, stoppingToken);

                        var deepTask = menu.GetLayoutAsync(0, -1, []);
                        await Task.WhenAny(deepTask, Task.Delay(Timeout.Infinite, fetchCts.Token));
                        if (!deepTask.IsCompletedSuccessfully) goto skipCache;

                        var (_, layout) = deepTask.Result;
                        var tree = BuildMenuNode(layout.Id, layout.Properties, layout.Children);
                        var json = JsonSerializer.Serialize(tree, JsonOptions);
                        if (json.Length > 2)
                        {
                            menuCache[windowId] = json;
                            logger.LogInformation("[Prefetch] 0x{W:X8}: menu JSON cached ({Len} chars)", windowId, json.Length);
                        }
                        skipCache:;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug("[Prefetch] 0x{W:X8}: cache-warm failed: {M}", windowId, ex.Message);
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
        return inner.ErrorName != "org.freedesktop.DBus.Error.UnknownObject"
            && inner.ErrorName != "org.freedesktop.DBus.Error.UnknownMethod"
            && inner.ErrorName != "org.freedesktop.DBus.Error.ServiceUnknown";
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
        X11ActiveWindowMonitor x11,
        AppMenuRegistrarImpl registrar,
        CancellationToken stoppingToken)
    {
        // Build PID → windows map once so we don't scan connections with no matching windows.
        var windowsByPid = new Dictionary<uint, List<uint>>();
        foreach (var wid in x11.GetAllClientWindows())
        {
            var pid = x11.GetWindowPid((IntPtr)wid);
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
                x11.SetWindowMenuInfo((IntPtr)wid, name, foundPath);
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
        X11ActiveWindowMonitor x11,
        AppMenuRegistrarImpl registrar,
        uint windowId,
        string? knownMenuPath,
        CancellationToken cancellationToken)
    {
        var pid = x11.GetWindowPid((IntPtr)windowId);
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

        bool pidFound = false;
        foreach (var name in names)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!name.StartsWith(':')) continue;  // skip well-known names

            uint connPid;
            try { connPid = await dbus.GetConnectionUnixProcessIDAsync(name); }
            catch { continue; }

            if (connPid != pid) continue;

            pidFound = true;
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
}
