cmake_minimum_required(VERSION 3.24)
project(PDFVisor VERSION 1.4.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(PDFium_DIR "${CMAKE_SOURCE_DIR}/third_party/pdfium" CACHE PATH "Path to PDFium precompiled binaries")
find_package(PDFium REQUIRED)

set(QPDF_ROOT "${CMAKE_SOURCE_DIR}/third_party/qpdf/qpdf-12.4.2-msvc64" CACHE PATH "Path to QPDF MSVC binaries")
if(NOT EXISTS "${QPDF_ROOT}/include/qpdf/QPDF.hh")
    message(FATAL_ERROR "QPDF not found at ${QPDF_ROOT}. Run scripts/fetch-qpdf.ps1 first.")
endif()

add_library(qpdf SHARED IMPORTED)
set_target_properties(qpdf PROPERTIES
    IMPORTED_LOCATION "${QPDF_ROOT}/bin/qpdf30.dll"
    IMPORTED_IMPLIB "${QPDF_ROOT}/lib/qpdf.lib"
    INTERFACE_INCLUDE_DIRECTORIES "${QPDF_ROOT}/include"
)

add_subdirectory(src/engine)
