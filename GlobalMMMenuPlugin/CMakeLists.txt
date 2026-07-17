cmake_minimum_required(VERSION 3.16)
project(GlobalMMMenuPlugin CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_AUTOMOC ON)

# ── Qt version detection ──────────────────────────────────────────────────────
# Prefer Qt6 (Plasma 6) unless the caller forces Qt5 with -DQT_MAJOR_VERSION=5.
# Install required dev packages on Debian/Ubuntu:
#   Qt6: sudo apt install qt6-base-dev qt6-declarative-dev
#   Qt5: sudo apt install qtbase5-dev qtdeclarative5-dev
if (NOT DEFINED QT_MAJOR_VERSION)
    find_package(Qt6 QUIET COMPONENTS Core)
    if (Qt6_FOUND)
        set(QT_MAJOR_VERSION 6)
    else()
        set(QT_MAJOR_VERSION 5)
    endif()
endif()

message(STATUS "Building GlobalMMMenuPlugin for Qt${QT_MAJOR_VERSION} (Plasma ${QT_MAJOR_VERSION})")

find_package(Qt${QT_MAJOR_VERSION} REQUIRED COMPONENTS Core Qml Quick Gui Widgets DBus)

add_library(globalmenuhelper MODULE
    src/plugin.h
    src/plugin.cpp
    src/globalmenuhelper.h
    src/globalmenuhelper.cpp
    src/iconprovider.h
    src/iconprovider.cpp
)

target_include_directories(globalmenuhelper PRIVATE src)

target_compile_definitions(globalmenuhelper PRIVATE
    QT_MAJOR_VERSION=${QT_MAJOR_VERSION}
)

target_link_libraries(globalmenuhelper
    PRIVATE
    Qt${QT_MAJOR_VERSION}::Core
    Qt${QT_MAJOR_VERSION}::Qml
    Qt${QT_MAJOR_VERSION}::Quick
    Qt${QT_MAJOR_VERSION}::Gui
    Qt${QT_MAJOR_VERSION}::Widgets
    Qt${QT_MAJOR_VERSION}::DBus
)

# ── QML install path ──────────────────────────────────────────────────────────
# Detect the correct QML installation directory for the selected Qt version.
# Qt6 uses /usr/lib/.../qt6/qml, Qt5 uses /usr/lib/.../qt5/qml — the two are
# separate so both versions of the plugin can coexist on the same machine.
if (QT_MAJOR_VERSION EQUAL 6)
    find_program(QT_QMAKE_EXECUTABLE NAMES qmake6 qmake6-qt6
        HINTS ${Qt6_DIR}/../../../bin)
    if (NOT QT_QMAKE_EXECUTABLE)
        # Fallback: ask cmake's Qt6 target directly
        get_target_property(_qmake6 Qt6::qmake IMPORTED_LOCATION)
        if (_qmake6)
            set(QT_QMAKE_EXECUTABLE "${_qmake6}")
        endif()
    endif()
    if (NOT QT_QMAKE_EXECUTABLE)
        # Last resort: try to find qmake in PATH but verify it's Qt6
        find_program(QT_QMAKE_EXECUTABLE NAMES qmake)
    endif()
else()
    find_program(QT_QMAKE_EXECUTABLE NAMES qmake5 qmake)
endif()

if (NOT QT_QMAKE_EXECUTABLE)
    message(FATAL_ERROR "qmake not found for Qt${QT_MAJOR_VERSION}")
endif()

execute_process(
    COMMAND ${QT_QMAKE_EXECUTABLE} -query QT_INSTALL_QML
    OUTPUT_VARIABLE QT_QML_DIR
    OUTPUT_STRIP_TRAILING_WHITESPACE
)

if (NOT QT_QML_DIR)
    message(FATAL_ERROR "Could not determine QT_INSTALL_QML from ${QT_QMAKE_EXECUTABLE}")
endif()

set(QML_INSTALL_DIR
    "${QT_QML_DIR}/com/kde/plasma/globalmenu"
    CACHE PATH "QML plugin install directory")

message(STATUS "QML install directory: ${QML_INSTALL_DIR}")

install(TARGETS globalmenuhelper
    LIBRARY DESTINATION "${QML_INSTALL_DIR}")

install(FILES "${CMAKE_SOURCE_DIR}/qmldir"
    DESTINATION "${QML_INSTALL_DIR}")


