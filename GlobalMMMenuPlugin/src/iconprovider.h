#pragma once

#include <QHash>
#include <QQuickImageProvider>
#include <QReadWriteLock>

/**
 * GlobalMenuIconProvider — serves icon-data PNG bytes from DBusMenu items
 * as QML images using the "globalmenuicons" scheme:
 *   iconSource: "image://globalmenuicons/<itemId>"
 *
 * Icon data is registered from QML via GlobalMenuHelper::setIconData().
 * It is cleared automatically when a new menu is loaded.
 */
class GlobalMenuIconProvider : public QQuickImageProvider
{
public:
    GlobalMenuIconProvider();

    QImage requestImage(const QString &id, QSize *size, const QSize &requestedSize) override;

    void setIconData(const QString &itemId, const QByteArray &pngData);
    void clear();

private:
    QHash<QString, QByteArray> m_icons;
    mutable QReadWriteLock     m_lock;
};
