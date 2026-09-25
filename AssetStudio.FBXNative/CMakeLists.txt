# Builds libAssetStudio.FBXNative.so (Linux) / .dylib (macOS). Windows uses the .vcxproj.
#
#   cmake -S AssetStudio.FBXNative -B build -DFBXSDK_ROOT=/path/to/fbxsdk \
#         -DZLIB_STATIC=/path/libz.a -DLIBXML2_STATIC=/path/libxml2.a
#   cmake --build build
#
# Everything (FBX SDK, libxml2, zlib, libstdc++) is linked statically so the library only depends on
# glibc; system libxml2 sonames differ between distributions (libxml2.so.2 vs .so.16).
# See build-fbxnative-linux.sh for a script that also builds zlib/libxml2.
cmake_minimum_required(VERSION 3.16)
project(AssetStudio.FBXNative CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

set(FBXSDK_ROOT "" CACHE PATH "Autodesk FBX SDK install directory (contains include/ and lib/)")
set(ZLIB_STATIC "" CACHE FILEPATH "static zlib (libz.a, built with -fPIC)")
set(LIBXML2_STATIC "" CACHE FILEPATH "static libxml2 (libxml2.a, built with -fPIC)")

if(NOT EXISTS "${FBXSDK_ROOT}/include/fbxsdk.h")
  message(FATAL_ERROR "FBXSDK_ROOT must point to the FBX SDK (missing ${FBXSDK_ROOT}/include/fbxsdk.h)")
endif()
if(CMAKE_BUILD_TYPE STREQUAL "Debug")
  set(FBXSDK_LIB "${FBXSDK_ROOT}/lib/debug/libfbxsdk.a")
else()
  set(FBXSDK_LIB "${FBXSDK_ROOT}/lib/release/libfbxsdk.a")
endif()
foreach(lib FBXSDK_LIB ZLIB_STATIC LIBXML2_STATIC)
  if(NOT EXISTS "${${lib}}")
    message(FATAL_ERROR "${lib} not found: '${${lib}}'")
  endif()
endforeach()

add_library(AssetStudio.FBXNative SHARED
  api.cpp
  asfbx_anim_context.cpp
  asfbx_context.cpp
  asfbx_morph_context.cpp
  asfbx_skin_context.cpp
  utils.cpp
)
target_compile_definitions(AssetStudio.FBXNative PRIVATE _AS_DLL FBXSDK_SHARED=0)
target_include_directories(AssetStudio.FBXNative PRIVATE "${FBXSDK_ROOT}/include")
target_compile_options(AssetStudio.FBXNative PRIVATE -Wno-deprecated-declarations)
# fbxredblacktree.h has a typo (mLefttChild) in an uninstantiated template; GCC 14+ rejects it by default
include(CheckCXXCompilerFlag)
check_cxx_compiler_flag(-Wno-template-body HAS_WNO_TEMPLATE_BODY)
if(HAS_WNO_TEMPLATE_BODY)
  target_compile_options(AssetStudio.FBXNative PRIVATE -Wno-template-body)
endif()
target_link_libraries(AssetStudio.FBXNative PRIVATE
  "${FBXSDK_LIB}" "${LIBXML2_STATIC}" "${ZLIB_STATIC}"
  pthread dl m
)
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
  target_link_options(AssetStudio.FBXNative PRIVATE
    -static-libstdc++ -static-libgcc
    -Wl,--exclude-libs,ALL   # don't re-export symbols of the static dependencies
    -Wl,--no-undefined
    -Wl,--gc-sections
  )
endif()
