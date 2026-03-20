#include "plugin.h"
#include "globalmenuhelper.h"
#include "iconprovider.h"

#include <qqml.h>
#include <QQmlEngine>
#include <QFile>
#include <QTextStream>
#include <QDateTime>

// Module-global provider. Created once when the engine initialises this plugin.
// GlobalMenuHelper instances call setIconProvider() on construction via the
// static accessor below.
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
    // Ownership is transferred to the engine — it deletes the provider on teardown.
    engine->addImageProvider(QStringLiteral("globalmenuicons"), s_iconProvider);

    // Startup diagnostic — confirms the plugin is actually loaded.
    QFile dbg("/tmp/gmm_debug.txt");
    if (dbg.open(QIODevice::Append | QIODevice::Text)) {
        QTextStream s(&dbg);
        s << "=== GlobalMenuHelper plugin loaded at " << QDateTime::currentDateTime().toString() << "\n";
    }
}
