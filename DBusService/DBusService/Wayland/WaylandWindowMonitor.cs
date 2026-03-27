using System.Collections.Concurrent;
using DBusService.DBus;
using Tmds.DBus;

namespace DBusService.Wayland;

/// <summary>
/// Wayland-session implementation of <see cref="IActiveWindowMonitor"/> for KWin 5 / Plasma 5.
///
/// Loads a KWin JavaScript script via <c>org.kde.kwin.Scripting</c> that listens
/// for <c>workspace.clientActivated</c> events (KWin 5 API).  The script uses KWin's
/// <c>callDBus</c> to invoke <see cref="WindowActivatedAsync"/> on this object,
/// which is registered on the session bus at <see cref="CallbackPath"/>.
///
/// Because this class is also the D-Bus callback receiver it implements
/// <see cref="IKWinWindowCallback"/> and is registered with
/// <c>connection.RegisterObjectAsync</c> before the KWin script is loaded, so
/// no events are missed.
///
/// Window IDs come directly from KWin's <c>client.windowId</c> (a numeric integer).
/// For XWayland windows this is the X11 window ID; for native Wayland windows it is
/// an internal KWin counter.  IDs are stable for the lifetime of the KWin session.
///
/// Menu service/path associations (equivalent to _KDE_NET_WM_APPMENU_* atoms on
/// X11) are stored in an in-process concurrent dictionary because Wayland does not
/// have per-window X11 properties.
///
/// PID lookup: the KWin script passes <c>client.pid</c> directly in the callback.
/// For XWayland windows the PID may be 0 — callers should fall back to AT-SPI's
/// PID resolution in that case.
/// </summary>
public sealed class WaylandWindowMonitor : IActiveWindowMonitor, IKWinWindowCallback
{
    /// <summary>D-Bus object path at which this monitor registers its callback receiver.</summary>
    internal const string CallbackPath = "/com/kde/globalmmmenu/windowmonitor";

    private const string ScriptPluginName = "globalmmmenu-monitor";
    private const string ScriptTempPath   = "/tmp/globalmmmenu-kwin-monitor.js";

    private readonly Connection _connection;
    private readonly ILogger    _logger;
    private          IKWinScripting? _scripting;

    // All window IDs seen since monitor start (populated on each clientActivated callback).
    private readonly ConcurrentDictionary<uint, byte> _seenIds = new();

    // In-memory menu-info storage (replaces _KDE_NET_WM_APPMENU_* X11 atoms).
    private readonly ConcurrentDictionary<uint, (string Service, string Path)> _menuInfo = new();

    // Window metadata cache updated directly from the KWin script callback.
    private readonly ConcurrentDictionary<uint, (string? Caption, uint Pid)> _windowCache = new();

    private uint _lastActiveId;

    public event Action<(uint WindowId, string? AppmenuService, string? AppmenuPath)>? ActiveWindowChanged;

    // IDBusObject — required to register this object with connection.RegisterObjectAsync.
    public ObjectPath ObjectPath => new(CallbackPath);

    public WaylandWindowMonitor(Connection connection, ILogger logger)
    {
        _connection = connection;
        _logger     = logger;
    }

    // ── IActiveWindowMonitor ─────────────────────────────────────────────────

