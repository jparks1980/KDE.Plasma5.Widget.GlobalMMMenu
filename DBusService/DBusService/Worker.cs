using System.Text.Json;
using System.Threading.Channels;
using DBusService.DBus;
using DBusService.X11;
using Tmds.DBus;

namespace DBusService;

public class Worker(ILogger<Worker> logger, GlobalMenuExporter exporter) : BackgroundService
{
    // Fallback registrar for GTK apps that don't set X11 KDE appmenu window properties.
    private const string RegistrarService = "com.canonical.AppMenu.Registrar";
    private const string RegistrarPath    = "/com/canonical/AppMenu/Registrar";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var connection = new Connection(Address.Session!);
        await connection.ConnectAsync();
        logger.LogInformation("Connected to D-Bus session bus");

        await connection.RegisterServiceAsync("com.kde.GlobalMMMenu");
        await connection.RegisterObjectAsync(exporter);
        logger.LogInformation("Registered com.kde.GlobalMMMenu on session bus");

        // Registrar fallback for apps that don't set X11 KDE appmenu properties (e.g. GTK apps).
        var registrar = connection.CreateProxy<IAppMenuRegistrar>(RegistrarService, RegistrarPath);

        // ── X11 active-window monitor ─────────────────────────────────────────
        using var x11 = new X11ActiveWindowMonitor();
        if (!x11.Connect())
        {
            logger.LogWarning(
                "Could not connect to X11 display — window focus monitoring is unavailable.");
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            return;
        }

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

        var x11Task = Task.Run(() => x11.RunEventLoop(stoppingToken), stoppingToken);

        IDisposable? layoutSub   = null;
        IDisposable? propertySub = null;

        try
        {
            await foreach (var (windowId, x11Service, x11Path) in channel.Reader.ReadAllAsync(stoppingToken))
            {
                layoutSub?.Dispose();   layoutSub   = null;
                propertySub?.Dispose(); propertySub = null;

                logger.LogInformation("Active window → 0x{WindowId:X8}", windowId);
                exporter.Update("{}", null);

                // Resolve service + path:
                // 1. X11 window properties (set by Qt/KDE apps, works even after registrar restarts)
                // 2. appmenu-registrar (fallback for GTK apps via appmenu-gtk-module)
                var service = x11Service;
                var path    = x11Path;

                if (string.IsNullOrEmpty(service) || string.IsNullOrEmpty(path) || path == "/")
                {
                    try
                    {
                        using var rcts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        rcts.CancelAfter(TimeSpan.FromSeconds(2));
                        var (rs, rp) = await registrar.GetMenuForWindowAsync(windowId);
                        if (!string.IsNullOrEmpty(rs) && rp != new ObjectPath("/"))
                        {
                            service = rs;
                            path    = rp.ToString();
                            logger.LogDebug("  0x{W:X8}: menu from registrar ({S})", windowId, service);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug("  0x{W:X8}: registrar lookup failed ({M})", windowId, ex.Message);
                    }
                }
                else
                {
                    logger.LogDebug("  0x{W:X8}: menu from X11 props", windowId);
                }

                if (string.IsNullOrEmpty(service) || string.IsNullOrEmpty(path) || path == "/")
                {
                    logger.LogDebug("  0x{W:X8}: no menu", windowId);
                    continue;
                }

                logger.LogInformation("  Menu: service={S}  path={P}", service, path);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(TimeSpan.FromSeconds(3));

                var menuPath   = new ObjectPath(path);
                var processTask = FetchMenuAsync(
                    connection, service, menuPath, stoppingToken,
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
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "  Error fetching menu for {S} {P}", service, path);
                }
            }
        }
        finally
        {
            layoutSub?.Dispose();
            propertySub?.Dispose();
            channel.Writer.TryComplete();
            await x11Task.ConfigureAwait(false);
        }
    }

    private async Task FetchMenuAsync(
        Connection connection,
        string service,
        ObjectPath menuPath,
        CancellationToken stoppingToken,
        Action<IDisposable?> setLayoutSub,
        Action<IDisposable?> setPropertySub)
    {
        var menu = connection.CreateProxy<IDbusMenu>(service, menuPath);

        await LogMenuJsonAsync(menu, service, stoppingToken);

        setLayoutSub(await menu.WatchLayoutUpdatedAsync(
            args => logger.LogInformation(
                "  [{Service}] Menu layout updated — revision={Revision}, changed item={Parent}",
                service, args.Revision, args.Parent),
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
        catch (Exception ex) { logger.LogWarning(ex, "  [{Service}] Failed to fetch menu layout", service); }
    }

    private async Task FetchAndLogMenuAsync(IDbusMenu menu, string service, CancellationToken stoppingToken, CancellationToken timeoutToken)
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
        logger.LogInformation("  [{Service}] Menu:\n{Json}", service, json);
        exporter.Update(json, menu);
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
