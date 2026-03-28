# GlobalMMMenu

A KDE Plasma widget that displays a native global menu bar for the active window, using a C# DBus service to monitor window focus and fetch menus, plus a C++ QML plugin to render them as real native Qt menus.

Supports both **X11** and **Wayland** (KDE/KWin 5) sessions.

## Overview

KDE Plasma's built-in global menu applet requires apps to export their menus via DBusMenu. **GlobalMMMenu** extends this with:

- A background **C# .NET service** that monitors the active window (X11 or Wayland/KWin) and reads menu data via multiple discovery methods (see below), with intelligent caching so repeated focus events are near-instant.
- A **C++ QML plugin** (`libglobalmenuhelper.so`) that receives the menu JSON over DBus and pops up native `QMenu` instances with full icon and shortcut support.
- A **Plasma plasmoid** (`com.kde.plasma.globalmmenu`) that provides the panel widget UI and calls the C++ plugin.

---

## Architecture

```
Window focus change
  X11:     _NET_ACTIVE_WINDOW event (event-driven, no polling)
  Wayland: KWin D-Bus clientActivated callback via kwin script
        │
        ▼
IActiveWindowMonitor (C#)
  ├─ X11:     X11ActiveWindowMonitor    — XSelectInput event loop
  └─ Wayland: WaylandWindowMonitor      — KWin JS script + D-Bus callback
        │
        ├─ 1. Window source cache ──────────────────────────────────────────┐
        │     Previously-seen windows go straight to their known provider.  │
        │                                                                   │
        ├─ 2. com.canonical.AppMenu.Registrar (live registry)               │
        │     Qt/KDE apps call RegisterWindow — kded5-appmenu bridges       │
        │     the Wayland appmenu-v1 protocol to this registrar for us.     │
        │                                                                   │
        ├─ 3. Window menu properties                                        │
        │     X11: _KDE_NET_WM_APPMENU_* atoms written by the app.         │
        │     Wayland: in-memory cache, populated after successful probe.   │
        │                                                                   │
        ├─ 4. PID-based DBus probe                                          │
        │     Scans /MenuBar/N and /com/canonical/menu/<id> on all          │
        │     D-Bus connections belonging to the window's PID.              │
        │                                                                   │
        └─ 5. AT-SPI accessibility tree  ◄────────────────────────────────┘
              Fallback for non-KDE apps (e.g. Electron, GTK2).
              Not used for native KDE/Qt apps on Wayland — they export
              menus directly via dbusmenu once MenuBar is enabled.

        └─ 6. org.gtk.Menus (GMenuModel) ─────────────────────────────────
              For native Wayland GTK3/4 apps (HandBrake, etc.) that hide
              their GtkMenuBar when AppMenu.Registrar is present and
              instead export menus via the GLib GMenuModel D-Bus protocol.
              Discovered via the app's GtkApplication well-known bus name
              (e.g. fr.handbrake.ghb → /fr/handbrake/ghb/menus/menubar)
              or by recursive introspection of the app's D-Bus tree.
        │
        ▼
  Menu JSON (dbusmenu or AT-SPI tree)
        │
        ├─ Background enrichment (dbusmenu path)
        │   a. 3-depth AboutToShow pass → lazy submenus populated
        │   b. Icon names / icon data attached to each item
        │   c. Keyboard shortcuts resolved
        │   d. Result cached in windowSources (instant on re-focus)
        └────────────────────────────────────────────────────────
        │
        ▼
GlobalMenuExporter (C#)  ←→  com.kde.GlobalMMMenu  DBus service
        │
        ▼
GlobalMMMenuPlugin (C++)   reads JSON via DBus
  builds QMenu with icons + shortcuts
        │
        ▼
Plasma panel widget (QML)  shows menu buttons, calls openNativeMenu()
```

---

## Requirements

### Runtime
- KDE Plasma 5.x on X11 or Wayland
- .NET 10 runtime (`dotnet-runtime-10`)
- Qt5 (`libqt5qml5`, `libqt5widgets5`)
- `kded5` with the `appmenu` module (standard in KDE Plasma 5)
- `appmenu-gtk3-module` — for GTK app menus on X11 (Firefox, Thunderbird, etc.)
  > **Note:** On native Wayland, GTK3/4 apps (e.g. HandBrake) use the `org.gtk.Menus`
  > GMenuModel protocol instead. This is handled automatically without any extra module.
- `appmenu-registrar` — GTK app menu registration daemon

### Build
- .NET 10 SDK (`dotnet-sdk-10`)
- CMake ≥ 3.16
- Qt5 development headers: `qtbase5-dev qtdeclarative5-dev qtquickcontrols2-5-dev`
- `build-essential`

```bash
# Install build dependencies (Ubuntu/Kubuntu)
sudo apt install cmake build-essential dotnet-sdk-10 \
    qtbase5-dev qtdeclarative5-dev \
    appmenu-gtk3-module appmenu-registrar
```

