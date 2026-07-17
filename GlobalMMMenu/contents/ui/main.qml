import QtQuick 2.15
import QtQuick.Layouts 1.15
import QtQuick.Controls 2.15 as QQC2
import org.kde.plasma.plasmoid 2.0
import org.kde.plasma.components 3.0 as PC3
import org.kde.ksvg 1.0 as KSvg
import org.kde.kirigami as Kirigami
import org.kde.taskmanager 0.1 as TaskManager
import com.kde.plasma.globalmenu 1.0

// ────────────────────────────────────────────────────────────────────────────
// GlobalMMMenu — Global menu bar for Plasma 5 and Plasma 6
//
// Two menu-source modes (handled transparently by GlobalMenuHelper C++):
//
//   DIRECT MODE  (preferred, "borrows stock GlobalMenu logic"):
//     TasksModel reports ApplicationMenuServiceName + ApplicationMenuObjectPath
//     for the active window → menuHelper.connectDirectMenu(service, path).
//     GlobalMenuHelper reads dbusmenu GetLayout directly; no C# service needed.
//     Provides reactive updates via LayoutUpdated signal subscription.
//
//   SERVICE MODE  (fallback, for AT-SPI / GTK / unregistered windows):
//     Active window has no direct menu registration →
//     menuHelper.startServicePolling().
//     GlobalMenuHelper polls the C# GlobalMMMenu service every 300 ms via
//     com.kde.GlobalMMMenu GetActiveMenuJson (pure D-Bus, no subprocess).
//
// The PlasmaCore.DataSource / qdbus subprocess approach is gone — both modes
// use QDBusInterface directly from C++, making this QML file compatible with
// both Plasma 5 (org.kde.plasma.core 2.0) and Plasma 6 without any conditional
// import hacks.
// ────────────────────────────────────────────────────────────────────────────

