#include "plugin.h"
#include "globalmenuhelper.h"
#include "iconprovider.h"

#include <qqml.h>
#include <QQmlEngine>

// Module-global provider. Created once when the engine initialises this plugin.
// GlobalMenuHelper instances auto-wire to it via the static accessor.
static GlobalMenuIconProvider *s_iconProvider = nullptr;

GlobalMenuIconProvider *GlobalMenuHelper::iconProvider()
{
    return s_iconProvider;
}

void GlobalMenuHelperPlugin::registerTypes(const char *uri)
{
    qmlRegisterType<GlobalMenuHelper>(uri, 1, 0, "GlobalMenuHelper");
}

void GlobalMenuHelperPlugin::initializeEngine(QQmlEngine *engine, const char *uri)
{
    Q_UNUSED(uri)
    s_iconProvider = new GlobalMenuIconProvider();
    // Ownership transferred to the engine — deleted on teardown.
    engine->addImageProvider(QStringLiteral("globalmenuicons"), s_iconProvider);
}

