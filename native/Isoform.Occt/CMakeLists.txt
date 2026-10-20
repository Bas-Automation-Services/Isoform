cmake_minimum_required(VERSION 3.21)
project(IsoformOcct LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(OCCT_LIBS TKernel TKMath TKG3d TKGeomBase TKBRep TKTopAlgo TKPrim TKBO TKBool TKFillet TKShHealing TKXSBase TKDESTEP)

add_library(isoform_occt SHARED src/isoform_occt.cpp)
target_include_directories(isoform_occt PRIVATE include)

# Either an installed OCCT (OpenCASCADE_DIR → OpenCASCADEConfig.cmake), or a plain OCCT build folder
# (OCCT_ROOT with inc/ and a <platform>/<compiler>/{lib,bin} layout, e.g. win64/vc14).
if(OCCT_ROOT)
    set(OCCT_BINARIES "win64/vc14" CACHE STRING "OCCT platform/compiler sub-folder under OCCT_ROOT")
    target_include_directories(isoform_occt PRIVATE ${OCCT_ROOT}/inc)
    foreach(lib ${OCCT_LIBS})
        target_link_libraries(isoform_occt PRIVATE ${OCCT_ROOT}/${OCCT_BINARIES}/lib/${lib}.lib)
    endforeach()
    file(GLOB OCCT_DLLS ${OCCT_ROOT}/${OCCT_BINARIES}/bin/TK*.dll)
    add_custom_command(TARGET isoform_occt POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different ${OCCT_DLLS} $<TARGET_FILE_DIR:isoform_occt>)
else()
    find_package(OpenCASCADE CONFIG REQUIRED)
    target_include_directories(isoform_occt PRIVATE ${OpenCASCADE_INCLUDE_DIR})
    target_link_libraries(isoform_occt PRIVATE ${OCCT_LIBS})
    add_custom_command(TARGET isoform_occt POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_RUNTIME_DLLS:isoform_occt> $<TARGET_FILE_DIR:isoform_occt>
        COMMAND_EXPAND_LISTS)
endif()

if(MSVC)
    target_compile_options(isoform_occt PRIVATE /W3 /EHsc /permissive-)
endif()
