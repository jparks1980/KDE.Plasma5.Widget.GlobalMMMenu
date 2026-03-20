/****************************************************************************
** Meta object code from reading C++ file 'globalmenuhelper.h'
**
** Created by: The Qt Meta Object Compiler version 67 (Qt 5.15.13)
**
** WARNING! All changes made in this file will be lost!
*****************************************************************************/

#include <memory>
#include "../../../src/globalmenuhelper.h"
#include <QtCore/qbytearray.h>
#include <QtCore/qmetatype.h>
#if !defined(Q_MOC_OUTPUT_REVISION)
#error "The header file 'globalmenuhelper.h' doesn't include <QObject>."
#elif Q_MOC_OUTPUT_REVISION != 67
#error "This file was generated using the moc from 5.15.13. It"
#error "cannot be used with the include files from this version of Qt."
#error "(The moc has changed too much.)"
#endif

QT_BEGIN_MOC_NAMESPACE
QT_WARNING_PUSH
QT_WARNING_DISABLE_DEPRECATED
struct qt_meta_stringdata_GlobalMenuHelper_t {
    QByteArrayData data[20];
    char stringdata0[226];
};
#define QT_MOC_LITERAL(idx, ofs, len) \
    Q_STATIC_BYTE_ARRAY_DATA_HEADER_INITIALIZER_WITH_OFFSET(len, \
    qptrdiff(offsetof(qt_meta_stringdata_GlobalMenuHelper_t, stringdata0) + ofs \
        - idx * sizeof(QByteArrayData)) \
    )
static const qt_meta_stringdata_GlobalMenuHelper_t qt_meta_stringdata_GlobalMenuHelper = {
    {
QT_MOC_LITERAL(0, 0, 16), // "GlobalMenuHelper"
QT_MOC_LITERAL(1, 17, 15), // "menuOpenChanged"
QT_MOC_LITERAL(2, 33, 0), // ""
QT_MOC_LITERAL(3, 34, 19), // "currentIndexChanged"
QT_MOC_LITERAL(4, 54, 20), // "requestActivateIndex"
QT_MOC_LITERAL(5, 75, 5), // "index"
QT_MOC_LITERAL(6, 81, 13), // "menuTriggered"
QT_MOC_LITERAL(7, 95, 6), // "itemId"
QT_MOC_LITERAL(8, 102, 10), // "menuHidden"
QT_MOC_LITERAL(9, 113, 10), // "setButtons"
QT_MOC_LITERAL(10, 124, 7), // "buttons"
QT_MOC_LITERAL(11, 132, 11), // "setIconData"
QT_MOC_LITERAL(12, 144, 9), // "base64Png"
QT_MOC_LITERAL(13, 154, 10), // "clearIcons"
QT_MOC_LITERAL(14, 165, 14), // "openNativeMenu"
QT_MOC_LITERAL(15, 180, 11), // "QQuickItem*"
QT_MOC_LITERAL(16, 192, 6), // "anchor"
QT_MOC_LITERAL(17, 199, 4), // "node"
QT_MOC_LITERAL(18, 204, 8), // "menuOpen"
QT_MOC_LITERAL(19, 213, 12) // "currentIndex"

    },
    "GlobalMenuHelper\0menuOpenChanged\0\0"
    "currentIndexChanged\0requestActivateIndex\0"
    "index\0menuTriggered\0itemId\0menuHidden\0"
    "setButtons\0buttons\0setIconData\0base64Png\0"
    "clearIcons\0openNativeMenu\0QQuickItem*\0"
    "anchor\0node\0menuOpen\0currentIndex"
};
#undef QT_MOC_LITERAL

