#pragma once

#include <QList>
#include <QObject>
#include <QPointer>
#include <QTimer>
#include <QVariantList>
#include <QVariantMap>

class QQuickItem;
class QMenu;
class GlobalMenuIconProvider;

/**
 * GlobalMenuHelper — C++ counterpart to KDE's nativeInterface/AppMenuPlugin.
 *
 * Responsibilities:
 *  - Receives the list of top-level menu buttons from QML.
 *  - While a menu is open, polls QCursor::pos() every 50 ms and emits
 *    requestActivateIndex(i) when the cursor moves over a different button.
 *  - Serves icon-data PNG bytes via GlobalMenuIconProvider so QML can use
 *    image://globalmenuicons/<itemId> as iconSource on menu items.
 */
class GlobalMenuHelper : public QObject
{
    Q_OBJECT

    Q_PROPERTY(bool menuOpen    READ menuOpen    WRITE setMenuOpen    NOTIFY menuOpenChanged)
    Q_PROPERTY(int  currentIndex READ currentIndex WRITE setCurrentIndex NOTIFY currentIndexChanged)

public:
    explicit GlobalMenuHelper(QObject *parent = nullptr);

    void setIconProvider(GlobalMenuIconProvider *provider);
    static GlobalMenuIconProvider *iconProvider();

    bool menuOpen()    const { return m_menuOpen; }    
    int  currentIndex() const { return m_currentIndex; }

    void setMenuOpen(bool open);
    void setCurrentIndex(int index);

    Q_INVOKABLE void setButtons(const QVariantList &buttons);

    /// Register base64-encoded PNG icon data for an item id.
    Q_INVOKABLE void setIconData(const QString &itemId, const QString &base64Png);

    /// Clear all cached icon data (call when a new menu is loaded).
    Q_INVOKABLE void clearIcons();

    /**
     * Build and show a native QMenu (with real QAction icons) from the given
     * menu-node QVariantMap.  The menu pops up at anchor's screen position.
     * Emits menuTriggered(itemId) on item activation, menuHidden() on close.
     */
    Q_INVOKABLE void openNativeMenu(QQuickItem *anchor, const QVariantMap &node);

signals:
    void menuOpenChanged();
    void currentIndexChanged();
    void requestActivateIndex(int index);
    /// Emitted when a menu item is activated; itemId matches the DBusMenu id.
    void menuTriggered(int itemId);
    /// Emitted when the native menu closes (user dismissed or new menu opened).
    void menuHidden();

private:
    void poll();
    QMenu  *buildNativeMenu(const QVariantMap &node);
    static QString cleanLabel(const QString &s);

    QTimer                       m_pollTimer;
    QList<QPointer<QQuickItem>>  m_buttons;
    GlobalMenuIconProvider      *m_iconProvider = nullptr;
    QPointer<QMenu>              m_nativeMenu;
    bool                         m_menuOpen     = false;
    int                          m_currentIndex  = -1;
};
