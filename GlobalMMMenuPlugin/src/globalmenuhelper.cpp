#include "globalmenuhelper.h"
#include "iconprovider.h"

#include <QCursor>
#include <QDBusArgument>
#include <QDBusConnection>
#include <QDBusInterface>
#include <QDBusMessage>
#include <QDBusReply>
#include <QDebug>
#include <QFile>
#include <QIcon>
#include <QJsonDocument>
#include <QJsonObject>
#include <QJsonArray>
#include <QKeySequence>
#include <QMenu>
#include <QPixmap>
#include <QAction>
#include <QQuickItem>
#include <QQuickWindow>
#include <QTextStream>
#include <QVariant>
#include <QWindow>

GlobalMenuHelper::GlobalMenuHelper(QObject *parent)
    : QObject(parent)
{
    m_hoverTimer.setInterval(50);
    connect(&m_hoverTimer, &QTimer::timeout, this, &GlobalMenuHelper::poll);

    m_serviceTimer.setInterval(300);
    connect(&m_serviceTimer, &QTimer::timeout, this, &GlobalMenuHelper::onServicePollTimer);

    // Auto-wire to the module-global provider registered by the plugin.
    m_iconProvider = iconProvider();
}

GlobalMenuHelper::~GlobalMenuHelper()
{
    disconnectDirectMenu();
}

void GlobalMenuHelper::setIconProvider(GlobalMenuIconProvider *provider)
{
    m_iconProvider = provider;
}

void GlobalMenuHelper::setIconData(const QString &itemId, const QString &base64Png)
{
    if (m_iconProvider)
        m_iconProvider->setIconData(itemId, QByteArray::fromBase64(base64Png.toUtf8()));
}

void GlobalMenuHelper::clearIcons()
{
    if (m_iconProvider)
        m_iconProvider->clear();
}

void GlobalMenuHelper::setMenuOpen(bool open)
{
    if (m_menuOpen == open)
        return;
    m_menuOpen = open;
    if (open)
        m_hoverTimer.start();
    else
        m_hoverTimer.stop();
    emit menuOpenChanged();
}

void GlobalMenuHelper::setCurrentIndex(int index)
{
    if (m_currentIndex == index)
        return;
    m_currentIndex = index;
    emit currentIndexChanged();
}

void GlobalMenuHelper::setButtons(const QVariantList &buttons)
{
    m_buttons.clear();
    for (const QVariant &v : buttons) {
        if (auto *item = qvariant_cast<QQuickItem *>(v))
            m_buttons.append(item);
    }
}

void GlobalMenuHelper::poll()
{
    if (!m_menuOpen || m_currentIndex < 0)
        return;

    const QPoint cursor = QCursor::pos();

    for (int i = 0; i < m_buttons.size(); ++i) {
        QQuickItem *btn = m_buttons.at(i).data();
        if (!btn || !btn->isVisible() || btn->width() <= 0)
            continue;

        const QPointF topLeft = btn->mapToGlobal(QPointF(0, 0));
        const QRectF  rect(topLeft, QSizeF(btn->width(), btn->height()));

        if (rect.contains(QPointF(cursor)) && i != m_currentIndex) {
            emit requestActivateIndex(i);
            break;
        }
    }
}

// ── Direct mode: stock-GlobalMenu-style D-Bus menu reading ───────────────────

/**
 * Connect directly to a window's advertised dbusmenu service (borrow logic
 * from KDE's stock GlobalMenu widget / AppMenuModel).
 *
 * This path requires no C# service involvement — we call GetLayout on the
 * application's own menu service, subscribe to LayoutUpdated for reactivity,
 * and route action execution directly via the DBusMenu Event method.
 *
 * Called by QML's TasksModel handler when ApplicationMenuServiceName and
 * ApplicationMenuObjectPath are both available for the active task.
 */
