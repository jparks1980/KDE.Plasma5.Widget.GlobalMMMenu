import QtQuick 2.15
import QtQuick.Layouts 1.15
import QtQuick.Controls 2.15 as QQC2
import org.kde.plasma.plasmoid 2.0
import org.kde.plasma.components 3.0 as PC3
import org.kde.plasma.core 2.0 as PlasmaCore
import com.kde.plasma.globalmenu 1.0

Item {
    id: root

    Plasmoid.preferredRepresentation: Plasmoid.fullRepresentation

    property var    menuData:        null
    property bool   menuOpen:        false
    property int    activeIndex:     -1
    property var    _buttonRepeater: null  // set by fullRepresentation
    property string lastRawOutput:   ""
    property string lastError:       ""
    property int    pollCount:       0
    // True when a real error (non-zero exit or parse failure) has occurred
    readonly property bool hasError: {
        if (!lastError) return false
        // "exit=0" with no extra text is not an error
        return lastError !== "exit=0" && lastError !== ""
    }

    // ── C++ hover-to-switch helper + native menu ──────────────────────────────
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
    }

    // C++ native menu signals → QML state
    Connections {
        target: menuHelper
        function onMenuTriggered(itemId) { root.executeItem(itemId) }
        function onMenuHidden() {
            root.menuOpen    = false
            root.activeIndex = -1
        }
    }

    function executeItem(itemId) {
        dbusSource.connectSource(
            "qdbus com.kde.GlobalMMMenu /com/kde/GlobalMMMenu com.kde.GlobalMMMenu.ExecuteItem " + itemId + " 2>&1"
        )
    }

    function cleanLabel(s) {
        if (!s) return s
        s = s.replace(/&&/g, "\u0000").replace(/&(?=.)/g,  "").replace(/\u0000/g, "&")
        s = s.replace(/__/g,  "\u0001").replace(/_(?=.)/g,  "").replace(/\u0001/g, "_")
        return s
    }

    function showTopMenu(btn, idx, node) {
        root.menuOpen    = true
        root.activeIndex = idx
        // Opens a native QMenu built entirely in C++ with QAction::setIcon()
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
        debugText.text = [
            "=== GlobalMMMenu Debug ===",
            "Poll count:  " + root.pollCount,
            "",
            "Last raw stdout:",
            root.lastRawOutput || "(empty)",
            "",
            "Last error / stderr:",
            root.lastError || "(none)",
            "",
            "menuData:",
            root.menuData ? JSON.stringify(root.menuData, null, 2) : "null",
            "",
            "Live logs:  journalctl --user -f | grep GlobalMMMenu",
            "Direct test:",
            "  qdbus com.kde.GlobalMMMenu /com/kde/GlobalMMMenu com.kde.GlobalMMMenu.GetActiveMenuJson"
        ].join("\n")
        debugPopup.open()
    }

    // ── Full representation ───────────────────────────────────────────────────
    Plasmoid.fullRepresentation: RowLayout {
        spacing: 0
        Layout.fillHeight: true

        Component.onCompleted: root._buttonRepeater = buttonRepeater

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

            // Keep the C++ helper's button list in sync when model changes.
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

                // 0=Rest 1=Hover 2=Pressed — ternary chain so QML tracks all deps
                property int menuState:
                    (root.menuOpen && root.activeIndex === index) ? 2 :
                    (hovered && !root.menuOpen)                   ? 1 : 0

                Layout.fillHeight: true
                visible: {
                    var t = root.cleanLabel(modelData.label || "")
                    return t !== "" && t !== "Root" && modelData.visible !== false
                }

                hoverEnabled: true

                // Padding on the button itself (not the label) — same as KDE's MenuDelegate
                topPadding:    bg.margins.top
                leftPadding:   bg.margins.left
                rightPadding:  bg.margins.right
                bottomPadding: bg.margins.bottom

                background: PlasmaCore.FrameSvgItem {
                    id: bg
                    imagePath: "widgets/menubaritem"
                    // ternary instead of switch — reliably reactive in QML bindings
                    prefix: menuState === 2 ? "pressed" : menuState === 1 ? "hover" : "normal"
                }

                contentItem: PC3.Label {
                    text: root.cleanLabel(modelData.label || "")
                    color: menuState === 0
                        ? PlasmaCore.Theme.textColor
                        : PlasmaCore.Theme.highlightedTextColor
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

        // Hide the spacer when there's no content at all — keeps the panel
        // from reserving blank space when no window menu is present.
        Item { Layout.fillWidth: true; visible: root.menuData !== null || root.hasError }
    }

    // ── D-Bus polling ─────────────────────────────────────────────────────────
    PlasmaCore.DataSource {
        id: dbusSource
        engine: "executable"
        onNewData: function(sourceName, data) {
            root.pollCount++
            var out    = (data["stdout"] || "").trim()
            var err    = (data["stderr"] || "").trim()
            var exitCd = data["exit code"] !== undefined ? data["exit code"] : "?"

            root.lastRawOutput = out
            root.lastError     = "exit=" + exitCd + (err ? "\n" + err : "")

            if (out.length > 2) {
                try {
                    var parsed = JSON.parse(out)
                    if (parsed && parsed.children && parsed.children.length > 0)
                        root.menuData = parsed
                    else
                        root.menuData = null
                } catch (e) {
                    root.lastError += "\nJSON parse error: " + e
                    root.menuData = null
                }
            } else {
                root.menuData = null
            }
            disconnectSource(sourceName)
        }
    }

    function poll() {
        dbusSource.connectSource(
            "qdbus com.kde.GlobalMMMenu /com/kde/GlobalMMMenu com.kde.GlobalMMMenu.GetActiveMenuJson 2>&1"
        )
    }

    Timer {
        interval: 150
        running: true
        repeat: true
        onTriggered: root.poll()
    }

    Component.onCompleted: root.poll()
}
