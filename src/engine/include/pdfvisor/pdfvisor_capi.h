#ifndef PDFVISOR_CAPI_H
#define PDFVISOR_CAPI_H

#include <stdint.h>
#include <stddef.h>

#ifdef _WIN32
  #ifdef PDFVISOR_ENGINE_EXPORTS
    #define PV_API __declspec(dllexport)
  #else
    #define PV_API __declspec(dllimport)
  #endif
#else
  #define PV_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct pv_document_t* pv_document_handle;

typedef enum pv_status_t {
    PV_OK = 0,
    PV_ERR_INVALID_ARG = 1,
    PV_ERR_NOT_INITIALIZED = 2,
    PV_ERR_OPEN_FAILED = 3,
    PV_ERR_PASSWORD_REQUIRED = 4,
    PV_ERR_INVALID_PAGE = 5,
    PV_ERR_RENDER_FAILED = 6,
    PV_ERR_OUT_OF_MEMORY = 7,
    PV_ERR_SAVE_FAILED = 8,
    PV_ERR_EDIT_FAILED = 9,
    PV_ERR_QPDF_FAILED = 10,
    PV_ERR_UNKNOWN = 99
} pv_status_t;

typedef struct pv_page_size_t {
    double width_pt;
    double height_pt;
} pv_page_size_t;

typedef struct pv_rect_t {
    double left;
    double top;
    double right;
    double bottom;
} pv_rect_t;

typedef struct pv_point_t {
    float x;
    float y;
} pv_point_t;

typedef struct pv_bookmark_t {
    int32_t level;
    int32_t page_index; /* -1 if bookmark has no page destination */
    char* title_utf8;
} pv_bookmark_t;

typedef struct pv_form_field_t {
    char* name_utf8;
    char* field_type_utf8; /* "/Tx", "/Btn", "/Ch", "/Sig" */
    char* value_utf8;
    int32_t is_checked;
} pv_form_field_t;

typedef struct pv_signature_info_t {
    char* subfilter_utf8;
    char* reason_utf8;
    char* time_utf8;
    int32_t byte_range[4];
    uint8_t* contents_der;
    int32_t contents_len;
} pv_signature_info_t;

typedef enum pv_page_obj_type_t {
    PV_OBJ_UNKNOWN = 0,
    PV_OBJ_TEXT = 1,
    PV_OBJ_PATH = 2,
    PV_OBJ_IMAGE = 3,
    PV_OBJ_SHADING = 4,
    PV_OBJ_FORM = 5
} pv_page_obj_type_t;

typedef struct pv_page_object_info_t {
    int32_t index;
    int32_t type;
    pv_rect_t bounds;
    float font_size;
    char* text_utf8;
} pv_page_object_info_t;

typedef struct pv_render_options_t {
    int32_t width_px;
    int32_t height_px;
    int32_t rotation;          /* 0, 1 (90), 2 (180), 3 (270) */
    uint32_t background_argb;  /* e.g. 0xFFFFFFFF for opaque white */
    int32_t render_annotations;/* 1 = render annotations, 0 = skip */
    int32_t lcd_text;          /* 1 = LCD optimized text */
} pv_render_options_t;

/* Engine lifecycle */
PV_API pv_status_t pv_engine_init(void);
PV_API void        pv_engine_shutdown(void);
PV_API const char* pv_engine_version(void);
PV_API const char* pv_get_last_error(void);

/* Document lifecycle */
PV_API pv_status_t pv_doc_open(const char* utf8_path, const char* utf8_password, pv_document_handle* out_doc);
PV_API void        pv_doc_close(pv_document_handle doc);
PV_API int32_t     pv_doc_get_page_count(pv_document_handle doc);
PV_API pv_status_t pv_doc_get_page_size(pv_document_handle doc, int32_t page_index, pv_page_size_t* out_size);
PV_API pv_status_t pv_doc_save(pv_document_handle doc, const char* utf8_dest_path, int32_t incremental);

/* Phase 2: Page organization */
PV_API pv_status_t pv_doc_get_page_rotation(pv_document_handle doc, int32_t page_index, int32_t* out_rotation);
PV_API pv_status_t pv_doc_set_page_rotation(pv_document_handle doc, int32_t page_index, int32_t rotation);
PV_API pv_status_t pv_doc_delete_page(pv_document_handle doc, int32_t page_index);
PV_API pv_status_t pv_doc_move_page(pv_document_handle doc, int32_t from_index, int32_t to_index);
PV_API pv_status_t pv_doc_import_pages(
    pv_document_handle dest_doc,
    const char* src_utf8_path,
    const char* src_utf8_password,
    int32_t insert_at_index,
    int32_t* out_imported_count);