void GlobalMenuHelper::connectDirectMenu(const QString &service, const QString &path)
{
    // Disconnect from any previous direct menu first
    if (!m_directMenuService.isEmpty()) {
        QDBusConnection::sessionBus().disconnect(
            m_directMenuService, m_directMenuPath,
            QStringLiteral("com.canonical.dbusmenu"),
            QStringLiteral("LayoutUpdated"),
            this, SLOT(onLayoutUpdated(uint,int)));
    }

    // Stop the C# service fallback timer if it was running
    m_serviceTimer.stop();

    m_directMenuService = service;
    m_directMenuPath    = path;
    m_directMode        = true;
    emit directModeChanged();

    // Subscribe to LayoutUpdated for reactive menu refreshes
    QDBusConnection::sessionBus().connect(
        service, path,
        QStringLiteral("com.canonical.dbusmenu"),
        QStringLiteral("LayoutUpdated"),
        this, SLOT(onLayoutUpdated(uint,int)));

    // Fetch the menu now
    fetchDirectMenu();
}

void GlobalMenuHelper::disconnectDirectMenu()
{
    if (!m_directMenuService.isEmpty()) {
        QDBusConnection::sessionBus().disconnect(
            m_directMenuService, m_directMenuPath,
            QStringLiteral("com.canonical.dbusmenu"),
            QStringLiteral("LayoutUpdated"),
            this, SLOT(onLayoutUpdated(uint,int)));
    }
    m_directMenuService.clear();
    m_directMenuPath.clear();

    if (m_directMode) {
        m_directMode = false;
        emit directModeChanged();
    }

    if (m_menuJson != QLatin1String("{}")) {
        m_menuJson = QStringLiteral("{}");
        emit menuJsonChanged();
    }
}

void GlobalMenuHelper::onLayoutUpdated(uint /*revision*/, int /*parentId*/)
{
    fetchDirectMenu();
}

/**
 * Call com.canonical.dbusmenu GetLayout on the direct menu service and convert
 * the reply to the same JSON schema the C# service uses.  Updates menuJson.
 */
void GlobalMenuHelper::fetchDirectMenu()
{
    if (m_directMenuService.isEmpty() || m_directMenuPath.isEmpty())
        return;

    QDBusInterface iface(
        m_directMenuService, m_directMenuPath,
        QStringLiteral("com.canonical.dbusmenu"),
        QDBusConnection::sessionBus());

    if (!iface.isValid())
        return;

    // GetLayout(parentId=0, recursionDepth=-1, propertyNames=[]) 
    // returns (uint revision, (int id, a{sv} props, av children))
    QDBusMessage reply = iface.call(
        QStringLiteral("GetLayout"), 0, -1, QStringList());

    if (reply.type() != QDBusMessage::ReplyMessage || reply.arguments().isEmpty())
        return;

    // reply.arguments()[0] = uint revision (skip)
    // reply.arguments()[1] = QDBusArgument holding (i, a{sv}, av)
    const QDBusArgument layoutArg = reply.arguments().at(1).value<QDBusArgument>();

    QVariantMap rootMap = parseDbusMenuNode(layoutArg);

    // Build JSON matching the C# service schema
    QJsonObject root;
    root[QStringLiteral("label")]    = rootMap.value(QStringLiteral("label")).toString();
    root[QStringLiteral("id")]       = rootMap.value(QStringLiteral("id")).toInt();

    const QVariantList children = rootMap.value(QStringLiteral("children")).toList();
    QJsonArray childArray;
    for (const QVariant &child : children) {
        childArray.append(QJsonObject::fromVariantMap(child.toMap()));
    }
    root[QStringLiteral("children")] = childArray;

    const QString json = QJsonDocument(root).toJson(QJsonDocument::Compact);
    if (json != m_menuJson) {
        m_menuJson = json;
        emit menuJsonChanged();
    }
}

