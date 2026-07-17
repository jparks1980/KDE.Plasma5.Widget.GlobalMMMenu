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

# ── Plasma version detection ─────────────────────────────────────────────────
# Returns 6 for Plasma 6+, 5 otherwise.
get_plasma_major() {
    local ver
    ver=$(plasmashell --version 2>/dev/null | grep -oP '\d+' | head -1)
    echo "${ver:-5}"
}
PLASMA_MAJOR=$(get_plasma_major)
info "Detected Plasma ${PLASMA_MAJOR}"

# kpackagetool: Plasma 6 ships kpackagetool6; Plasma 5 uses kpackagetool5.
if [[ $PLASMA_MAJOR -ge 6 ]]; then
    KPACKAGETOOL=$(command -v kpackagetool6 2>/dev/null || command -v kpackagetool5 2>/dev/null || echo "")
else
    KPACKAGETOOL=$(command -v kpackagetool5 2>/dev/null || echo "")
fi

# ── Dependency checks ─────────────────────────────────────────────────────────
check_deps() {
    local missing=()
    for cmd in cmake make dotnet plasmashell; do
        command -v "$cmd" &>/dev/null || missing+=("$cmd")
    done
    [[ -z "$KPACKAGETOOL" ]] && missing+=("kpackagetool5 or kpackagetool6")
    if [[ ${#missing[@]} -gt 0 ]]; then
        die "Missing required tools: ${missing[*]}\n  Run: sudo apt install cmake build-essential dotnet-sdk-10"
    fi

    # Qt dev libraries — check for Qt6 first (Plasma 6), fall back to Qt5
    if [[ $PLASMA_MAJOR -ge 6 ]]; then
        if ! dpkg -l qt6-base-dev qt6-declarative-dev &>/dev/null 2>&1; then
            die "Qt6 development libraries not found.\n  Run: sudo apt install qt6-base-dev qt6-declarative-dev"
        fi
    else
        if ! pkg-config --exists Qt5Qml Qt5Quick Qt5Gui Qt5Widgets 2>/dev/null; then
            die "Qt5 development libraries not found.\n  Run: sudo apt install qtbase5-dev qtdeclarative5-dev"
        fi
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

    info "Running cmake (Qt${PLASMA_MAJOR})..."
    cmake -S "$PLUGIN_DIR" -B "$build_dir" -DCMAKE_BUILD_TYPE=Release \
        -DQT_MAJOR_VERSION="$PLASMA_MAJOR"

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
    # Prefer build output; fall back to system QML path
    local build_so="$PLUGIN_DIR/build/libglobalmenuhelper.so"
    if [[ $PLASMA_MAJOR -ge 6 ]]; then
        system_qml="/usr/lib/x86_64-linux-gnu/qt6/qml/com/kde/plasma/globalmenu"
    else
        system_qml="$(qmake -query QT_INSTALL_QML 2>/dev/null)/com/kde/plasma/globalmenu"
    fi
    local so_src=""
    [[ -f "$build_so" ]] && so_src="$build_so"
    [[ -z "$so_src" && -f "$system_qml/libglobalmenuhelper.so" ]] && so_src="$system_qml/libglobalmenuhelper.so"
    if [[ -n "$so_src" ]]; then
        cp "$so_src" "$qml_bundle/"
        cp "$PLUGIN_DIR/qmldir" "$qml_bundle/"
        success "Bundled QML plugin into plasmoid from $so_src"
    else
        warn "libglobalmenuhelper.so not found — run --plugin first, or full install."
    fi

    # Register with KDE
    # Plasma discovers plasmoids by directory name, so create a symlink from
    # the canonical ID directory → the actual com.github.globalmmmenu directory.
    local canonical_dir="$HOME/.local/share/plasma/plasmoids/com.github.globalmmmenu"
    local id_link="$HOME/.local/share/plasma/plasmoids/$PLASMOID_ID"
    if [[ ! -e "$id_link" || "$(readlink "$id_link")" != "$canonical_dir" ]]; then
        ln -sfn "$canonical_dir" "$id_link"
        info "Created symlink: $id_link → $canonical_dir"
    fi

    # Rebuild KDE service cache (Plasma 5: kbuildsycoca5; Plasma 6: kbuildsycoca6)
    if [[ $PLASMA_MAJOR -ge 6 ]]; then
        kbuildsycoca6 --noincremental 2>/dev/null || true
    else
        kbuildsycoca5 --noincremental 2>/dev/null || true
    fi

    if "$KPACKAGETOOL" --list --type Plasma/Applet 2>/dev/null | grep -q "$PLASMOID_ID"; then
        info "Upgrading existing plasmoid registration..."
        "$KPACKAGETOOL" --upgrade "$plasmoid_dest" --type Plasma/Applet 2>/dev/null || true
    else
        info "Registering plasmoid with KDE..."
        "$KPACKAGETOOL" --install "$plasmoid_dest" --type Plasma/Applet 2>/dev/null || true
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

    # Ensure XDG_RUNTIME_DIR is available for systemctl --user (needed when the
    # script is invoked via sudo or from a shell that did not inherit the user
    # session environment).
    export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"

    if [[ ! -d "$XDG_RUNTIME_DIR" ]]; then
        warn "XDG_RUNTIME_DIR ($XDG_RUNTIME_DIR) does not exist — cannot reach the user D-Bus session."
        warn "The service unit was written to $SERVICE_UNIT."
        warn "Run the following commands as your normal user to activate it:"
        warn "  systemctl --user daemon-reload"
        warn "  systemctl --user enable --now globalmmmenu.service"
        return 0
    fi

    info "Reloading systemd user daemon..."
    systemctl --user daemon-reload

    info "Enabling service (auto-start on graphical login)..."
    systemctl --user enable globalmmmenu.service

    info "Restarting service..."
    systemctl --user restart globalmmmenu.service

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
    "$KPACKAGETOOL" --remove "$PLASMOID_ID" --type Plasma/Applet 2>/dev/null || true
    [[ -d "$plasmoid_dest" ]] && rm -rf "$plasmoid_dest" && success "Removed plasmoid"

    # Remove system QML plugin (Qt5 and Qt6 paths)
    local qml_dir5 qml_dir6
    qml_dir5="$(qmake -query QT_INSTALL_QML 2>/dev/null)/com/kde/plasma/globalmenu"
    qml_dir6="/usr/lib/x86_64-linux-gnu/qt6/qml/com/kde/plasma/globalmenu"
    [[ -d "$qml_dir5" ]] && sudo rm -rf "$qml_dir5" && success "Removed Qt5 system QML plugin"
    [[ -d "$qml_dir6" ]] && sudo rm -rf "$qml_dir6" && success "Removed Qt6 system QML plugin"

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
            # Also sync QML and metadata to the running plasmoid (both known install locations).
            for plasmoid_dest_dir in \
                "$HOME/.local/share/plasma/plasmoids/$PLASMOID_ID" \
                "$HOME/.local/share/plasma/plasmoids/com.github.globalmmmenu"; do
                if [[ -d "$plasmoid_dest_dir/contents/ui" ]]; then
                    cp "$PLASMOID_DIR/contents/ui/main.qml" "$plasmoid_dest_dir/contents/ui/main.qml"
                    cp "$PLASMOID_DIR/metadata.json" "$plasmoid_dest_dir/metadata.json"
                    info "Synced QML + metadata → $plasmoid_dest_dir"
                fi
            done
            # Ensure symlink exists so Plasma finds the plugin by its canonical ID
            local canonical_dir="$HOME/.local/share/plasma/plasmoids/com.github.globalmmmenu"
            local id_link="$HOME/.local/share/plasma/plasmoids/$PLASMOID_ID"
            if [[ ! -e "$id_link" || "$(readlink "$id_link")" != "$canonical_dir" ]]; then
                ln -sfn "$canonical_dir" "$id_link"
                info "Created symlink: $id_link → $canonical_dir"
            fi
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
