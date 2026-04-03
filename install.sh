#!/usr/bin/env bash
# =============================================================================
# GlobalMMMenu — Installation Script
# =============================================================================
# Builds and deploys all components:
#   1. C++ QML plugin  (GlobalMMMenuPlugin)
#   2. Plasma plasmoid (GlobalMMMenu)
#   3. C# DBus service (DBusService)
#   4. systemd user service (auto-start on login)
#
# Usage:
#   ./install.sh            # full install
#   ./install.sh --plugin   # C++ plugin only
#   ./install.sh --plasmoid # plasmoid QML only
#   ./install.sh --service  # DBus service only
#   ./install.sh --uninstall
# =============================================================================

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="$REPO_DIR/GlobalMMMenuPlugin"
PLASMOID_DIR="$REPO_DIR/GlobalMMMenu"
SERVICE_DIR="$REPO_DIR/DBusService/DBusService"

PLASMOID_ID="com.kde.plasma.globalmmenu"
SERVICE_BIN="/usr/local/bin/globalmmmenu"
SYSTEMD_USER_DIR="$HOME/.config/systemd/user"
SERVICE_UNIT="$SYSTEMD_USER_DIR/globalmmmenu.service"

# ── Colours ───────────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
BLUE='\033[0;34m'; BOLD='\033[1m'; NC='\033[0m'