---

## Installation

### Quick Install

```bash
git clone https://github.com/YOUR_USERNAME/GlobalMMMenu.git
cd GlobalMMMenu
./install.sh
```

This will:
1. Build and install the C++ QML plugin (requires `sudo` for the system Qt path)
2. Install the Plasma plasmoid
3. Build and publish the C# DBus service as a self-contained binary at `/usr/local/bin/globalmmmenu`, along with its required `appsettings.json`
4. Install and enable a **systemd user service** that auto-starts with your graphical session

### Partial Installs

```bash
./install.sh --plugin    # Rebuild C++ plugin only (after source changes)
./install.sh --plasmoid  # Reinstall QML/plasmoid only
./install.sh --service   # Rebuild and reinstall the C# service only
```

### Uninstall

```bash
./install.sh --uninstall
```

---

## Adding the Widget to the Panel

1. Right-click the KDE panel → **Add Widgets**
2. Search for **"Global MM Menu"**
3. Drag it to the panel (recommended: left side, before the system tray)

---

## Service Management

The service runs as a **systemd user service**:

```bash
# Status
systemctl --user status globalmmmenu

# Live logs
journalctl --user -u globalmmmenu -f

# Restart after reconfiguration
systemctl --user restart globalmmmenu

# Disable auto-start
systemctl --user disable globalmmmenu
```

---

## Configuration

`appsettings.json` (deployed to `/usr/local/bin/appsettings.json`) controls runtime behaviour:

```json
{
  "GlobalMMMenu": {
    "MenuLogMode": "Limited",
    "AtSpiRichMetadata": false,
    "EnablePrefetch": false
  }
}
```

| Key | Values | Description |
|---|---|---|
| `MenuLogMode` | `Full`, `Limited`, `None` | How much menu JSON to write to the log. `Limited` shows the first 250 chars. |
| `AtSpiRichMetadata` | `true`/`false` | Fetch shortcuts inline during the AT-SPI tree walk (adds latency; background enrichment is preferred). Default `false`. |
| `EnablePrefetch` | `true`/`false` | Pre-discover menus for all open windows in the background. Useful for very fast window-switching but increases D-Bus traffic. Default `false`. |

---

## Project Structure

```
GlobalMMMenu/               Plasma plasmoid (QML)
  contents/ui/main.qml     Panel widget — buttons + polling

GlobalMMMenuPlugin/         C++ QML plugin
  src/
    plugin.cpp/h            QML plugin registration
    globalmenuhelper.cpp/h  Menu builder — JSON → QMenu with icons/shortcuts
    iconprovider.cpp/h      Icon resolution helper
  CMakeLists.txt
  qmldir

DBusService/
  DBusService/
    Worker.cs               X11 monitor + menu fetcher + window source cache
    GlobalMenuExporter.cs   Exposes com.kde.GlobalMMMenu on the session bus
    AtSpiMenuReader.cs      AT-SPI fallback: reads menus for any Qt/KDE app
    GtkMenuReader.cs        GTK3/4 native Wayland: reads org.gtk.Menus (GMenuModel)
    X11/
      X11ActiveWindowMonitor.cs   X11 event loop + window property reader
      NativeMethods.cs            libX11 P/Invoke bindings
    DBus/
      IAppMenuRegistrar.cs  com.canonical.AppMenu.Registrar interface
      IDbusMenu.cs          com.canonical.dbusmenu interface
      IGlobalMenuService.cs com.kde.GlobalMMMenu interface
      IAtSpi.cs             AT-SPI2 accessibility bus interfaces

install.sh                  Full deployment script
```

---

## How Windows Are Detected

The service uses a **four-tier discovery chain**, from fastest to slowest:

### 1. Window source cache (fastest — subsequent focuses)
Once a window's menu provider is confirmed, the result is stored in a per-session `windowSources` dictionary keyed by window ID. On all future focus events for that window, the service goes directly to the confirmed provider with no registration scan, no PID lookup, and no polling.

The cache is cleared on `StaleMenuPathException` (app restarted) so stale entries don't persist.

### 2. Live registrar registry
Qt/KDE apps register via `com.canonical.AppMenu.Registrar`. On **Wayland**, apps use the `appmenu-v1` Wayland protocol — KWin notifies `kded5-appmenu`, which bridges it and calls `RegisterWindow` on our registrar. On service startup, `kded5-appmenu` is sent a `reconfigure()` signal so it re-registers any windows that started before the service.

### 3. Window menu properties
`_KDE_NET_WM_APPMENU_SERVICE_NAME` and `_KDE_NET_WM_APPMENU_OBJECT_PATH` on X11 — written by the app at startup and persisted in the X server. On Wayland these are stored in an in-process dictionary. The service also writes these back after successful PID discovery so subsequent focus events resolve instantly.

### 4. PID-based DBus probe
Used when no registrar or property data is available. Enumerates all D-Bus connections belonging to the window's PID, introspects `/com/canonical/menu` for Chromium-style apps, and probes `/MenuBar/N` for KDE/Qt `KMainWindow` apps.

