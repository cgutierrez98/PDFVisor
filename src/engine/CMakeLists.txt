add_library(PDFVisor.Engine SHARED
    core/engine.cpp
    structure/qpdf_ops.cpp
)

target_include_directories(PDFVisor.Engine
    PUBLIC
        "${CMAKE_CURRENT_SOURCE_DIR}/include"
)

target_link_libraries(PDFVisor.Engine
    PRIVATE
        pdfium
        qpdf
)

if(MSVC)
    target_compile_options(PDFVisor.Engine PRIVATE /W4 /utf-8)
    target_compile_definitions(PDFVisor.Engine PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
endif()

# Copy pdfium.dll, qpdf30.dll and QPDF's bundled MSVC runtime DLLs next to PDFVisor.Engine.dll
file(GLOB QPDF_RUNTIME_DLLS "${QPDF_ROOT}/bin/*.dll")
add_custom_command(TARGET PDFVisor.Engine POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:pdfium>
        ${QPDF_RUNTIME_DLLS}
        $<TARGET_FILE_DIR:PDFVisor.Engine>
    COMMENT "Copying pdfium.dll, qpdf30.dll and runtime DLLs to output directory"
)
