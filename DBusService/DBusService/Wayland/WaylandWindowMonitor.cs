using System.Collections.Concurrent;
using DBusService.DBus;
using Tmds.DBus;

namespace DBusService.Wayland;

/// <summary>
/// Wayland-session implementation of <see cref="IActiveWindowMonitor"/>.
/// Supports both KWin 5 (Plasma 5) and KWin 6 (Plasma 6).
///
/// Loads a KWin JavaScript script via <c>org.kde.kwin.Scripting</c> that fires a
/// D-Bus callback (<see cref="WindowActivatedAsync"/>) on every window focus change.
///
/// KWin 5 uses <c>workspace.clientActivated</c>; KWin 6 uses
/// <c>workspace.windowActivated</c>.  The correct script variant is selected at
/// runtime by probing the scripting interface: if <c>isScriptLoaded</c> is absent
/// we are on KWin 6.
///
/// In KWin 6 windows do not expose an X11 <c>windowId</c> — only a UUID
/// <c>internalId</c>.  The C# callback hashes the UUID to a stable <c>uint</c>
/// with bit 31 set so it does not collide with real X11 IDs.
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

    // True when probed KWin is version 6 (isScriptLoaded / start are absent).
    private bool _isKWin6;

    // All window IDs seen since monitor start.
    private readonly ConcurrentDictionary<uint, byte> _seenIds = new();

    // In-memory menu-info storage (replaces _KDE_NET_WM_APPMENU_* X11 atoms).
    private readonly ConcurrentDictionary<uint, (string Service, string Path)> _menuInfo = new();

    // Window metadata cache updated directly from the KWin script callback.
    private readonly ConcurrentDictionary<uint, (string? Caption, uint Pid, int Gx, int Gy)> _windowCache = new();

    private uint _lastActiveId;

    public event Action<(uint WindowId, string? AppmenuService, string? AppmenuPath)>? ActiveWindowChanged;

    public ObjectPath ObjectPath => new(CallbackPath);

    public WaylandWindowMonitor(Connection connection, ILogger logger)
    {
        _connection = connection;
        _logger     = logger;
    }

    // ── IActiveWindowMonitor ─────────────────────────────────────────────────

    public bool Connect()
    {
        try
        {
            _logger.LogInformation("[Wayland] Creating KWin D-Bus proxies...");
            _scripting = _connection.CreateProxy<IKWinScripting>("org.kde.KWin", new ObjectPath("/Scripting"));

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

    public void RunEventLoop(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Wayland] Active window monitor starting — detecting KWin version...");
        try
        {
            DetectKWinVersion();
            _logger.LogInformation("[Wayland] Detected KWin {V} — loading monitor script...",
                _isKWin6 ? "6 (Plasma 6)" : "5 (Plasma 5)");
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

    // ── IKWinWindowCallback ──────────────────────────────────────────────────

    public Task WindowActivatedAsync(string windowId, string pid, string caption, string internalId, string gx, string gy)
    {
        if (!uint.TryParse(windowId, out var rawId)) return Task.CompletedTask;
        uint.TryParse(pid, out var pidVal);
        int.TryParse(gx, out var gxVal);
        int.TryParse(gy, out var gyVal);

        uint id;
        if (rawId != 0)
        {
            id = rawId;
        }
        else if (!string.IsNullOrEmpty(internalId))
        {
            // KWin UUID → stable uint (FNV-1a hash), bit 31 set to avoid X11 collision.
            uint hash = 2166136261u;
            foreach (var c in internalId) { hash ^= (byte)c; hash *= 16777619u; }
            id = 0x80000000u | (hash & 0x7FFFFFFFu);
        }
        else if (pidVal != 0 && (gxVal != 0 || gyVal != 0))
        {
            uint hash = 2166136261u;
            hash ^= (byte)(pidVal)      ; hash *= 16777619u;
            hash ^= (byte)(pidVal >> 8) ; hash *= 16777619u;
            hash ^= (byte)(pidVal >> 16); hash *= 16777619u;
            hash ^= (byte)(pidVal >> 24); hash *= 16777619u;
            hash ^= (byte)(gxVal)       ; hash *= 16777619u;
            hash ^= (byte)(gxVal >> 8)  ; hash *= 16777619u;
            hash ^= (byte)(gyVal)       ; hash *= 16777619u;
            hash ^= (byte)(gyVal >> 8)  ; hash *= 16777619u;
            id = 0x80000000u | (hash & 0x7FFFFFFFu);
        }
        else if (pidVal != 0)
        {
            id = 0x80000000u | (pidVal & 0x7FFFFFFFu);
        }
        else
        {
            _logger.LogDebug("[Wayland] windowActivated: no usable ID — ignoring");
            return Task.CompletedTask;
        }

        _lastActiveId = id;
        _seenIds[id]  = 0;

        if (pidVal != 0 || !string.IsNullOrEmpty(caption) || gxVal != 0 || gyVal != 0)
        {
            var cap = string.IsNullOrEmpty(caption) ? null : caption;
            _windowCache.AddOrUpdate(id, (cap, pidVal, gxVal, gyVal), (_, existing) =>
                (cap ?? existing.Caption, pidVal != 0 ? pidVal : existing.Pid,
                 gxVal != 0 ? gxVal : existing.Gx, gyVal != 0 ? gyVal : existing.Gy));
        }

        var (svc, path) = GetWindowMenuInfo((IntPtr)id);
        _logger.LogInformation("[Wayland] windowActivated rawId={RawId} id=0x{I:X8} pid={P} geo=({GX},{GY}) iid={IID} caption={C}",
            rawId, id, pidVal, gxVal, gyVal, internalId, caption);
        ActiveWindowChanged?.Invoke((id, svc, path));
        return Task.CompletedTask;
    }

    // ── IActiveWindowMonitor ─────────────────────────────────────────────────

    public uint GetActiveWindow() => _lastActiveId;
    public uint[] GetAllClientWindows() => [.. _seenIds.Keys];

    public string? GetWindowName(IntPtr window)
    {
        var id = (uint)window;
        if (_windowCache.TryGetValue(id, out var c) && c.Caption != null)
            return c.Caption;
        return null;
    }

    public (int Gx, int Gy) GetWindowGeometry(IntPtr window)
    {
        var id = (uint)window;
        if (_windowCache.TryGetValue(id, out var c))
            return (c.Gx, c.Gy);
        return (-1, -1);
    }

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
    /// Determines whether KWin is version 5 or 6 by probing for the
    /// <c>isScriptLoaded</c> method (present in KWin 5, removed in KWin 6).
    /// Sets <see cref="_isKWin6"/> accordingly.
    /// </summary>
    private void DetectKWinVersion()
    {
        try
        {
            // KWin 5 responds with a bool; KWin 6 responds with an error
            // "org.freedesktop.DBus.Error.UnknownMethod".
            var task = _scripting!.isScriptLoadedAsync("__probe__");
            if (task.Wait(TimeSpan.FromSeconds(3)))
            {
                _isKWin6 = false;  // KWin 5: call succeeded
            }
            else
            {
                // Timeout — treat as KWin 6 (or unresponsive, script won't work anyway)
                _isKWin6 = true;
            }
        }
        catch (Exception ex)
        {
            // Any error (including "UnknownMethod") → KWin 6
            _logger.LogDebug("[Wayland] isScriptLoaded probe error ({M}) → assuming KWin 6", ex.Message);
            _isKWin6 = true;
        }
    }

    /// <summary>
    /// Generates the KWin monitor script appropriate for the detected KWin version,
    /// writes it to a temp file, unloads any stale instance, then loads it.
    /// </summary>
    private void LoadKWinScript()
    {
        // ── Stale script cleanup ─────────────────────────────────────────────
        // KWin 5: use isScriptLoaded to check before unloading (unloadScript hangs
        //         indefinitely if the script is not loaded).
        // KWin 6: isScriptLoaded does not exist — just try unloadScript and ignore errors.
        if (_isKWin6)
        {
            try
            {
                var unloadTask = _scripting!.unloadScriptAsync(ScriptPluginName);
                if (!unloadTask.Wait(TimeSpan.FromSeconds(3)))
                    _logger.LogDebug("[Wayland] KWin6 pre-unload timed out (non-fatal)");
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[Wayland] KWin6 pre-unload error (non-fatal): {M}", ex.Message);
            }
        }
        else
        {
            try
            {
                var isLoadedTask = _scripting!.isScriptLoadedAsync(ScriptPluginName);
                if (isLoadedTask.Wait(TimeSpan.FromSeconds(5)) && isLoadedTask.Result)
                {
                    _logger.LogInformation("[Wayland] Unloading stale KWin5 script '{P}'...", ScriptPluginName);
                    var unloadTask = _scripting!.unloadScriptAsync(ScriptPluginName);
                    if (!unloadTask.Wait(TimeSpan.FromSeconds(5)))
                        _logger.LogWarning("[Wayland] unloadScript timed out — proceeding anyway");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[Wayland] KWin5 isScriptLoaded check error: {M}", ex.Message);
            }
        }

        // ── Generate the appropriate script ──────────────────────────────────
        string script = _isKWin6 ? BuildKWin6Script() : BuildKWin5Script();

        _logger.LogInformation("[Wayland] Writing {V} script to {P}",
            _isKWin6 ? "KWin6" : "KWin5", ScriptTempPath);
        File.WriteAllText(ScriptTempPath, script);

        // ── Load the script ──────────────────────────────────────────────────
        _logger.LogInformation("[Wayland] Calling loadScript (5 s timeout)...");
        var loadTask = _scripting!.loadScriptAsync(ScriptTempPath, ScriptPluginName);
        if (!loadTask.Wait(TimeSpan.FromSeconds(5)))
        {
            _logger.LogError("[Wayland] loadScript timed out — KWin did not reply. " +
                             "Wayland window events will not be detected.");
            return;
        }
        _logger.LogInformation("[Wayland] loadScript returned id={Id}", loadTask.Result);

        // ── KWin 5 only: call start() ────────────────────────────────────────
        // KWin 6 removed start() — scripts run automatically after loadScript.
        if (!_isKWin6)
        {
            _logger.LogInformation("[Wayland] Calling start() (KWin5)...");
            var startTask = _scripting.startAsync();
            if (!startTask.Wait(TimeSpan.FromSeconds(5)))
                _logger.LogWarning("[Wayland] start() timed out (non-fatal — script may still run)");
            else
                _logger.LogInformation("[Wayland] start() returned — KWin5 monitor active");
        }
        else
        {
            _logger.LogInformation("[Wayland] KWin6: no start() needed — monitor active");
        }
    }

    /// <summary>
    /// KWin 5 (Plasma 5) script.
    /// Uses <c>workspace.clientActivated</c> signal and <c>client.windowId</c>,
    /// <c>client.geometry</c>, <c>client.internalId</c>.
    /// </summary>
    private string BuildKWin5Script() =>
        $@"workspace.clientActivated.connect(function(c) {{
    if (!c) return;
    var wid = c.windowId || 0;
    var iid = (typeof c.internalId === 'string') ? c.internalId : String(c.internalId || '');
    var gx  = c.geometry ? c.geometry.x : (c.x || 0);
    var gy  = c.geometry ? c.geometry.y : (c.y || 0);
    callDBus(
        ""com.kde.GlobalMMMenu"", ""{CallbackPath}"",
        ""com.kde.GlobalMMMenu.WindowMonitor"", ""WindowActivated"",
        String(wid), String(c.pid || 0), String(c.caption || ''), iid,
        String(gx), String(gy)
    );
}});
";

    /// <summary>
    /// KWin 6 (Plasma 6) script.
    /// Uses <c>workspace.windowActivated</c> signal (renamed from clientActivated).
    /// KWin 6 exposes no X11 <c>windowId</c> — only <c>internalId</c> (UUID string).
    /// Position comes from <c>w.pos</c> (replaces <c>w.geometry</c> which no longer
    /// exists in the KWin 6 scripting API).
    /// </summary>
    private string BuildKWin6Script() =>
        $@"workspace.windowActivated.connect(function(w) {{
    if (!w) return;
    var iid = String(w.internalId || '');
    var gx  = w.pos ? w.pos.x : (w.frameGeometry ? w.frameGeometry.x : 0);
    var gy  = w.pos ? w.pos.y : (w.frameGeometry ? w.frameGeometry.y : 0);
    callDBus(
        ""com.kde.GlobalMMMenu"", ""{CallbackPath}"",
        ""com.kde.GlobalMMMenu.WindowMonitor"", ""WindowActivated"",
        ""0"", String(w.pid || 0), String(w.caption || ''), iid,
        String(gx), String(gy)
    );
}});
";

    private void CleanupScript()
    {
        _ = _scripting?.unloadScriptAsync(ScriptPluginName);
        try { if (File.Exists(ScriptTempPath)) File.Delete(ScriptTempPath); } catch { }
    }
}
