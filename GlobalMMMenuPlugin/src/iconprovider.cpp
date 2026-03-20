#include "iconprovider.h"

#include <QImage>

GlobalMenuIconProvider::GlobalMenuIconProvider()
    : QQuickImageProvider(QQuickImageProvider::Image)
{
}

QImage GlobalMenuIconProvider::requestImage(const QString &id, QSize *size, const QSize &requestedSize)
{
    QReadLocker lock(&m_lock);
    const QByteArray &data = m_icons.value(id);
    if (data.isEmpty())
        return QImage();

    QImage img = QImage::fromData(data);
    if (size)
        *size = img.size();
    if (requestedSize.isValid() && !requestedSize.isEmpty())
        return img.scaled(requestedSize, Qt::KeepAspectRatio, Qt::SmoothTransformation);
    return img;
}

void GlobalMenuIconProvider::setIconData(const QString &itemId, const QByteArray &pngData)
{
    QWriteLocker lock(&m_lock);
    m_icons.insert(itemId, pngData);
}

void GlobalMenuIconProvider::clear()
{
    QWriteLocker lock(&m_lock);
    m_icons.clear();
}