PV_API pv_status_t pv_doc_extract_pages(
    pv_document_handle src_doc,
    const int32_t* page_indices,
    int32_t count,
    const char* dest_utf8_path);

/* Phase 2: ISO 32000 Annotations (coordinates in PDF points, bottom-left origin; colors packed 0xRRGGBBAA) */
PV_API int32_t     pv_page_get_annot_count(pv_document_handle doc, int32_t page_index);
PV_API pv_status_t pv_page_add_highlight_annot(
    pv_document_handle doc,
    int32_t page_index,
    const pv_rect_t* rect,
    uint32_t rgba,
    int32_t* out_annot_index);
PV_API pv_status_t pv_page_add_square_annot(
    pv_document_handle doc,
    int32_t page_index,
    const pv_rect_t* rect,
    uint32_t stroke_rgba,
    uint32_t fill_rgba,
    int32_t* out_annot_index);
PV_API pv_status_t pv_page_add_note_annot(
    pv_document_handle doc,
    int32_t page_index,
    const pv_rect_t* rect,
    const char* utf8_contents,
    uint32_t rgba,
    int32_t* out_annot_index);
PV_API pv_status_t pv_page_add_ink_annot(
    pv_document_handle doc,
    int32_t page_index,
    const pv_point_t* points,
    int32_t point_count,
    uint32_t rgba,
    int32_t* out_annot_index);
PV_API pv_status_t pv_page_remove_annot(
    pv_document_handle doc,
    int32_t page_index,
    int32_t annot_index);

/* Phase 2: QPDF Structural Optimization, Linearization & AES-256 Encryption */
PV_API pv_status_t pv_qpdf_optimize(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path,
    int32_t linearize,
    int32_t recompress_streams,
    int32_t object_streams);

PV_API pv_status_t pv_qpdf_encrypt_aes256(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path,
    const char* user_password,
    const char* owner_password,
    int32_t allow_print,
    int32_t allow_modify,
    int32_t allow_extract);

PV_API pv_status_t pv_qpdf_is_linearized(
    const char* utf8_path,
    const char* utf8_password,
    int32_t* out_is_linearized);

/* Phase 3: AcroForms & Digital Signatures (PAdES) */
PV_API pv_status_t pv_qpdf_get_form_fields(
    const char* utf8_path,
    const char* utf8_password,
    pv_form_field_t** out_fields,
    int32_t* out_count);

PV_API pv_status_t pv_qpdf_set_form_field_value(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path,
    const char* field_name_utf8,
    const char* value_utf8);

PV_API pv_status_t pv_qpdf_add_text_field(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path,
    int32_t page_index,
    const char* field_name_utf8,
    const pv_rect_t* rect,
    const char* default_value_utf8);

PV_API pv_status_t pv_qpdf_prepare_pades_signature(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path,
    int32_t page_index,
    const pv_rect_t* rect,
    const char* signer_label_utf8,
    const char* reason_utf8,
    const char* location_utf8,
    int32_t reserved_bytes);

PV_API pv_status_t pv_doc_get_signatures(
    pv_document_handle doc,
    pv_signature_info_t** out_sigs,
    int32_t* out_count);

PV_API void pv_free_form_fields(pv_form_field_t* fields, int32_t count);
PV_API void pv_free_signatures(pv_signature_info_t* sigs, int32_t count);

/* Phase 4: Direct Content Editing (Text, Fonts, Images & Object Transforms) */
PV_API pv_status_t pv_page_get_objects(
    pv_document_handle doc,
    int32_t page_index,
    pv_page_object_info_t** out_objects,
    int32_t* out_count);

PV_API pv_status_t pv_page_edit_text_object(
    pv_document_handle doc,
    int32_t page_index,
    int32_t obj_index,
    const char* new_utf8_text);

PV_API pv_status_t pv_page_move_object(
    pv_document_handle doc,
    int32_t page_index,
    int32_t obj_index,
    double dx_pt,
    double dy_pt);

PV_API pv_status_t pv_page_remove_object(
    pv_document_handle doc,
    int32_t page_index,
    int32_t obj_index);