static const uint qt_meta_data_GlobalMenuHelper[] = {

 // content:
       8,       // revision
       0,       // classname
       0,    0, // classinfo
       9,   14, // methods
       2,   82, // properties
       0,    0, // enums/sets
       0,    0, // constructors
       0,       // flags
       5,       // signalCount

 // signals: name, argc, parameters, tag, flags
       1,    0,   59,    2, 0x06 /* Public */,
       3,    0,   60,    2, 0x06 /* Public */,
       4,    1,   61,    2, 0x06 /* Public */,
       6,    1,   64,    2, 0x06 /* Public */,
       8,    0,   67,    2, 0x06 /* Public */,

 // methods: name, argc, parameters, tag, flags
       9,    1,   68,    2, 0x02 /* Public */,
      11,    2,   71,    2, 0x02 /* Public */,
      13,    0,   76,    2, 0x02 /* Public */,
      14,    2,   77,    2, 0x02 /* Public */,

 // signals: parameters
    QMetaType::Void,
    QMetaType::Void,
    QMetaType::Void, QMetaType::Int,    5,
    QMetaType::Void, QMetaType::Int,    7,
    QMetaType::Void,

 // methods: parameters
    QMetaType::Void, QMetaType::QVariantList,   10,
    QMetaType::Void, QMetaType::QString, QMetaType::QString,    7,   12,
    QMetaType::Void,
    QMetaType::Void, 0x80000000 | 15, QMetaType::QVariantMap,   16,   17,

 // properties: name, type, flags
      18, QMetaType::Bool, 0x00495103,
      19, QMetaType::Int, 0x00495103,

 // properties: notify_signal_id
       0,
       1,

       0        // eod
};

void GlobalMenuHelper::qt_static_metacall(QObject *_o, QMetaObject::Call _c, int _id, void **_a)
{
    if (_c == QMetaObject::InvokeMetaMethod) {
        auto *_t = static_cast<GlobalMenuHelper *>(_o);
        (void)_t;
        switch (_id) {
        case 0: _t->menuOpenChanged(); break;
        case 1: _t->currentIndexChanged(); break;
        case 2: _t->requestActivateIndex((*reinterpret_cast< int(*)>(_a[1]))); break;
        case 3: _t->menuTriggered((*reinterpret_cast< int(*)>(_a[1]))); break;
        case 4: _t->menuHidden(); break;
        case 5: _t->setButtons((*reinterpret_cast< const QVariantList(*)>(_a[1]))); break;
        case 6: _t->setIconData((*reinterpret_cast< const QString(*)>(_a[1])),(*reinterpret_cast< const QString(*)>(_a[2]))); break;
        case 7: _t->clearIcons(); break;
        case 8: _t->openNativeMenu((*reinterpret_cast< QQuickItem*(*)>(_a[1])),(*reinterpret_cast< const QVariantMap(*)>(_a[2]))); break;
        default: ;
        }
    } else if (_c == QMetaObject::IndexOfMethod) {
        int *result = reinterpret_cast<int *>(_a[0]);
        {
            using _t = void (GlobalMenuHelper::*)();
            if (*reinterpret_cast<_t *>(_a[1]) == static_cast<_t>(&GlobalMenuHelper::menuOpenChanged)) {
                *result = 0;
                return;
            }
        }
        {
            using _t = void (GlobalMenuHelper::*)();
            if (*reinterpret_cast<_t *>(_a[1]) == static_cast<_t>(&GlobalMenuHelper::currentIndexChanged)) {
                *result = 1;
                return;
            }
        }
        {
            using _t = void (GlobalMenuHelper::*)(int );
            if (*reinterpret_cast<_t *>(_a[1]) == static_cast<_t>(&GlobalMenuHelper::requestActivateIndex)) {
                *result = 2;
                return;
            }
        }
        {
            using _t = void (GlobalMenuHelper::*)(int );
            if (*reinterpret_cast<_t *>(_a[1]) == static_cast<_t>(&GlobalMenuHelper::menuTriggered)) {
                *result = 3;
                return;
            }
        }
        {
            using _t = void (GlobalMenuHelper::*)();
            if (*reinterpret_cast<_t *>(_a[1]) == static_cast<_t>(&GlobalMenuHelper::menuHidden)) {
                *result = 4;
                return;
            }
        }
    }
#ifndef QT_NO_PROPERTIES
    else if (_c == QMetaObject::ReadProperty) {
        auto *_t = static_cast<GlobalMenuHelper *>(_o);
        (void)_t;
        void *_v = _a[0];
        switch (_id) {
        case 0: *reinterpret_cast< bool*>(_v) = _t->menuOpen(); break;
        case 1: *reinterpret_cast< int*>(_v) = _t->currentIndex(); break;
        default: break;
        }
    } else if (_c == QMetaObject::WriteProperty) {
        auto *_t = static_cast<GlobalMenuHelper *>(_o);
        (void)_t;
        void *_v = _a[0];
        switch (_id) {
        case 0: _t->setMenuOpen(*reinterpret_cast< bool*>(_v)); break;
        case 1: _t->setCurrentIndex(*reinterpret_cast< int*>(_v)); break;
        default: break;
        }
    } else if (_c == QMetaObject::ResetProperty) {
    }
#endif // QT_NO_PROPERTIES
}