### 5. org.gtk.Menus (GMenuModel) — native Wayland GTK3/4
When a GTK3/4 app running natively on Wayland detects `com.canonical.AppMenu.Registrar`, it hides its in-app `GtkMenuBar` and exports the same menu via the `org.gtk.Menus` GLib GMenuModel D-Bus protocol. KDE's `appmenu-gtk3-module` (which bridges this on X11 via the constant path `/org/appmenu/gtk/window/menus/menubar`) is not installed on most Wayland-only systems.

The service discovers these menus by:
1. Checking which D-Bus well-known name the app owns (e.g. `fr.handbrake.ghb`)
2. Deriving the path: `fr.handbrake.ghb` → `/fr/handbrake/ghb/menus/menubar`
3. Falling back to recursive D-Bus introspection of the app's connection tree

The full menu tree is fetched recursively via `org.gtk.Menus.Start()` subscription IDs, converted to dbusmenu-compatible JSON, and served to the panel widget.

---

## AT-SPI Menu Features

AT-SPI is used as a **fallback** for apps that don't export a dbusmenu object (e.g. some Electron apps, legacy GTK2 apps). Native KDE/Qt apps on Wayland use dbusmenu via `kded5-appmenu` and do not require AT-SPI.

| Feature | Available via AT-SPI | Notes |
|---|---|---|
| Menu labels | ✅ | `org.a11y.atspi.Accessible` Name property |
| Enabled/disabled state | ✅ | AT-SPI `SENSITIVE` + `ENABLED` state bits |
| Checkmark / radio items | ✅ | AT-SPI `CHECKED` state bit + role |
| Separators | ✅ | AT-SPI `SEPARATOR` role |
| Keyboard shortcuts | ✅ | `org.a11y.atspi.Action.GetKeyBinding` — Alt-only mnemonics are filtered out |
| Icons | ✅ | Merged from DBus menu in the background enrichment pass. Falls back to a heuristic label→icon table (~100 standard FreeDesktop actions) when no DBus menu is available. |

---

## Wayland Prerequisites

On Wayland, KDE/Qt apps export their menus via the **Wayland `appmenu-v1` protocol**, which `kded5-appmenu` bridges to `com.canonical.AppMenu.Registrar`. For this to work:

1. **Apps must have their menu bar enabled.** If a previous global menu applet was active (KDE's own or another tool), it may have set `MenuBar=Disabled` in app config files, hiding the menu bar. Re-enable it with:
   ```bash
   kwriteconfig5 --file konsolerc   --group MainWindow --key MenuBar Enabled
   kwriteconfig5 --file dolphinrc   --group MainWindow --key MenuBar Enabled
   # Repeat for any other affected KDE apps
   ```
   Then reopen the affected apps.

2. **`kded5` appmenu module must be loaded** (it is by default in Plasma 5):
   ```bash
   qdbus org.kde.kded5 /kded org.kde.kded5.loadedModules | grep appmenu
   # expected output: appmenu
   ```

---
- **appmenu-registrar** must be installed for GTK app menus on X11 (Firefox, LibreOffice, Thunderbird): `sudo apt install appmenu-registrar`
- For GTK apps on **X11**, `appmenu-gtk3-module` must be loaded. Add to `/etc/environment`:
  ```
  GTK_MODULES=appmenu-gtk-module
  ```
  > **Wayland note:** GTK3/4 apps running natively on Wayland (e.g. HandBrake/ghb) do **not** require `appmenu-gtk3-module`. The service reads their menus directly via the `org.gtk.Menus` GMenuModel protocol.
- After installation, a **plasmashell restart** is required if the widget was already on the panel: `plasmashell --replace &`
- The `appsettings.json` config file **must** be present in the same directory as the binary (`/usr/local/bin/`). The install script handles this automatically; if deploying manually, copy it alongside the binary.

---

## Development

### Rebuild C++ plugin after changes

```bash
cd GlobalMMMenuPlugin/build
make -j$(nproc) && sudo make install
# Then reload plasmashell:
plasmashell --replace &
```

### Run the service in debug mode

```bash
# Stop the system service first
systemctl --user stop globalmmmenu

# Run directly (logs appear in the terminal)
dotnet run --project DBusService/DBusService/DBusService.csproj --configuration Debug
```

### Rebuild and redeploy service after changes

```bash
# Full publish + install + restart:
./install.sh --service

# Or manually:
dotnet publish DBusService/DBusService/DBusService.csproj --configuration Release --output /tmp/globalmmmenu-publish
sudo install -m 755 /tmp/globalmmmenu-publish/DBusService /usr/local/bin/globalmmmenu
sudo install -m 644 /tmp/globalmmmenu-publish/appsettings.json /usr/local/bin/appsettings.json
systemctl --user restart globalmmmenu
```

---


---

## License

GPL-2.0-or-later
