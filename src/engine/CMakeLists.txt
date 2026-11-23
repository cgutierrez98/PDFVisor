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

# Copy pdfium.dll and qpdf30.dll next to PDFVisor.Engine.dll after build
add_custom_command(TARGET PDFVisor.Engine POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:pdfium>
        $<TARGET_FILE_DIR:PDFVisor.Engine>
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        $<TARGET_FILE:qpdf>
        $<TARGET_FILE_DIR:PDFVisor.Engine>
    COMMENT "Copying pdfium.dll and qpdf30.dll to output directory"
)