/**
 * Recursively parse a com.canonical.dbusmenu layout node from a QDBusArgument.
 * The D-Bus signature is (i, a{sv}, av) where each av element is another
 * QDBusVariant wrapping a further (i, a{sv}, av).
 */
QVariantMap GlobalMenuHelper::parseDbusMenuNode(const QDBusArgument &arg)
{
    QVariantMap result;

    arg.beginStructure();

    int id = 0;
    arg >> id;
    result[QStringLiteral("id")] = id;

    // Properties: a{sv}
    QVariantMap props;
    arg >> props;

    const QString label   = props.value(QStringLiteral("label")).toString();
    const QString type    = props.value(QStringLiteral("type")).toString();
    const bool    enabled = props.value(QStringLiteral("enabled"), true).toBool();
    const bool    visible = props.value(QStringLiteral("visible"), true).toBool();
    const QString iconName = props.value(QStringLiteral("icon-name")).toString();

    result[QStringLiteral("label")]   = label;
    result[QStringLiteral("enabled")] = enabled;
    result[QStringLiteral("visible")] = visible;
    if (!type.isEmpty())
        result[QStringLiteral("type")] = type;
    if (!iconName.isEmpty())
        result[QStringLiteral("icon-name")] = iconName;

    // Handle shortcuts: dbusmenu stores as "shortcut" a{sv} value
    if (props.contains(QStringLiteral("shortcut"))) {
        result[QStringLiteral("shortcut")] = props[QStringLiteral("shortcut")];
    }

    // Children: av, each element is a QDBusVariant wrapping (i, a{sv}, av)
    QVariantList childrenList;
    arg.beginArray();
    while (!arg.atEnd()) {
        QDBusVariant childDbusVar;
        arg >> childDbusVar;
        const QDBusArgument childArg = childDbusVar.variant().value<QDBusArgument>();
        QVariantMap childMap = parseDbusMenuNode(childArg);
        if (childMap.value(QStringLiteral("visible"), true).toBool())
            childrenList.append(childMap);
    }
    arg.endArray();

    if (!childrenList.isEmpty())
        result[QStringLiteral("children")] = childrenList;

    arg.endStructure();
    return result;
}

// ── Service mode: fallback polling of the C# GlobalMMMenu service ─────────────

void GlobalMenuHelper::startServicePolling()
{
    disconnectDirectMenu();   // ensure direct mode is off
    m_serviceTimer.start();
    // Fetch immediately so the first render doesn't wait 300 ms
    onServicePollTimer();
}

void GlobalMenuHelper::stopServicePolling()
{
    m_serviceTimer.stop();
}

void GlobalMenuHelper::onServicePollTimer()
{
    QDBusInterface iface(
        QStringLiteral("com.kde.GlobalMMMenu"),
        QStringLiteral("/com/kde/GlobalMMMenu"),
        QStringLiteral("com.kde.GlobalMMMenu"),
        QDBusConnection::sessionBus());

    if (!iface.isValid())
        return;

    QDBusReply<QString> reply = iface.call(QStringLiteral("GetActiveMenuJson"));
    if (!reply.isValid())
        return;

    const QString json = reply.value().trimmed();
    if (json.length() > 2 && json != m_menuJson) {
        m_menuJson = json;
        emit menuJsonChanged();
    } else if (json.length() <= 2 && !m_menuJson.isEmpty()) {
        m_menuJson.clear();
        emit menuJsonChanged();
    }
}

// ── Native QMenu / QAction display ───────────────────────────────────────────