PV_API pv_status_t pv_page_add_text_object(
    pv_document_handle doc,
    int32_t page_index,
    const char* utf8_text,
    const char* font_name_ascii,
    float font_size,
    double x_pt,
    double y_pt,
    uint32_t rgba,
    int32_t* out_obj_index);

PV_API pv_status_t pv_page_add_image_bgra(
    pv_document_handle doc,
    int32_t page_index,
    const uint8_t* bgra_pixels,
    int32_t img_width_px,
    int32_t img_height_px,
    const pv_rect_t* dest_rect,
    int32_t* out_obj_index);

PV_API void pv_free_page_objects(pv_page_object_info_t* objects, int32_t count);

/* Phase 5: Permanent Redaction, Metadata Sanitization & Invisible OCR Layer (Tr 3) */
PV_API pv_status_t pv_page_apply_redaction(
    pv_document_handle doc,
    int32_t page_index,
    const pv_rect_t* rect,
    uint32_t fill_rgba,
    int32_t* out_removed_objects);

PV_API pv_status_t pv_page_add_invisible_text(
    pv_document_handle doc,
    int32_t page_index,
    const char* utf8_text,
    float font_size,
    double x_pt,
    double y_pt,
    int32_t* out_obj_index);

PV_API pv_status_t pv_qpdf_sanitize_metadata(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path);

/* Blocks 1-2: Text selection hit-testing & TrueType Unicode text insertion */
PV_API pv_status_t pv_page_get_char_index_at_pos(
    pv_document_handle doc,
    int32_t page_index,
    double x_pt,
    double y_pt,
    double tolerance_pt,
    int32_t* out_char_index);

PV_API pv_status_t pv_page_get_text_range(
    pv_document_handle doc,
    int32_t page_index,
    int32_t start_char,
    int32_t char_count,
    char** out_utf8_text);

PV_API pv_status_t pv_page_get_text_range_rects(
    pv_document_handle doc,
    int32_t page_index,
    int32_t start_char,
    int32_t char_count,
    pv_rect_t** out_rects,
    int32_t* out_count);

PV_API pv_status_t pv_page_add_unicode_text_object(
    pv_document_handle doc,
    int32_t page_index,
    const char* utf8_text,
    const char* ttf_font_path_utf8,
    float font_size,
    double x_pt,
    double y_pt,
    uint32_t rgba,
    int32_t* out_obj_index);

/* Rendering (writes BGRA32 pixels into caller-allocated buffer of size >= stride_bytes * height_px) */
PV_API pv_status_t pv_page_render_bgra(
    pv_document_handle doc,
    int32_t page_index,
    const pv_render_options_t* options,
    uint8_t* out_bgra_buffer,
    int32_t stride_bytes);

/* Tile-based sub-rectangle rendering for high-zoom and large documents */
PV_API pv_status_t pv_page_render_tile_bgra(
    pv_document_handle doc,
    int32_t page_index,
    int32_t full_page_width_px,
    int32_t full_page_height_px,
    int32_t tile_x,
    int32_t tile_y,
    int32_t tile_width_px,
    int32_t tile_height_px,
    int32_t rotation,
    uint32_t background_argb,
    int32_t render_annotations,
    int32_t lcd_text,
    uint8_t* out_bgra_buffer,
    int32_t stride_bytes);

/* Text extraction (caller frees returned UTF-8 string with pv_free_string) */
PV_API pv_status_t pv_page_extract_text(
    pv_document_handle doc,
    int32_t page_index,
    char** out_utf8_text);

/* Text search returning PDF-space bounding rects (bottom-left origin, in points).
   Caller frees *out_rects with pv_free_rects. */
PV_API pv_status_t pv_page_search_text(
    pv_document_handle doc,
    int32_t page_index,
    const char* utf8_query,
    int32_t match_case,
    int32_t whole_word,
    pv_rect_t** out_rects,
    int32_t* out_count);

/* Flattened document outline/bookmarks (depth-first).
   Caller frees *out_bookmarks with pv_free_bookmarks. */
PV_API pv_status_t pv_doc_get_bookmarks(
    pv_document_handle doc,
    pv_bookmark_t** out_bookmarks,
    int32_t* out_count);

PV_API void pv_free_string(char* str);
PV_API void pv_free_rects(pv_rect_t* rects);
PV_API void pv_free_bookmarks(pv_bookmark_t* bookmarks, int32_t count);

#ifdef __cplusplus
}
#endif

#endif /* PDFVISOR_CAPI_H */