    /// <summary>
    /// Creates KWin D-Bus proxies and registers this object as the callback receiver.
    /// No KWin round-trips are made here — all blocking D-Bus calls happen in
    /// <see cref="RunEventLoop"/> which runs on a dedicated <c>Task.Run</c> thread
    /// (safe for <c>.GetAwaiter().GetResult()</c>; no SynchronizationContext deadlock).
    /// </summary>
    public bool Connect()
    {
        try
        {
            _logger.LogInformation("[Wayland] Creating KWin D-Bus proxies...");
            // CreateProxy is purely local — no D-Bus round-trip.
            _scripting = _connection.CreateProxy<IKWinScripting>("org.kde.KWin", new ObjectPath("/Scripting"));

            // Register this object as the D-Bus receiver BEFORE RunEventLoop loads the
            // script, so that early windowActivated callbacks are not missed.
            _connection.RegisterObjectAsync(this).GetAwaiter().GetResult();
            _logger.LogInformation("[Wayland] Callback receiver registered at {P}", CallbackPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "[Wayland] Cannot initialise KWin D-Bus ({M}) — Wayland monitoring unavailable",
                ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Loads the KWin monitor script (blocking KWin round-trips are safe here because
    /// this method runs on the <c>Task.Run</c> thread created by Worker), then blocks
    /// on <paramref name="cancellationToken"/>.  Cleans up the script on exit.
    /// </summary>
    public void RunEventLoop(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Wayland] Active window monitor starting — loading KWin script...");
        try
        {
            LoadKWinScript();
            _logger.LogInformation("[Wayland] KWin script active — waiting for windowActivated events");
            cancellationToken.WaitHandle.WaitOne();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Wayland] RunEventLoop fatal error");
        }
        finally
        {
            CleanupScript();
        }
        _logger.LogInformation("[Wayland] Active window monitor stopped");
    }

    // ── IKWinWindowCallback (D-Bus method called by the KWin script) ─────────

    /// <summary>
    /// Called by the KWin JavaScript monitor script via <c>callDBus</c> each time
    /// <c>workspace.clientActivated</c> fires.  Fires <see cref="ActiveWindowChanged"/>
    /// on the monitor so the <c>Worker</c> can handle it identically to X11.
    /// </summary>
    public Task WindowActivatedAsync(string windowId, string pid, string caption)
    {
        if (!uint.TryParse(windowId, out var rawId)) return Task.CompletedTask;
        uint.TryParse(pid, out var pidVal);

        uint id;
        if (rawId != 0)
        {
            // X11 or XWayland window — use the real window ID directly.
            id = rawId;
        }
        else if (pidVal != 0)
        {
            // Native Wayland window: KWin 5 reports windowId as undefined/0 for these.
            // c.internalId is a UUID string (not numeric), so we use pid instead.
            // Synthesise a stable ID with bit 31 set to avoid colliding with X11 IDs.
            // Multiple windows of the same app share the pid → same synthetic ID,
            // which is intentional: they share the same global menu.
            id = 0x80000000u | (pidVal & 0x7FFFFFFFu);
        }
        else
        {
            _logger.LogDebug("[Wayland] clientActivated: windowId=0 and pid=0, ignoring");
            return Task.CompletedTask;
        }

        _lastActiveId = id;
        _seenIds[id]  = 0;

        // Cache the pid and caption directly from the script (avoids any D-Bus round-trip).
        if (pidVal != 0 || !string.IsNullOrEmpty(caption))
        {
            var cap = string.IsNullOrEmpty(caption) ? null : caption;
            _windowCache.AddOrUpdate(id, (cap, pidVal), (_, existing) =>
                (cap ?? existing.Caption, pidVal != 0 ? pidVal : existing.Pid));
        }

        var (svc, path) = GetWindowMenuInfo((IntPtr)id);
        _logger.LogInformation("[Wayland] clientActivated windowId={RawId} id=0x{I:X8} pid={P} caption={C}",
            rawId, id, pidVal, caption);
        ActiveWindowChanged?.Invoke((id, svc, path));
        return Task.CompletedTask;
    }

    // ── IActiveWindowMonitor ─────────────────────────────────────────────────

    public uint GetActiveWindow() => _lastActiveId;

    /// <summary>
    /// Returns all window IDs seen since the monitor started.
    /// On Wayland there is no equivalent of _NET_CLIENT_LIST, so this reflects
    /// only windows that were active at some point during the current session.
    /// </summary>
    public uint[] GetAllClientWindows() => [.. _seenIds.Keys];

    public string? GetWindowName(IntPtr window)
    {
        var id = (uint)window;
        if (_windowCache.TryGetValue(id, out var c) && c.Caption != null)
            return c.Caption;
        return null;
    }

    /// <summary>
    /// Returns the PID for a window.  Populated directly from <c>client.pid</c> in the
    /// KWin 5 script callback.  May be 0 for XWayland windows — callers should fall back
    /// to AT-SPI's PID resolution.
    /// </summary>
    public uint GetWindowPid(IntPtr window)
    {
        var id = (uint)window;
        if (_windowCache.TryGetValue(id, out var cached))
            return cached.Pid;
        return 0;
    }

    public void SetWindowMenuInfo(IntPtr window, string service, string path)
        => _menuInfo[(uint)window] = (service, path);

    public void ClearWindowMenuInfo(IntPtr window)
        => _menuInfo.TryRemove((uint)window, out _);

    public (string? Service, string? Path) GetWindowMenuInfo(IntPtr window)
        => _menuInfo.TryGetValue((uint)window, out var v) ? (v.Service, v.Path) : (null, null);

    public void Dispose() => CleanupScript();

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Writes the KWin monitor script to a temp file, unloads any leftover
    /// instance from a prior service run, then loads and starts the new script.
    /// </summary>
    private void LoadKWinScript()
    {
        // Unload any stale script from a previous service run BEFORE loading the new one.
        // We check isScriptLoaded first rather than blindly calling unloadScript:
        // if the script was not loaded, unloadScript in KWin 5 may never reply, causing
        // GetResult() to hang indefinitely. isScriptLoaded always replies promptly.
        try
        {
            var isLoadedTask = _scripting!.isScriptLoadedAsync(ScriptPluginName);
            if (isLoadedTask.Wait(TimeSpan.FromSeconds(5)) && isLoadedTask.Result)
            {
                _logger.LogInformation("[Wayland] Unloading stale script '{P}'...", ScriptPluginName);
                var unloadTask = _scripting!.unloadScriptAsync(ScriptPluginName);
                if (!unloadTask.Wait(TimeSpan.FromSeconds(5)))
                    _logger.LogWarning("[Wayland] unloadScript timed out — proceeding anyway");
                else
                    _logger.LogInformation("[Wayland] Stale script unloaded");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Wayland] isScriptLoaded check error: {M}", ex.Message);
        }

        // KWin 5 Wayland: workspace.clientActivated fires for all windows.
        // c.windowId = X11/XWayland ID (a JavaScript number) for X11 windows, OR
        //            = undefined (not 0) for native Wayland windows.
        // c.internalId = a UUID *string* like "{4898dcbd-...}" — NOT usable as a uint.
        // c.pid        = numeric PID — always present and non-zero.
        // c.caption    = window title string — used for display and fallback matching.
        //
        // Strategy: pass (c.windowId || 0) as wid — gives 0 for Wayland-native windows.
        // C# handles wid==0 by synthesising a pid-based ID (0x80000000|pid).
        // All values passed as strings to avoid int32/uint32 D-Bus type mismatch.
        // Tmds.DBus strips "Async" from WindowActivatedAsync → D-Bus method "WindowActivated".
        // Use $@"..." (verbatim interpolated): "" → literal quote, {{ / }} → literal brace.
        var script = $@"workspace.clientActivated.connect(function(c) {{
    if (!c) return;
    // windowId is a JS number for X11/XWayland, undefined for native Wayland.
    // Use || 0 to normalise undefined → 0; do NOT use internalId (it is a UUID string).
    var wid = c.windowId || 0;
    callDBus(
        ""com.kde.GlobalMMMenu"", ""{CallbackPath}"",
        ""com.kde.GlobalMMMenu.WindowMonitor"", ""WindowActivated"",
        String(wid), String(c.pid || 0), String(c.caption || '')
    );
}});
";

