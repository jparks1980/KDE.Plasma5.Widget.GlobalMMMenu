cmake_minimum_required(VERSION 3.16)
project(GlobalMMMenuPlugin CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_AUTOMOC ON)

find_package(Qt5 5.15 REQUIRED COMPONENTS Core Qml Quick Gui Widgets)

add_library(globalmenuhelper MODULE
    src/plugin.h
    src/plugin.cpp
    src/globalmenuhelper.h
    src/globalmenuhelper.cpp
    src/iconprovider.h
    src/iconprovider.cpp
)

target_include_directories(globalmenuhelper PRIVATE src)

target_link_libraries(globalmenuhelper
    PRIVATE
    Qt5::Core
    Qt5::Qml
    Qt5::Quick
    Qt5::Gui
    Qt5::Widgets
)

# Install to the system Qt QML path — the same location used by
# org.kde.plasma.private.appmenu, which guarantees plasmashell finds it
# without any QML2_IMPORT_PATH changes. Requires one-time `sudo make install`.
execute_process(
    COMMAND qmake -query QT_INSTALL_QML
    OUTPUT_VARIABLE QT_QML_DIR
    OUTPUT_STRIP_TRAILING_WHITESPACE)
set(QML_INSTALL_DIR
    "${QT_QML_DIR}/com/kde/plasma/globalmenu"
    CACHE PATH "QML plugin install directory")

install(TARGETS globalmenuhelper
    LIBRARY DESTINATION "${QML_INSTALL_DIR}")

install(FILES "${CMAKE_SOURCE_DIR}/qmldir"
    DESTINATION "${QML_INSTALL_DIR}")

# Also update the user-local plasmoid-bundled copies so the running widget
# always picks up the latest build without needing a separate manual copy step.
set(LOCAL_PLASMOID_DIRS
    "$ENV{HOME}/.local/share/plasma/plasmoids/com.kde.plasma.globalmmenu/com/kde/plasma/globalmenu"
    "$ENV{HOME}/.local/share/plasma/plasmoids/com.github.globalmmmenu/com/kde/plasma/globalmenu"
)
foreach(LOCAL_DIR IN LISTS LOCAL_PLASMOID_DIRS)
    if(EXISTS "${LOCAL_DIR}")
        install(TARGETS globalmenuhelper
            LIBRARY DESTINATION "${LOCAL_DIR}")
    endif()
endforeach()
