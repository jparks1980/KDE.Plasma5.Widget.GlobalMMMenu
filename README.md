# GlobalMMMenu

A KDE Plasma widget that displays a native global menu bar for the active window, using a C# DBus service to monitor window focus and fetch menus, plus a C++ QML plugin to render them as real native Qt menus.

## Overview

KDE Plasma's built-in global menu applet requires apps to export their menus via DBusMenu. **GlobalMMMenu** extends this with:

- A background **C# .NET service** that monitors the active X11 window and reads menu data via multiple discovery methods (see below), with intelligent caching so repeated focus events are near-instant.
- A **C++ QML plugin** (`libglobalmenuhelper.so`) that receives the menu JSON over DBus and pops up native `QMenu` instances with full icon and shortcut support.
- A **Plasma plasmoid** (`com.kde.plasma.globalmmenu`) that provides the panel widget UI and calls the C++ plugin.

---

## Architecture

```
X11 window focus change
        │
        ▼
X11ActiveWindowMonitor (C#)
        │
        ├─ 1. Window source cache ──────────────────────────────────────────┐
        │     Previously-seen windows go straight to their known provider.  │
        │                                                                   │
        ├─ 2. com.canonical.AppMenu.Registrar (live registry)               │
        │     Qt/KDE apps that call RegisterWindow during this session.     │
        │                                                                   │
        ├─ 3. _KDE_NET_WM_APPMENU_* X11 window properties                  │
        │     Persisted by the app; survives registrar restarts.            │
        │                                                                   │
        ├─ 4. PID-based DBus probe                                          │
        │     Scans /com/canonical/menu/<windowId> for apps that never      │
        │     registered but still export a dbusmenu object.                │
        │                                                                   │
        └─ 5. AT-SPI accessibility tree fallback  ◄────────────────────────┘
              Works for ALL Qt/KDE apps unconditionally — Qt always
              exports the full UI tree to AT-SPI regardless of whether
              dbusmenu was initialised.
        │
        ▼
GlobalMenuExporter (C#)  ←→  com.kde.GlobalMMMenu  DBus service
        │
        ├─ dbusmenu path: full icons + shortcuts from protocol
        │
        └─ AT-SPI path:  shortcuts from org.a11y.atspi.Action
                         icons guessed from FreeDesktop label→icon table
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
- KDE Plasma 5.x on X11 or XWayland
- .NET 10 runtime (`dotnet-runtime-10`)
- Qt5 (`libqt5qml5`, `libqt5widgets5`)
- `appmenu-gtk3-module` — for GTK app menus (Firefox, Thunderbird, etc.)
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

The service uses a **five-tier discovery chain**, from fastest to slowest:

### 1. Window source cache (fastest — subsequent focuses)
Once a window's menu provider is confirmed, the result is stored in a per-session `windowSources` dictionary keyed by X11 window ID. On all future focus events for that window, the service goes directly to the confirmed provider — no registration scan, no PID lookup, no polling.

- **DBus path**: re-uses the saved `service` + `path` to skip all discovery steps.
- **AT-SPI path**: re-uses the saved AT-SPI bus unique name to skip the expensive `ListNames()` + `GetConnectionUnixProcessID()` scan.

The cache is cleared on `StaleMenuPathException` (app restarted) so stale entries don't persist.

### 2. Live registrar registry
Qt/KDE apps that call `RegisterWindow` on `com.canonical.AppMenu.Registrar` during the current session. Always preferred over X11 props, which may carry a stale bus name from a previous service run.

### 3. X11 window properties
`_KDE_NET_WM_APPMENU_SERVICE_NAME` and `_KDE_NET_WM_APPMENU_OBJECT_PATH` — written by the app at startup and persisted in the X server. The service also writes these back after successful PID discovery so subsequent focus events resolve instantly.

### 4. PID-based DBus probe
Used when no registrar or X11 property data is available. Scans `/com/canonical/menu/<windowId>` and other candidate paths using the window's OS process ID.

### 5. AT-SPI accessibility tree fallback
Used for apps that have no dbusmenu object at all (e.g. apps launched before the registrar started, or builds that skipped dbusmenu initialisation). Qt unconditionally exports its full UI tree to AT-SPI, so this works for every Qt/KDE app.

The AT-SPI reader returns the fast menu tree immediately, then fires a background **enrichment pass** that fetches keyboard shortcuts (`org.a11y.atspi.Action.GetKeyBinding`) for each menu item in parallel batches of 8. The enriched menu is pushed to the widget via a second `UpdateAtSpi` call — the widget picks it up on its next 150 ms poll.

---

## AT-SPI Menu Features

| Feature | Available via AT-SPI | Notes |
|---|---|---|
| Menu labels | ✅ | `org.a11y.atspi.Accessible` Name property |
| Enabled/disabled state | ✅ | AT-SPI `SENSITIVE` + `ENABLED` state bits |
| Checkmark / radio items | ✅ | AT-SPI `CHECKED` state bit + role |
| Separators | ✅ | AT-SPI `SEPARATOR` role |
| Keyboard shortcuts | ✅ | `org.a11y.atspi.Action.GetKeyBinding` — Alt-only mnemonics are filtered out |
| Icons | ⚠️ | Qt's AT-SPI bridge does not expose `QAction::icon().name()`. A heuristic label→icon table covers ~100 standard FreeDesktop actions (New, Open, Save, Copy, Paste, Undo, Find, Preferences, Quit, Help, About, etc.) |

---

## Known Limitations / Notes

- **X11 / XWayland only.** Pure Wayland sessions are not supported because the service relies on X11 window properties.
- **appmenu-registrar** must be installed for GTK app menus (Firefox, LibreOffice, Thunderbird): `sudo apt install appmenu-registrar`
- For GTK apps, `appmenu-gtk3-module` must be loaded. Add to `/etc/environment`:
  ```
  GTK_MODULES=appmenu-gtk-module
  ```
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

## License

GPL-2.0-or-later


## Overview

KDE Plasma's built-in global menu applet requires apps to export their menus via DBusMenu. **GlobalMMMenu** extends this with:

- A background **C# .NET service** that monitors the active X11 window and reads menu data from `_KDE_NET_WM_APPMENU_SERVICE_NAME` / `_KDE_NET_WM_APPMENU_OBJECT_PATH` (the same atoms the native KDE global menu uses), with a fallback to `com.canonical.AppMenu.Registrar` for GTK apps.
- A **C++ QML plugin** (`libglobalmenuhelper.so`) that receives the menu JSON over DBus and pops up native `QMenu` instances with full icon and shortcut support.
- A **Plasma plasmoid** (`com.kde.plasma.globalmmenu`) that provides the panel widget UI and calls the C++ plugin.

---

## Architecture

```
X11 window focus change
        │
        ▼
