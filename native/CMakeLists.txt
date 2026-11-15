# Copyright (c) 2026 Maël Kobura
# SPDX-License-Identifier: MIT
#
# Builds nat_vorbis, one shared library with libogg, libvorbis, libvorbisenc,
# and the C interface of nat_vorbis.c. The sources of libogg and libvorbis
# come from the submodules ogg/ and vorbis/, at their release tags.

cmake_minimum_required(VERSION 3.12)
project(nat_vorbis C)

set(OGG_DIR ${CMAKE_CURRENT_SOURCE_DIR}/ogg)
set(VORBIS_DIR ${CMAKE_CURRENT_SOURCE_DIR}/vorbis)

# libogg generates config_types.h; the fixed-width types of stdint.h fit every target compiler.
set(GENERATED ${CMAKE_CURRENT_BINARY_DIR}/include)
file(WRITE ${GENERATED}/ogg/config_types.h
"#ifndef __CONFIG_TYPES_H__
#define __CONFIG_TYPES_H__
#include <stdint.h>
typedef int16_t ogg_int16_t;
typedef uint16_t ogg_uint16_t;
typedef int32_t ogg_int32_t;
typedef uint32_t ogg_uint32_t;
typedef int64_t ogg_int64_t;
typedef uint64_t ogg_uint64_t;
#endif
")

set(VORBIS_SOURCES
    mdct.c smallft.c block.c envelope.c window.c lsp.c lpc.c analysis.c
    synthesis.c psy.c info.c floor1.c floor0.c res0.c mapping0.c registry.c
    codebook.c sharedbook.c lookup.c bitrate.c vorbisenc.c)
list(TRANSFORM VORBIS_SOURCES PREPEND ${VORBIS_DIR}/lib/)

add_library(nat_vorbis SHARED
    nat_vorbis.c
    ${OGG_DIR}/src/bitwise.c
    ${OGG_DIR}/src/framing.c
    ${VORBIS_SOURCES})

target_include_directories(nat_vorbis PRIVATE
    ${GENERATED}
    ${OGG_DIR}/include
    ${VORBIS_DIR}/include
    ${VORBIS_DIR}/lib)

if(MSVC)
    # The C runtime is linked statically: the library depends on nothing but the system.
    foreach(flags CMAKE_C_FLAGS_DEBUG CMAKE_C_FLAGS_RELEASE CMAKE_C_FLAGS_RELWITHDEBINFO CMAKE_C_FLAGS_MINSIZEREL)
        string(REPLACE "/MD" "/MT" ${flags} "${${flags}}")
    endforeach()
    target_compile_definitions(nat_vorbis PRIVATE _CRT_SECURE_NO_WARNINGS)
    target_compile_options(nat_vorbis PRIVATE /W3 /wd4244 /wd4267 /wd4305 /wd4996)
else()
    target_compile_options(nat_vorbis PRIVATE -O2 -fvisibility=hidden)
    target_link_libraries(nat_vorbis PRIVATE m)
endif()