PlasmoidItem {
    id: root

    property var    menuData:        null
    property bool   menuOpen:        false
    property int    activeIndex:     -1
    property var    _buttonRepeater: null
    // True when an error has occurred (non-empty lastError with real content)
    property string lastError:       ""
    readonly property bool hasError: {
        if (!lastError) return false
        return lastError !== "exit=0" && lastError !== ""
    }

    // ── TasksModel: detect active window's menu registration ─────────────────
    // When a window has ApplicationMenuServiceName + ApplicationMenuObjectPath,
    // we use the direct D-Bus path (stock GlobalMenu approach).
    // Otherwise we fall back to the C# service.
    TaskManager.TasksModel {
        id: tasksModel
        // Only match windows on the current screen (same as stock GlobalMenu).
        // Setting this prevents menus from other screens showing on this panel.
        filterByScreen: false

        onActiveTaskChanged: Qt.callLater(root.updateMenuSource)
        onDataChanged:       Qt.callLater(root.updateMenuSource)
        onCountChanged:      Qt.callLater(root.updateMenuSource)
    }

    // ── C++ helper: hover-to-switch + native menu display + menu fetching ────
    GlobalMenuHelper {
        id: menuHelper
        menuOpen:     root.menuOpen
        currentIndex: root.activeIndex

        onRequestActivateIndex: function(idx) {
            var rep = root._buttonRepeater
            if (!rep || !root.menuData || !root.menuData.children) return
            var btn  = rep.itemAt(idx)
            var node = root.menuData.children[idx]
            if (btn && node) root.showTopMenu(btn, idx, node)
        }

        // React to menu data changes from C++ (both direct and service modes)
        onMenuJsonChanged: root.applyMenuJson(menuHelper.menuJson)
    }

    Connections {
        target: menuHelper
        function onMenuTriggered(itemId) { /* execution handled in C++ */ }
        function onMenuHidden() {
            root.menuOpen    = false
            root.activeIndex = -1
        }
    }

    // ── Menu source logic ─────────────────────────────────────────────────────

    /// Called whenever TasksModel signals a change.  Picks the correct mode.
    function updateMenuSource() {
        var idx = tasksModel.activeTask
        if (!idx || !idx.valid) {
            menuHelper.startServicePolling()
            return
        }

        var service = tasksModel.data(idx, TaskManager.AbstractTasksModel.ApplicationMenuServiceName) || ""
        var path    = tasksModel.data(idx, TaskManager.AbstractTasksModel.ApplicationMenuObjectPath) || ""

        if (service !== "" && path !== "") {
            menuHelper.connectDirectMenu(service, path)
        } else {
            menuHelper.startServicePolling()
        }
    }

    /// Apply a raw JSON string (from either mode) to root.menuData.
    function applyMenuJson(json) {
        if (!json || json.length <= 2) {
            root.menuData  = null
            root.lastError = ""
            return
        }
        try {
            var parsed = JSON.parse(json)
            if (parsed && parsed.children && parsed.children.length > 0) {
                root.menuData  = parsed
                root.lastError = ""
            } else {
                root.menuData = null
            }
        } catch (e) {
            root.lastError = "JSON parse error: " + e
            root.menuData  = null
        }
    }

    // ── Menu actions ──────────────────────────────────────────────────────────

    function cleanLabel(s) {
        if (!s) return s
        s = s.replace(/&&/g, "\u0000").replace(/&(?=.)/g,  "").replace(/\u0000/g, "&")
        s = s.replace(/__/g,  "\u0001").replace(/_(?=.)/g,  "").replace(/\u0001/g, "_")
        return s
    }

    function showTopMenu(btn, idx, node) {
        root.menuOpen    = true
        root.activeIndex = idx
        menuHelper.openNativeMenu(btn.anchor, node)
    }

    // ── Debug popup ───────────────────────────────────────────────────────────
    QQC2.Popup {
        id: debugPopup
        modal: true
        focus: true
        closePolicy: QQC2.Popup.CloseOnEscape | QQC2.Popup.CloseOnPressOutside
        width: 600
        height: 400
        x: parent ? (parent.width  - width)  / 2 : 0
        y: parent ? (parent.height - height) / 2 : 0

        QQC2.ScrollView {
            anchors.fill: parent
            QQC2.TextArea {
                id: debugText
                readOnly: true
                wrapMode: Text.Wrap
                selectByMouse: true
                font.family: "monospace"
                font.pixelSize: 11
            }
        }
    }

    function showDebug() {
        var idx = tasksModel.activeTask
        var svc  = idx.valid ? (tasksModel.data(idx, TaskManager.AbstractTasksModel.ApplicationMenuServiceName) || "(none)") : "(no task)"
        var path = idx.valid ? (tasksModel.data(idx, TaskManager.AbstractTasksModel.ApplicationMenuObjectPath) || "(none)")  : "(no task)"
        debugText.text = [
            "=== GlobalMMMenu Debug ===",
            "Mode:        " + (menuHelper.directMode ? "DIRECT (no C# service)" : "SERVICE (C# fallback)"),
            "Direct svc:  " + svc,
            "Direct path: " + path,
            "",
            "Last error / parse failure:",
            root.lastError || "(none)",
            "",
            "menuData:",
            root.menuData ? JSON.stringify(root.menuData, null, 2) : "null",
            "",
            "Live logs:  journalctl --user -f | grep GlobalMMMenu",
            "Direct test (service mode):",
            "  qdbus com.kde.GlobalMMMenu /com/kde/GlobalMMMenu com.kde.GlobalMMMenu.GetActiveMenuJson"
        ].join("\n")
        debugPopup.open()
    }

    // ── Content ──────────────────────────────────────────────────────────────
    RowLayout {
        id: mainLayout
        anchors.fill: parent
        spacing: 0

        Component.onCompleted: {
            root._buttonRepeater = buttonRepeater
        }

        // Gear icon — only visible when an error has been detected
        PC3.ToolButton {
            Layout.fillHeight: true
            text: "⚙"
            visible: root.hasError
            opacity: 0.7
            onClicked: root.showDebug()
        }

        Repeater {
            id: buttonRepeater
            model: root.menuData && root.menuData.children ? root.menuData.children : []

            onCountChanged: {
                var btns = []
                for (var i = 0; i < buttonRepeater.count; i++) {
                    var item = buttonRepeater.itemAt(i)
                    if (item) btns.push(item)
                }
                menuHelper.setButtons(btns)
            }

            delegate: QQC2.AbstractButton {
                required property var modelData
                required property int index

                property Item anchor: dropAnchor

                property int menuState:
                    (root.menuOpen && root.activeIndex === index) ? 2 :
                    (hovered && !root.menuOpen)                   ? 1 : 0

                Layout.fillHeight: true
                visible: {
                    var t = root.cleanLabel(modelData.label || "")
                    return t !== "" && t !== "Root" && modelData.visible !== false
                }

                hoverEnabled: true

                topPadding:    bg.margins.top
                leftPadding:   bg.margins.left
                rightPadding:  bg.margins.right
                bottomPadding: bg.margins.bottom

                background: KSvg.FrameSvgItem {
                    id: bg
                    imagePath: "widgets/menubaritem"
                    prefix: menuState === 2 ? "pressed" : menuState === 1 ? "hover" : "normal"
                }

                contentItem: PC3.Label {
                    text: root.cleanLabel(modelData.label || "")
                    color: menuState === 0
                        ? Kirigami.Theme.textColor
                        : Kirigami.Theme.highlightedTextColor
                    horizontalAlignment: Text.AlignHCenter
                    verticalAlignment:   Text.AlignVCenter
                }

                Item {
                    id: dropAnchor
                    x: 0; y: parent.height + 6
                    width: 1; height: 1
                }

                onClicked: root.showTopMenu(this, index, modelData)
            }
        }

        Item { Layout.fillWidth: true; visible: root.menuData !== null || root.hasError }
    }

    Component.onCompleted: Qt.callLater(root.updateMenuSource)
}