X11ActiveWindowMonitor (C#)
  reads _KDE_NET_WM_APPMENU_SERVICE_NAME + _KDE_NET_WM_APPMENU_OBJECT_PATH
        │  (fallback: com.canonical.AppMenu.Registrar for GTK apps)
        ▼
Worker.cs fetches menu via com.canonical.dbusmenu → JSON
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
- KDE Plasma 5.x on X11 or XWayland
- .NET 10 runtime (`dotnet-runtime-10`)
- Qt5 (`libqt5qml5`, `libqt5widgets5`)
- `appmenu-gtk3-module` — for GTK app menus (Firefox, Thunderbird, etc.)
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
3. Build and install the C# DBus service as a self-contained binary at `/usr/local/bin/globalmmmenu`
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
    Worker.cs               X11 monitor + DBus menu fetcher
    GlobalMenuExporter.cs   Exposes com.kde.GlobalMMMenu on the session bus
    X11/
      X11ActiveWindowMonitor.cs   X11 event loop + window property reader
      NativeMethods.cs            libX11 P/Invoke bindings
    DBus/
      IAppMenuRegistrar.cs  com.canonical.AppMenu.Registrar interface
      IDbusMenu.cs          com.canonical.dbusmenu interface
      IGlobalMenuService.cs com.kde.GlobalMMMenu interface
      IKAppMenu.cs          org.kde.kappmenu interface

install.sh                  Full deployment script
```

---

## How Windows Are Detected

The service reads **X11 window properties** directly rather than relying solely on `com.canonical.AppMenu.Registrar`:

1. On every `_NET_ACTIVE_WINDOW` focus change, two properties are read from the window:
   - `_KDE_NET_WM_APPMENU_SERVICE_NAME` — DBus service name of the menu owner
   - `_KDE_NET_WM_APPMENU_OBJECT_PATH` — Object path of the dbusmenu tree
2. If those properties are not yet set (app still initialising), the service subscribes to `PropertyNotify` on that window and retries as soon as they appear — eliminating the startup race condition.
3. If neither property is ever set (GTK apps), falls back to `com.canonical.AppMenu.Registrar`.

---

## Known Limitations / Notes

- **X11 / XWayland only.** Pure Wayland sessions are not supported because the service relies on X11 window properties.
- **appmenu-registrar** must be installed for GTK app menus (Firefox, LibreOffice, Thunderbird): `sudo apt install appmenu-registrar`
- For GTK apps, `appmenu-gtk3-module` must be loaded. Add to `/etc/environment`:
  ```
  GTK_MODULES=appmenu-gtk-module
  ```
- After installation, a **plasmashell restart** is required if the widget was already on the panel: `plasmashell --replace &`

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

### Rebuild service after changes

```bash
# Quick rebuild without reinstalling the binary:
dotnet build DBusService/DBusService/DBusService.csproj --configuration Debug
# Then restart:
systemctl --user restart globalmmmenu
# Or use the install script to reinstall:
./install.sh --service
```

---

## License

GPL-2.0-or-later