void GlobalMenuHelper::openNativeMenu(QQuickItem *anchor, const QVariantMap &node)
{
    // Notify the application that this menu is about to be shown.
    // Required by the dbusmenu protocol so dynamic menus can populate themselves
    // and so the app tracks which items are "open" for Event() delivery.
    if (m_directMode && !m_directMenuService.isEmpty()) {
        const int parentId = node.value(QStringLiteral("id"), 0).toInt();
        QDBusInterface abIface(m_directMenuService, m_directMenuPath,
            QStringLiteral("com.canonical.dbusmenu"),
            QDBusConnection::sessionBus());
        abIface.call(QDBus::NoBlock, QStringLiteral("AboutToShow"), parentId);
    }

    QMenu *menu = buildNativeMenu(node, m_directMode);

    QPointer<QMenu> oldMenu = m_nativeMenu;
    m_nativeMenu = menu;

    if (oldMenu)
        oldMenu->close();

    QPoint screenPos;
    if (anchor) {
        QPointF global = anchor->mapToGlobal(QPointF(0, 0));
        screenPos = global.toPoint();
    }

    // Wayland: make QMenu a transient popup so the compositor treats it as
    // a panel popup rather than a full xdg_toplevel with title bar.
    if (anchor && anchor->window()) {
        menu->winId();
        if (QWindow *menuWin = menu->windowHandle())
            menuWin->setTransientParent(anchor->window());
    }

    auto *capturedMenu = menu;
    const bool directNow = m_directMode;
    connect(menu, &QMenu::aboutToHide, this, [this, capturedMenu, directNow]() {
        if (m_nativeMenu == capturedMenu) {
            m_nativeMenu = nullptr;
            if (!directNow) {
                // Notify the C# service that the popup is closing so it can
                // unfreeze its active proxy (fallback / service mode only).
                QDBusInterface iface(
                    QStringLiteral("com.kde.GlobalMMMenu"),
                    QStringLiteral("/com/kde/GlobalMMMenu"),
                    QStringLiteral("com.kde.GlobalMMMenu"));
                iface.call(QDBus::NoBlock, QStringLiteral("SetMenuOpen"), false);
            }
            emit menuHidden();
        }
    });

    if (!directNow) {
        // Freeze the C# service's active proxy BEFORE the popup opens.
        // On Wayland, opening the panel popup causes the app to re-focus its
        // last-active window which fires KWin windowActivated.  Without the
        // freeze the service would overwrite _activeMenu before the user clicks.
        // Must be blocking so the freeze is in effect before menu->popup().
        QDBusInterface iface(
            QStringLiteral("com.kde.GlobalMMMenu"),
            QStringLiteral("/com/kde/GlobalMMMenu"),
            QStringLiteral("com.kde.GlobalMMMenu"));
        iface.call(QDBus::Block, QStringLiteral("SetMenuOpen"), true);
    }

    menu->popup(screenPos);
}

/**
 * Build a native QMenu from a JSON-like QVariantMap node.
 * In direct mode, action triggers call com.canonical.dbusmenu Event directly.
 * In service mode, action triggers call com.kde.GlobalMMMenu ExecuteItem.
 */
