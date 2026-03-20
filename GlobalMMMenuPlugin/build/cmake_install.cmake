# Install script for directory: /home/joshd/Source/KDE/GlobalMMMenuPlugin

# Set the install prefix
if(NOT DEFINED CMAKE_INSTALL_PREFIX)
  set(CMAKE_INSTALL_PREFIX "/usr/local")
endif()
string(REGEX REPLACE "/$" "" CMAKE_INSTALL_PREFIX "${CMAKE_INSTALL_PREFIX}")

# Set the install configuration name.
if(NOT DEFINED CMAKE_INSTALL_CONFIG_NAME)
  if(BUILD_TYPE)
    string(REGEX REPLACE "^[^A-Za-z0-9_]+" ""
           CMAKE_INSTALL_CONFIG_NAME "${BUILD_TYPE}")
  else()
    set(CMAKE_INSTALL_CONFIG_NAME "Debug")
  endif()
  message(STATUS "Install configuration: \"${CMAKE_INSTALL_CONFIG_NAME}\"")
endif()

# Set the component getting installed.
if(NOT CMAKE_INSTALL_COMPONENT)
  if(COMPONENT)
    message(STATUS "Install component: \"${COMPONENT}\"")
    set(CMAKE_INSTALL_COMPONENT "${COMPONENT}")
  else()
    set(CMAKE_INSTALL_COMPONENT)
  endif()
endif()

# Install shared libraries without execute permission?
if(NOT DEFINED CMAKE_INSTALL_SO_NO_EXE)
  set(CMAKE_INSTALL_SO_NO_EXE "1")
endif()

# Is this installation the result of a crosscompile?
if(NOT DEFINED CMAKE_CROSSCOMPILING)
  set(CMAKE_CROSSCOMPILING "FALSE")
endif()

# Set default install directory permissions.
if(NOT DEFINED CMAKE_OBJDUMP)
  set(CMAKE_OBJDUMP "/usr/bin/objdump")
endif()

if(CMAKE_INSTALL_COMPONENT STREQUAL "Unspecified" OR NOT CMAKE_INSTALL_COMPONENT)
  if(EXISTS "$ENV{DESTDIR}/usr/lib/x86_64-linux-gnu/qt5/qml/com/kde/plasma/globalmenu/libglobalmenuhelper.so" AND
     NOT IS_SYMLINK "$ENV{DESTDIR}/usr/lib/x86_64-linux-gnu/qt5/qml/com/kde/plasma/globalmenu/libglobalmenuhelper.so")
    file(RPATH_CHECK
         FILE "$ENV{DESTDIR}/usr/lib/x86_64-linux-gnu/qt5/qml/com/kde/plasma/globalmenu/libglobalmenuhelper.so"
         RPATH "")
  endif()
  list(APPEND CMAKE_ABSOLUTE_DESTINATION_FILES
   "/usr/lib/x86_64-linux-gnu/qt5/qml/com/kde/plasma/globalmenu/libglobalmenuhelper.so")
  if(CMAKE_WARN_ON_ABSOLUTE_INSTALL_DESTINATION)
    message(WARNING "ABSOLUTE path INSTALL DESTINATION : ${CMAKE_ABSOLUTE_DESTINATION_FILES}")
  endif()
  if(CMAKE_ERROR_ON_ABSOLUTE_INSTALL_DESTINATION)
    message(FATAL_ERROR "ABSOLUTE path INSTALL DESTINATION forbidden (by caller): ${CMAKE_ABSOLUTE_DESTINATION_FILES}")
  endif()
  file(INSTALL DESTINATION "/usr/lib/x86_64-linux-gnu/qt5/qml/com/kde/plasma/globalmenu" TYPE MODULE FILES "/home/joshd/Source/KDE/GlobalMMMenuPlugin/build/libglobalmenuhelper.so")
  if(EXISTS "$ENV{DESTDIR}/usr/lib/x86_64-linux-gnu/qt5/qml/com/kde/plasma/globalmenu/libglobalmenuhelper.so" AND
     NOT IS_SYMLINK "$ENV{DESTDIR}/usr/lib/x86_64-linux-gnu/qt5/qml/com/kde/plasma/globalmenu/libglobalmenuhelper.so")
    if(CMAKE_INSTALL_DO_STRIP)
      execute_process(COMMAND "/usr/bin/strip" "$ENV{DESTDIR}/usr/lib/x86_64-linux-gnu/qt5/qml/com/kde/plasma/globalmenu/libglobalmenuhelper.so")
    endif()
  endif()
endif()

if(CMAKE_INSTALL_COMPONENT STREQUAL "Unspecified" OR NOT CMAKE_INSTALL_COMPONENT)
  list(APPEND CMAKE_ABSOLUTE_DESTINATION_FILES
   "/usr/lib/x86_64-linux-gnu/qt5/qml/com/kde/plasma/globalmenu/qmldir")
  if(CMAKE_WARN_ON_ABSOLUTE_INSTALL_DESTINATION)
    message(WARNING "ABSOLUTE path INSTALL DESTINATION : ${CMAKE_ABSOLUTE_DESTINATION_FILES}")
  endif()
  if(CMAKE_ERROR_ON_ABSOLUTE_INSTALL_DESTINATION)
    message(FATAL_ERROR "ABSOLUTE path INSTALL DESTINATION forbidden (by caller): ${CMAKE_ABSOLUTE_DESTINATION_FILES}")
  endif()
  file(INSTALL DESTINATION "/usr/lib/x86_64-linux-gnu/qt5/qml/com/kde/plasma/globalmenu" TYPE FILE FILES "/home/joshd/Source/KDE/GlobalMMMenuPlugin/qmldir")