info()    { echo -e "${BLUE}[INFO]${NC}  $*"; }
success() { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()    { echo -e "${YELLOW}[WARN]${NC}  $*"; }
die()     { echo -e "${RED}[ERROR]${NC} $*"; exit 1; }

# ── Dependency checks ─────────────────────────────────────────────────────────
check_deps() {
    local missing=()
    for cmd in cmake make dotnet kpackagetool5 plasmashell; do
        command -v "$cmd" &>/dev/null || missing+=("$cmd")
    done
    if [[ ${#missing[@]} -gt 0 ]]; then
        die "Missing required tools: ${missing[*]}\n  Run: sudo apt install cmake build-essential dotnet-sdk-10"
    fi

    # Qt5 dev libraries
    if ! pkg-config --exists Qt5Qml Qt5Quick Qt5Gui Qt5Widgets 2>/dev/null; then
        die "Qt5 development libraries not found.\n  Run: sudo apt install qtbase5-dev qtdeclarative5-dev"
    fi

    # appmenu-registrar (GTK app menu support)
    if ! command -v appmenu-registrar &>/dev/null && ! systemctl --user is-active appmenu-registrar &>/dev/null; then
        warn "appmenu-registrar not found — GTK app menus (Firefox, Thunderbird etc.) will not work."
        warn "Run: sudo apt install appmenu-registrar"
    fi
}

# ── Step 1: C++ QML Plugin ────────────────────────────────────────────────────
install_plugin() {
    echo -e "\n${BOLD}── Step 1: Building C++ QML plugin ──────────────────────────────────${NC}"

    local build_dir="$PLUGIN_DIR/build"
    mkdir -p "$build_dir"

    info "Running cmake..."
    cmake -S "$PLUGIN_DIR" -B "$build_dir" -DCMAKE_BUILD_TYPE=Release

    info "Building..."
    make -C "$build_dir" -j"$(nproc)"

    info "Installing (requires sudo)..."
    sudo make -C "$build_dir" install

    # Update local plasmoid copies (no sudo needed)
    local qml_dest_a="$HOME/.local/share/plasma/plasmoids/$PLASMOID_ID/com/kde/plasma/globalmenu"
    local qml_dest_b="$HOME/.local/share/plasma/plasmoids/com.github.globalmmmenu/com/kde/plasma/globalmenu"

    for dest in "$qml_dest_a" "$qml_dest_b"; do
        if [[ -d "$dest" ]]; then
            cp "$build_dir/libglobalmenuhelper.so" "$dest/"
            success "Updated $dest"
        fi
    done

    success "C++ plugin installed."
}

# ── Step 2: Plasma Plasmoid ───────────────────────────────────────────────────
install_plasmoid() {
    echo -e "\n${BOLD}── Step 2: Installing Plasma plasmoid ───────────────────────────────${NC}"

    local plasmoid_dest="$HOME/.local/share/plasma/plasmoids/$PLASMOID_ID"

    info "Copying plasmoid files..."
    mkdir -p "$plasmoid_dest/contents/ui"
    cp "$PLASMOID_DIR/metadata.json" "$plasmoid_dest/"
    cp "$PLASMOID_DIR/contents/ui/main.qml" "$plasmoid_dest/contents/ui/"

    # Copy QML plugin dir (qmldir + .so) into the plasmoid bundle
    local qml_bundle="$plasmoid_dest/com/kde/plasma/globalmenu"
    mkdir -p "$qml_bundle"
    local system_qml
    system_qml="$(qmake -query QT_INSTALL_QML 2>/dev/null)/com/kde/plasma/globalmenu"
    if [[ -f "$system_qml/libglobalmenuhelper.so" ]]; then
        cp "$system_qml/libglobalmenuhelper.so" "$qml_bundle/"
        cp "$PLUGIN_DIR/qmldir" "$qml_bundle/"
        success "Bundled QML plugin into plasmoid."
    else
        warn "System QML plugin not found at $system_qml — run --plugin first, or full install."
    fi

    # Register with KDE
    if kpackagetool5 --list --type Plasma/Applet 2>/dev/null | grep -q "$PLASMOID_ID"; then
        info "Upgrading existing plasmoid registration..."
        kpackagetool5 --upgrade "$plasmoid_dest" --type Plasma/Applet 2>/dev/null || true
    else
        info "Registering plasmoid with KDE..."
        kpackagetool5 --install "$plasmoid_dest" --type Plasma/Applet 2>/dev/null || true
    fi

    success "Plasmoid installed."
    info "To add to panel: right-click panel → Add Widgets → search 'Global MM Menu'"
}

# ── Step 3: DBus Service ──────────────────────────────────────────────────────
install_service() {
    echo -e "\n${BOLD}── Step 3: Building and installing DBus service ─────────────────────${NC}"

    info "Publishing self-contained executable..."
    dotnet publish "$SERVICE_DIR/DBusService.csproj" \
        --configuration Release \
        --output /tmp/globalmmmenu-publish

    info "Installing to $SERVICE_BIN (requires sudo)..."
    sudo install -m 755 /tmp/globalmmmenu-publish/DBusService "$SERVICE_BIN"

    # Copy appsettings.json alongside the binary — the service WorkingDirectory is
    # /usr/local/bin, so the binary looks for config files there at runtime.
    sudo install -m 644 /tmp/globalmmmenu-publish/appsettings.json "$(dirname "$SERVICE_BIN")/appsettings.json"
    if [[ -f /tmp/globalmmmenu-publish/appsettings.Development.json ]]; then
        sudo install -m 644 /tmp/globalmmmenu-publish/appsettings.Development.json "$(dirname "$SERVICE_BIN")/appsettings.Development.json"
    fi

    success "Service binary and config installed at $(dirname "$SERVICE_BIN")."
}

# ── Step 4: systemd user service ──────────────────────────────────────────────
install_systemd() {
    echo -e "\n${BOLD}── Step 4: Installing systemd user service ──────────────────────────${NC}"

    mkdir -p "$SYSTEMD_USER_DIR"
    cp "$SERVICE_DIR/globalmmmenu.service" "$SERVICE_UNIT"

    info "Reloading systemd user daemon..."
    systemctl --user daemon-reload

    info "Enabling service (auto-start on graphical login)..."
    systemctl --user enable globalmmmenu.service

    info "Starting service now..."
    systemctl --user start globalmmmenu.service

    if systemctl --user is-active --quiet globalmmmenu.service; then
        success "Service is running."
    else
        warn "Service did not start cleanly. Check: journalctl --user -u globalmmmenu"
    fi
}

# ── Uninstall ─────────────────────────────────────────────────────────────────
do_uninstall() {
    echo -e "\n${BOLD}── Uninstalling GlobalMMMenu ─────────────────────────────────────────${NC}"

    # Stop and disable service
    systemctl --user stop globalmmmenu.service 2>/dev/null || true
    systemctl --user disable globalmmmenu.service 2>/dev/null || true
    [[ -f "$SERVICE_UNIT" ]] && rm "$SERVICE_UNIT"
    systemctl --user daemon-reload

    # Remove service binary
    [[ -f "$SERVICE_BIN" ]] && sudo rDBusSerm "$SERVICE_BIN" && success "Removed $SERVICE_BIN"

    # Remove plasmoid
    local plasmoid_dest="$HOME/.local/share/plasma/plasmoids/$PLASMOID_ID"
    kpackagetool5 --remove "$PLASMOID_ID" --type Plasma/Applet 2>/dev/null || true
    [[ -d "$plasmoid_dest" ]] && rm -rf "$plasmoid_dest" && success "Removed plasmoid"

    # Remove system QML plugin
    local qml_dir
    qml_dir="$(qmake -query QT_INSTALL_QML 2>/dev/null)/com/kde/plasma/globalmenu"
    [[ -d "$qml_dir" ]] && sudo rm -rf "$qml_dir" && success "Removed system QML plugin"

    success "Uninstall complete. Restart plasmashell: plasmashell --replace &"
}

# ── Main ──────────────────────────────────────────────────────────────────────
main() {
    echo -e "${BOLD}GlobalMMMenu Installer${NC}"
    echo "────────────────────────────────────────────────────────────────────"

    case "${1:-}" in
        --plugin)
            check_deps
            install_plugin
            ;;
        --plasmoid)
            install_plasmoid
            ;;
        --service)
            check_deps
            install_service
            install_systemd
            ;;
        --uninstall)
            do_uninstall
            ;;
        "")
            check_deps
            install_plugin
            install_plasmoid
            install_service
            install_systemd
            echo ""
            echo -e "${GREEN}${BOLD}Installation complete!${NC}"
            echo ""
            echo "  Next steps:"
            echo "  1. Add the widget: right-click panel → Add Widgets → 'Global MM Menu'"
            echo "  2. Check service:  systemctl --user status globalmmmenu"
            echo "  3. View logs:      journalctl --user -u globalmmmenu -f"
            ;;
        *)
            echo "Usage: $0 [--plugin | --plasmoid | --service | --uninstall]"
            exit 1
            ;;
    esac
}

main "${@}"