QMenu *GlobalMenuHelper::buildNativeMenu(const QVariantMap &node, bool isDirectMode)
{
    auto *menu = new QMenu();

    const QVariantList children = node.value(QStringLiteral("children")).toList();

    for (const QVariant &childVar : children) {
        const QVariantMap child = childVar.toMap();

        if (child.value(QStringLiteral("type")).toString() == QLatin1String("separator")) {
            menu->addSeparator();
            continue;
        }

        const QString      label       = cleanLabel(child.value(QStringLiteral("label")).toString());
        const QString      iconName    = child.value(QStringLiteral("icon-name")).toString();
        const QString      iconDataB64 = child.value(QStringLiteral("icon-data")).toString();
        const bool         enabled     = child.value(QStringLiteral("enabled"), true).toBool();
        const int          itemId      = child.value(QStringLiteral("id")).toInt();
        const QVariantList subkids     = child.value(QStringLiteral("children")).toList();
        const QVariantList shortcutRaw = child.value(QStringLiteral("shortcut")).toList();

        QIcon icon;
        if (!iconName.isEmpty())
            icon = QIcon::fromTheme(iconName);
        if (icon.isNull() && !iconDataB64.isEmpty()) {
            QPixmap pix;
            if (pix.loadFromData(QByteArray::fromBase64(iconDataB64.toUtf8())))
                icon = QIcon(pix);
        }

        if (!subkids.isEmpty()) {
            QMenu *sub = buildNativeMenu(child, isDirectMode);
            sub->setTitle(label);
            if (!icon.isNull()) {
                sub->setIcon(icon);
                sub->menuAction()->setIconVisibleInMenu(true);
            }
            menu->addMenu(sub);
        } else {
            auto *action = new QAction(label, menu);
            action->setEnabled(enabled);
            if (!icon.isNull()) {
                action->setIcon(icon);
                action->setIconVisibleInMenu(true);
            }
            // Parse DBusMenu shortcut: [["Control","Shift","S"]] → Ctrl+Shift+S
            if (!shortcutRaw.isEmpty()) {
                const QVariantList combo = shortcutRaw.first().toList();
                if (!combo.isEmpty()) {
                    int modifiers = Qt::NoModifier;
                    QString key;
                    for (const QVariant &part : combo) {
                        const QString token = part.toString();
                        if (token == QLatin1String("Control"))   modifiers |= Qt::ControlModifier;
                        else if (token == QLatin1String("Shift")) modifiers |= Qt::ShiftModifier;
                        else if (token == QLatin1String("Alt"))   modifiers |= Qt::AltModifier;
                        else if (token == QLatin1String("Super")) modifiers |= Qt::MetaModifier;
                        else key = token;
                    }
                    if (!key.isEmpty()) {
                        const QKeySequence ks(modifiers | QKeySequence(key)[0]);
                        if (!ks.isEmpty())
                            action->setShortcut(ks);
                    }
                }
            }

            if (isDirectMode) {
                // Direct mode: send Event("clicked") to the application's dbusmenu.
                // The "data" parameter must be a D-Bus variant (type 'v'), so wrap
                // the zero value in QDBusVariant; sending a plain QVariant(int)
                // produces type 'i' which some implementations (e.g. Konsole) reject.
                const QString svc  = m_directMenuService;
                const QString path = m_directMenuPath;
                connect(action, &QAction::triggered, this, [svc, path, itemId]() {
                    QDBusInterface iface(svc, path,
                        QStringLiteral("com.canonical.dbusmenu"),
                        QDBusConnection::sessionBus());
                    const quint32 ts = static_cast<quint32>(
                        QDateTime::currentMSecsSinceEpoch() / 1000);
                    iface.call(QDBus::AutoDetect,
                        QStringLiteral("Event"),
                        itemId,
                        QStringLiteral("clicked"),
                        QVariant::fromValue(QDBusVariant(QVariant(0))),
                        ts);
                });
            } else {
                // Service mode: ExecuteItem on the C# service.
                // Must be synchronous (QDBus::Block) — see detailed comment
                // in the original implementation for the Wayland focus-race rationale.
                connect(action, &QAction::triggered, this, [itemId]() {
                    QDBusInterface iface(
                        QStringLiteral("com.kde.GlobalMMMenu"),
                        QStringLiteral("/com/kde/GlobalMMMenu"),
                        QStringLiteral("com.kde.GlobalMMMenu"));
                    iface.call(QDBus::Block, QStringLiteral("ExecuteItem"), itemId);
                });
            }

            menu->addAction(action);
        }
    }

    return menu;
}

// Strip &X / _X mnemonics (keep doubled && / __ as literal & / _).
QString GlobalMenuHelper::cleanLabel(const QString &s)
{
    QString r;
    r.reserve(s.size());
    for (int i = 0; i < s.size(); ++i) {
        const QChar c = s[i];
        if ((c == QLatin1Char('&') || c == QLatin1Char('_')) && i + 1 < s.size()) {
            if (s[i + 1] == c) {
                r += c;
                ++i;
            }
        } else {
            r += c;
        }
    }
    return r;
}