QT_INIT_METAOBJECT const QMetaObject GlobalMenuHelper::staticMetaObject = { {
    QMetaObject::SuperData::link<QObject::staticMetaObject>(),
    qt_meta_stringdata_GlobalMenuHelper.data,
    qt_meta_data_GlobalMenuHelper,
    qt_static_metacall,
    nullptr,
    nullptr
} };


const QMetaObject *GlobalMenuHelper::metaObject() const
{
    return QObject::d_ptr->metaObject ? QObject::d_ptr->dynamicMetaObject() : &staticMetaObject;
}

void *GlobalMenuHelper::qt_metacast(const char *_clname)
{
    if (!_clname) return nullptr;
    if (!strcmp(_clname, qt_meta_stringdata_GlobalMenuHelper.stringdata0))
        return static_cast<void*>(this);
    return QObject::qt_metacast(_clname);
}

int GlobalMenuHelper::qt_metacall(QMetaObject::Call _c, int _id, void **_a)
{
    _id = QObject::qt_metacall(_c, _id, _a);
    if (_id < 0)
        return _id;
    if (_c == QMetaObject::InvokeMetaMethod) {
        if (_id < 9)
            qt_static_metacall(this, _c, _id, _a);
        _id -= 9;
    } else if (_c == QMetaObject::RegisterMethodArgumentMetaType) {
        if (_id < 9)
            *reinterpret_cast<int*>(_a[0]) = -1;
        _id -= 9;
    }
#ifndef QT_NO_PROPERTIES
    else if (_c == QMetaObject::ReadProperty || _c == QMetaObject::WriteProperty
            || _c == QMetaObject::ResetProperty || _c == QMetaObject::RegisterPropertyMetaType) {
        qt_static_metacall(this, _c, _id, _a);
        _id -= 2;
    } else if (_c == QMetaObject::QueryPropertyDesignable) {
        _id -= 2;
    } else if (_c == QMetaObject::QueryPropertyScriptable) {
        _id -= 2;
    } else if (_c == QMetaObject::QueryPropertyStored) {
        _id -= 2;
    } else if (_c == QMetaObject::QueryPropertyEditable) {
        _id -= 2;
    } else if (_c == QMetaObject::QueryPropertyUser) {
        _id -= 2;
    }
#endif // QT_NO_PROPERTIES
    return _id;
}

// SIGNAL 0
void GlobalMenuHelper::menuOpenChanged()
{
    QMetaObject::activate(this, &staticMetaObject, 0, nullptr);
}

// SIGNAL 1
void GlobalMenuHelper::currentIndexChanged()
{
    QMetaObject::activate(this, &staticMetaObject, 1, nullptr);
}

// SIGNAL 2
void GlobalMenuHelper::requestActivateIndex(int _t1)
{
    void *_a[] = { nullptr, const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t1))) };
    QMetaObject::activate(this, &staticMetaObject, 2, _a);
}

// SIGNAL 3
void GlobalMenuHelper::menuTriggered(int _t1)
{
    void *_a[] = { nullptr, const_cast<void*>(reinterpret_cast<const void*>(std::addressof(_t1))) };
    QMetaObject::activate(this, &staticMetaObject, 3, _a);
}

// SIGNAL 4
void GlobalMenuHelper::menuHidden()
{
    QMetaObject::activate(this, &staticMetaObject, 4, nullptr);
}
QT_WARNING_POP
QT_END_MOC_NAMESPACE
