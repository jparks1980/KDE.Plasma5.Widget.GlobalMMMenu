#include "globalmenuhelper.h"
#include "iconprovider.h"

#include <QCursor>
#include <QDebug>
#include <QFile>
#include <QIcon>
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
    m_pollTimer.setInterval(50);
    connect(&m_pollTimer, &QTimer::timeout, this, &GlobalMenuHelper::poll);
    // Auto-wire to the module-global provider registered by the plugin.
    m_iconProvider = iconProvider();
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
        m_pollTimer.start();
    else
        m_pollTimer.stop();
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

        // mapToGlobal maps the item's (0,0) to screen-global logical pixels —
        // the same space QCursor::pos() uses, including HiDPI scaling.
        const QPointF topLeft = btn->mapToGlobal(QPointF(0, 0));
        const QRectF  rect(topLeft, QSizeF(btn->width(), btn->height()));

        if (rect.contains(QPointF(cursor)) && i != m_currentIndex) {
            emit requestActivateIndex(i);
            break;
        }
    }
}

// ---------------------------------------------------------------------------
// Native QMenu / QAction implementation
// ---------------------------------------------------------------------------

void GlobalMenuHelper::openNativeMenu(QQuickItem *anchor, const QVariantMap &node)
{
    {
        QFile dbg("/tmp/gmm_debug.txt");
        if (dbg.open(QIODevice::Append | QIODevice::Text)) {
            QTextStream s(&dbg);
            s << "=== openNativeMenu called, node label=" << node.value("label").toString()
              << "  keys=" << node.keys().join(",") << "\n";
        }
    }

    QMenu *menu = buildNativeMenu(node);

    // Update m_nativeMenu BEFORE closing old so that old menu's aboutToHide
    // signal sees a different pointer and doesn't emit menuHidden().
    QPointer<QMenu> oldMenu = m_nativeMenu;
    m_nativeMenu = menu;

    if (oldMenu)
        oldMenu->close();

    // Compute screen position from the anchor QQuickItem.
    // QQuickItem::mapToGlobal() is the correct cross-platform way: it handles
    // the local→scene→window→screen chain including HiDPI and Wayland offsets.
    QPoint screenPos;
    if (anchor) {
        QPointF global = anchor->mapToGlobal(QPointF(0, 0));
        screenPos = global.toPoint();
    }

    // ── Wayland: make QMenu a transient popup, not a top-level window ──────
    // On Wayland, a QMenu without a transient parent is treated as an
    // xdg_toplevel by KWin and gets a full title bar + window decorations.
    // Calling winId() forces QWindow creation, then setTransientParent() tells
    // the compositor this is a popup that belongs to the panel window.
    // On X11 this sets WM_TRANSIENT_FOR which is correct and harmless.
    if (anchor && anchor->window()) {
        menu->winId(); // ensures windowHandle() is non-null
        if (QWindow *menuWin = menu->windowHandle())
            menuWin->setTransientParent(anchor->window());
    }

    auto *capturedMenu = menu;
    connect(menu, &QMenu::aboutToHide, this, [this, capturedMenu]() {
        if (m_nativeMenu == capturedMenu) {
            m_nativeMenu = nullptr;
            emit menuHidden();
        }
    });

    menu->popup(screenPos);
}

QMenu *GlobalMenuHelper::buildNativeMenu(const QVariantMap &node)
{
    auto *menu = new QMenu();

    const QVariantList children = node.value(QStringLiteral("children")).toList();

    // Write debug info to a file for easy inspection
    QFile dbg("/tmp/gmm_debug.txt");
    if (dbg.open(QIODevice::Append | QIODevice::Text)) {
        QTextStream s(&dbg);
        s << "=== buildNativeMenu: " << node.value("label").toString()
          << "  children=" << children.size()
          << "  iconTheme=" << QIcon::themeName()
          << "  searchPaths=" << QIcon::themeSearchPaths().join(":")
          << "  testIcon=" << (QIcon::fromTheme("document-open").isNull() ? "NULL" : "OK")
          << "\n";
        for (const QVariant &cv : children) {
            const QVariantMap c = cv.toMap();
            const QString iname = c.value("icon-name").toString();
            const QString idata = c.value("icon-data").toString();
            const bool themeOk = !iname.isEmpty() && !QIcon::fromTheme(iname).isNull();
            const bool dataOk  = !idata.isEmpty();
            s << "  [" << c.value("id").toInt() << "] "
              << c.value("label").toString()
              << "  icon-name=" << iname
              << "  themeOk=" << (themeOk ? "YES" : "NO")
              << "  has-icon-data=" << (dataOk ? "YES" : "NO")
              << "\n";
        }
    }

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

        // Prefer theme icon; fall back to inline PNG bytes from DBusMenu icon-data.
        QIcon icon;
        if (!iconName.isEmpty())
            icon = QIcon::fromTheme(iconName);
        if (icon.isNull() && !iconDataB64.isEmpty()) {
            QPixmap pix;
            if (pix.loadFromData(QByteArray::fromBase64(iconDataB64.toUtf8())))
                icon = QIcon(pix);
        }

        if (!subkids.isEmpty()) {
            QMenu *sub = buildNativeMenu(child);
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
                        if (token == QLatin1String("Control"))    modifiers |= Qt::ControlModifier;
                        else if (token == QLatin1String("Shift"))  modifiers |= Qt::ShiftModifier;
                        else if (token == QLatin1String("Alt"))    modifiers |= Qt::AltModifier;
                        else if (token == QLatin1String("Super"))  modifiers |= Qt::MetaModifier;
                        else key = token;
                    }
                    if (!key.isEmpty()) {
                        const QKeySequence ks(modifiers | QKeySequence(key)[0]);
                        if (!ks.isEmpty())
                            action->setShortcut(ks);
                    }
                }
            }
            connect(action, &QAction::triggered, this, [this, itemId]() {
                emit menuTriggered(itemId);
            });
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
                r += c;   // doubled delimiter → keep one literal
                ++i;
            }
            // else: single mnemonic marker → skip
        } else {
            r += c;
        }
    }
    return r;
}