        _logger.LogInformation("[Wayland] Writing script to {P}", ScriptTempPath);
        File.WriteAllText(ScriptTempPath, script);

        _logger.LogInformation("[Wayland] Calling loadScript (5 s timeout)...");
        var loadTask = _scripting!.loadScriptAsync(ScriptTempPath, ScriptPluginName);
        if (!loadTask.Wait(TimeSpan.FromSeconds(5)))
        {
            _logger.LogError("[Wayland] loadScript timed out — KWin did not reply. " +
                             "Wayland window events will not be detected.");
            return;
        }
        _logger.LogInformation("[Wayland] loadScript returned id={Id}", loadTask.Result);

        _logger.LogInformation("[Wayland] Calling start...");
        var startTask = _scripting.startAsync();
        if (!startTask.Wait(TimeSpan.FromSeconds(5)))
            _logger.LogWarning("[Wayland] start() timed out (non-fatal — script may still run)");
        else
            _logger.LogInformation("[Wayland] start() returned — monitor active");
    }

    private void CleanupScript()
    {
        // Best-effort unload — fire-and-forget for the same reason as in LoadKWinScript.
        _ = _scripting?.unloadScriptAsync(ScriptPluginName);
        try { if (File.Exists(ScriptTempPath)) File.Delete(ScriptTempPath); } catch { }
    }
}