endif()

if(CMAKE_INSTALL_COMPONENT STREQUAL "Unspecified" OR NOT CMAKE_INSTALL_COMPONENT)
  if(EXISTS "$ENV{DESTDIR}/home/joshd/.local/share/plasma/plasmoids/com.kde.plasma.globalmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so" AND
     NOT IS_SYMLINK "$ENV{DESTDIR}/home/joshd/.local/share/plasma/plasmoids/com.kde.plasma.globalmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so")
    file(RPATH_CHECK
         FILE "$ENV{DESTDIR}/home/joshd/.local/share/plasma/plasmoids/com.kde.plasma.globalmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so"
         RPATH "")
  endif()
  list(APPEND CMAKE_ABSOLUTE_DESTINATION_FILES
   "/home/joshd/.local/share/plasma/plasmoids/com.kde.plasma.globalmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so")
  if(CMAKE_WARN_ON_ABSOLUTE_INSTALL_DESTINATION)
    message(WARNING "ABSOLUTE path INSTALL DESTINATION : ${CMAKE_ABSOLUTE_DESTINATION_FILES}")
  endif()
  if(CMAKE_ERROR_ON_ABSOLUTE_INSTALL_DESTINATION)
    message(FATAL_ERROR "ABSOLUTE path INSTALL DESTINATION forbidden (by caller): ${CMAKE_ABSOLUTE_DESTINATION_FILES}")
  endif()
  file(INSTALL DESTINATION "/home/joshd/.local/share/plasma/plasmoids/com.kde.plasma.globalmmenu/com/kde/plasma/globalmenu" TYPE MODULE FILES "/home/joshd/Source/KDE/GlobalMMMenuPlugin/build/libglobalmenuhelper.so")
  if(EXISTS "$ENV{DESTDIR}/home/joshd/.local/share/plasma/plasmoids/com.kde.plasma.globalmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so" AND
     NOT IS_SYMLINK "$ENV{DESTDIR}/home/joshd/.local/share/plasma/plasmoids/com.kde.plasma.globalmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so")
    if(CMAKE_INSTALL_DO_STRIP)
      execute_process(COMMAND "/usr/bin/strip" "$ENV{DESTDIR}/home/joshd/.local/share/plasma/plasmoids/com.kde.plasma.globalmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so")
    endif()
  endif()
endif()

if(CMAKE_INSTALL_COMPONENT STREQUAL "Unspecified" OR NOT CMAKE_INSTALL_COMPONENT)
  if(EXISTS "$ENV{DESTDIR}/home/joshd/.local/share/plasma/plasmoids/com.github.globalmmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so" AND
     NOT IS_SYMLINK "$ENV{DESTDIR}/home/joshd/.local/share/plasma/plasmoids/com.github.globalmmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so")
    file(RPATH_CHECK
         FILE "$ENV{DESTDIR}/home/joshd/.local/share/plasma/plasmoids/com.github.globalmmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so"
         RPATH "")
  endif()
  list(APPEND CMAKE_ABSOLUTE_DESTINATION_FILES
   "/home/joshd/.local/share/plasma/plasmoids/com.github.globalmmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so")
  if(CMAKE_WARN_ON_ABSOLUTE_INSTALL_DESTINATION)
    message(WARNING "ABSOLUTE path INSTALL DESTINATION : ${CMAKE_ABSOLUTE_DESTINATION_FILES}")
  endif()
  if(CMAKE_ERROR_ON_ABSOLUTE_INSTALL_DESTINATION)
    message(FATAL_ERROR "ABSOLUTE path INSTALL DESTINATION forbidden (by caller): ${CMAKE_ABSOLUTE_DESTINATION_FILES}")
  endif()
  file(INSTALL DESTINATION "/home/joshd/.local/share/plasma/plasmoids/com.github.globalmmmenu/com/kde/plasma/globalmenu" TYPE MODULE FILES "/home/joshd/Source/KDE/GlobalMMMenuPlugin/build/libglobalmenuhelper.so")
  if(EXISTS "$ENV{DESTDIR}/home/joshd/.local/share/plasma/plasmoids/com.github.globalmmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so" AND
     NOT IS_SYMLINK "$ENV{DESTDIR}/home/joshd/.local/share/plasma/plasmoids/com.github.globalmmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so")
    if(CMAKE_INSTALL_DO_STRIP)
      execute_process(COMMAND "/usr/bin/strip" "$ENV{DESTDIR}/home/joshd/.local/share/plasma/plasmoids/com.github.globalmmmenu/com/kde/plasma/globalmenu/libglobalmenuhelper.so")
    endif()
  endif()
endif()

if(CMAKE_INSTALL_COMPONENT)
  set(CMAKE_INSTALL_MANIFEST "install_manifest_${CMAKE_INSTALL_COMPONENT}.txt")
else()
  set(CMAKE_INSTALL_MANIFEST "install_manifest.txt")
endif()

string(REPLACE ";" "\n" CMAKE_INSTALL_MANIFEST_CONTENT
       "${CMAKE_INSTALL_MANIFEST_FILES}")
file(WRITE "/home/joshd/Source/KDE/GlobalMMMenuPlugin/build/${CMAKE_INSTALL_MANIFEST}"
     "${CMAKE_INSTALL_MANIFEST_CONTENT}")
