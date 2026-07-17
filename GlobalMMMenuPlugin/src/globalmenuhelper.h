#pragma once

#include <QDBusConnection>
#include <QList>
#include <QObject>
#include <QPointer>
#include <QTimer>
#include <QVariantList>
#include <QVariantMap>

class QQuickItem;
class QMenu;
class QDBusArgument;
class GlobalMenuIconProvider;

/**
 * GlobalMenuHelper — C++ back-end for the GlobalMMMenu Plasma widget.
 *
 * This class has two menu-source modes that feed the same menuJson property:
 *
 * 1. DIRECT MODE (preferred, "borrow stock GlobalMenu logic"):
 *    Activated by connectDirectMenu(service, path).  Connects directly to the
 *    D-Bus menu service advertised by the active window
 *    (ApplicationMenuServiceName / ApplicationMenuObjectPath from TasksModel).
 *    Uses com.canonical.dbusmenu GetLayout to fetch the menu structure and
 *    subscribes to LayoutUpdated for reactive updates.  The C# service is
 *    not involved.  Execution calls Event("clicked") on the menu service.
 *
 * 2. SERVICE MODE (fallback, for AT-SPI / GTK / unregistered windows):
 *    Activated by startServicePolling().  Polls the com.kde.GlobalMMMenu C#
 *    service via GetActiveMenuJson every 300 ms using a QTimer + QDBusInterface.
 *    The subprocess qdbus approach is gone — this is pure D-Bus with no
 *    process spawning, and it works in both Plasma 5 and Plasma 6.
 *
 * Other responsibilities:
 *  - Hover-to-switch: polls QCursor::pos() while a menu is open.
 *  - Native QMenu display with real QAction icons (icon-name + icon-data).
 *  - GlobalMenuIconProvider: serves PNG blobs via image://globalmenuicons/<id>.
 */
class GlobalMenuHelper : public QObject
{
    Q_OBJECT

    Q_PROPERTY(bool   menuOpen     READ menuOpen     WRITE setMenuOpen     NOTIFY menuOpenChanged)
    Q_PROPERTY(int    currentIndex READ currentIndex WRITE setCurrentIndex NOTIFY currentIndexChanged)

    // ── Menu data property (both modes write here) ────────────────────────
    // JSON string with the same schema the C# service uses:
    // { "label": "Root", "children": [ { "id": N, "label": "...", ... } ] }
    Q_PROPERTY(QString menuJson     READ menuJson     NOTIFY menuJsonChanged)

    // True when direct D-Bus mode is active (connected to a menu service directly).
    Q_PROPERTY(bool   directMode   READ directMode   NOTIFY directModeChanged)

public:
    explicit GlobalMenuHelper(QObject *parent = nullptr);
    ~GlobalMenuHelper() override;

    void setIconProvider(GlobalMenuIconProvider *provider);
    static GlobalMenuIconProvider *iconProvider();

    bool   menuOpen()    const { return m_menuOpen; }
    int    currentIndex() const { return m_currentIndex; }
    QString menuJson()    const { return m_menuJson; }
    bool   directMode()  const { return m_directMode; }

    void setMenuOpen(bool open);
    void setCurrentIndex(int index);

    Q_INVOKABLE void setButtons(const QVariantList &buttons);

    // ── Icon helpers ──────────────────────────────────────────────────────
    Q_INVOKABLE void setIconData(const QString &itemId, const QString &base64Png);
    Q_INVOKABLE void clearIcons();

    // ── Direct mode: stock-GlobalMenu-style D-Bus menu reading ────────────
    /// Connect directly to the window's advertised dbusmenu service.
    /// Fetches the menu immediately and subscribes to LayoutUpdated.
    Q_INVOKABLE void connectDirectMenu(const QString &service, const QString &path);
    /// Disconnect from the direct menu and clear menuJson.
    Q_INVOKABLE void disconnectDirectMenu();

    // ── Service mode: fallback polling of the C# GlobalMMMenu service ─────
    /// Start polling com.kde.GlobalMMMenu GetActiveMenuJson every 300 ms.
    Q_INVOKABLE void startServicePolling();
    /// Stop the polling timer.
    Q_INVOKABLE void stopServicePolling();

    // ── Native menu display ───────────────────────────────────────────────
    /// Build and show a native QMenu from the given menu-node QVariantMap.
    Q_INVOKABLE void openNativeMenu(QQuickItem *anchor, const QVariantMap &node);

signals:
    void menuOpenChanged();
    void currentIndexChanged();
    void menuJsonChanged();
    void directModeChanged();
    void requestActivateIndex(int index);
    /// Emitted when a menu item is activated; itemId matches the DBusMenu id.
    void menuTriggered(int itemId);
    /// Emitted when the native menu closes (user dismissed or opened a new one).
    void menuHidden();

private slots:
    void onLayoutUpdated(uint revision, int parentId);
    void onServicePollTimer();

private:
    // Hover-to-switch poll
    void   poll();

    // D-Bus menu parsing: converts com.canonical.dbusmenu GetLayout reply to QVariantMap
    void   fetchDirectMenu();
    static QVariantMap parseDbusMenuNode(const QDBusArgument &arg);
    static QString     cleanLabel(const QString &s);

    // Native QMenu builder
    QMenu *buildNativeMenu(const QVariantMap &node, bool isDirectMode);

    // Shared state
    QTimer                       m_hoverTimer;    // hover-to-switch (50 ms)
    QList<QPointer<QQuickItem>>  m_buttons;
    GlobalMenuIconProvider      *m_iconProvider = nullptr;
    QPointer<QMenu>              m_nativeMenu;
    bool                         m_menuOpen     = false;
    int                          m_currentIndex = -1;
    QString                      m_menuJson;

    // Direct mode state
    bool    m_directMode         = false;
    QString m_directMenuService;
    QString m_directMenuPath;

    // Service mode state
    QTimer  m_serviceTimer;    // 300 ms polling timer
};
