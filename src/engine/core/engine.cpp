#define PDFVISOR_ENGINE_EXPORTS
#include "pdfvisor/pdfvisor_capi.h"

#include <fpdfview.h>
#include <fpdf_text.h>
#include <fpdf_doc.h>
#include <fpdf_edit.h>
#include <fpdf_ppo.h>
#include <fpdf_save.h>
#include <fpdf_annot.h>
#include <fpdf_signature.h>
#include <fpdf_formfill.h>
#include <fpdf_attachment.h>
#include <fpdf_flatten.h>
#include <fpdf_transformpage.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#endif

struct pv_document_t {
    FPDF_DOCUMENT raw = nullptr;
    std::unique_ptr<std::ifstream> stream;
    FPDF_FILEACCESS file_access{};
    FPDF_FORMFILLINFO form_info{};
    FPDF_FORMHANDLE form_handle = nullptr;
    int active_page_index = -1;
    FPDF_PAGE active_page = nullptr;
};

namespace {

std::mutex g_pdfium_mutex;
std::atomic<int> g_init_refcount{0};
thread_local std::string g_last_error;

void set_last_error(const std::string& msg) {
    g_last_error = msg;
}

void unpack_rgba(uint32_t rgba, unsigned int& r, unsigned int& g, unsigned int& b, unsigned int& a) {
    r = (rgba >> 24) & 0xFFu;
    g = (rgba >> 16) & 0xFFu;
    b = (rgba >> 8) & 0xFFu;
    a = rgba & 0xFFu;
}

std::string utf16_to_utf8(const std::vector<unsigned short>& utf16) {
    if (utf16.empty()) return {};
#ifdef _WIN32
    int len = static_cast<int>(utf16.size());
    while (len > 0 && utf16[len - 1] == 0) {
        --len;
    }
    if (len == 0) return {};
    int needed = WideCharToMultiByte(
        CP_UTF8, 0,
        reinterpret_cast<const wchar_t*>(utf16.data()), len,
        nullptr, 0, nullptr, nullptr);
    if (needed <= 0) return {};
    std::string out(static_cast<size_t>(needed), '\0');
    WideCharToMultiByte(
        CP_UTF8, 0,
        reinterpret_cast<const wchar_t*>(utf16.data()), len,
        out.data(), needed, nullptr, nullptr);
    return out;
#else
    return {};
#endif
}

std::vector<unsigned short> utf8_to_utf16(const char* utf8) {
    if (!utf8 || *utf8 == '\0') return {0};
#ifdef _WIN32
    int needed = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
    if (needed <= 0) return {0};
    std::vector<unsigned short> out(static_cast<size_t>(needed), 0);
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, reinterpret_cast<wchar_t*>(out.data()), needed);
    return out;
#else
    return {0};
#endif
}

std::filesystem::path utf8_to_path(const char* utf8) {
#ifdef _WIN32
    auto w = utf8_to_utf16(utf8);
    return std::filesystem::path(reinterpret_cast<const wchar_t*>(w.data()));
#else
    return std::filesystem::path(utf8 ? utf8 : "");
#endif
}

std::string format_fpdf_error(unsigned long err);

void ffi_noop_invalidate(FPDF_FORMFILLINFO*, FPDF_PAGE, double, double, double, double) {}
void ffi_noop_cursor(FPDF_FORMFILLINFO*, int) {}
int ffi_noop_set_timer(FPDF_FORMFILLINFO*, int, TimerCallback) { return 0; }
void ffi_noop_kill_timer(FPDF_FORMFILLINFO*, int) {}
FPDF_SYSTEMTIME ffi_get_local_time(FPDF_FORMFILLINFO*) { return FPDF_SYSTEMTIME{}; }
int ffi_get_rotation(FPDF_FORMFILLINFO*, FPDF_PAGE) { return 0; }
void ffi_noop_named_action(FPDF_FORMFILLINFO*, FPDF_BYTESTRING) {}

FPDF_PAGE ffi_get_page(FPDF_FORMFILLINFO* info, FPDF_DOCUMENT, int page_index) {
    // Compute enclosing pv_document_t from member pointer offset
    auto* base = reinterpret_cast<uint8_t*>(info) - offsetof(pv_document_t, form_info);
    auto* pv_doc = reinterpret_cast<pv_document_t*>(base);
    if (pv_doc && pv_doc->active_page && pv_doc->active_page_index == page_index) {
        return pv_doc->active_page;
    }
    return nullptr;
}

void init_doc_form_fill(pv_document_t* doc) {
    if (!doc || !doc->raw) return;
    doc->form_info = FPDF_FORMFILLINFO{};
    doc->form_info.version = 1;
    doc->form_info.FFI_Invalidate = &ffi_noop_invalidate;
    doc->form_info.FFI_SetCursor = &ffi_noop_cursor;
    doc->form_info.FFI_SetTimer = &ffi_noop_set_timer;
    doc->form_info.FFI_KillTimer = &ffi_noop_kill_timer;
    doc->form_info.FFI_GetLocalTime = &ffi_get_local_time;
    doc->form_info.FFI_GetPage = &ffi_get_page;
    doc->form_info.FFI_GetRotation = &ffi_get_rotation;
    doc->form_info.FFI_ExecuteNamedAction = &ffi_noop_named_action;

    doc->form_handle = FPDFDOC_InitFormFillEnvironment(doc->raw, &doc->form_info);
    if (doc->form_handle) {
        FPDF_RemoveFormFieldHighlight(doc->form_handle);
    }
}

int read_file_block_callback(void* param, unsigned long position, unsigned char* buf, unsigned long size) {
    auto* ifs = static_cast<std::ifstream*>(param);
    if (!ifs || !buf || size == 0) return 0;
    ifs->clear();
    ifs->seekg(static_cast<std::streamoff>(position), std::ios::beg);
    if (!ifs->good()) return 0;
    ifs->read(reinterpret_cast<char*>(buf), static_cast<std::streamsize>(size));
    return ifs->gcount() == static_cast<std::streamsize>(size) ? 1 : 0;
}

pv_status_t open_fpdf_from_utf8_path(
    const char* utf8_path,
    const char* utf8_password,
    std::unique_ptr<std::ifstream>& out_stream,
    FPDF_FILEACCESS& out_access,
    FPDF_DOCUMENT& out_raw_doc) {
    out_raw_doc = nullptr;
    auto fs_path = utf8_to_path(utf8_path);
    auto ifs = std::make_unique<std::ifstream>(fs_path, std::ios::in | std::ios::binary | std::ios::ate);
    if (!ifs->is_open()) {
        set_last_error("File not found or could not be opened");
        return PV_ERR_OPEN_FAILED;
    }

    auto end_pos = ifs->tellg();
    if (end_pos < 0 || static_cast<uint64_t>(end_pos) > static_cast<uint64_t>(std::numeric_limits<unsigned long>::max())) {
        // Fallback to FPDF_LoadDocument for >4GB files
        ifs.reset();
        out_raw_doc = FPDF_LoadDocument(utf8_path, utf8_password);
        if (!out_raw_doc) {
            unsigned long err = FPDF_GetLastError();
            set_last_error(format_fpdf_error(err));
            return (err == FPDF_ERR_PASSWORD) ? PV_ERR_PASSWORD_REQUIRED : PV_ERR_OPEN_FAILED;
        }
        return PV_OK;
    }

    ifs->seekg(0, std::ios::beg);
    out_access = FPDF_FILEACCESS{};
    out_access.m_FileLen = static_cast<unsigned long>(end_pos);
    out_access.m_GetBlock = &read_file_block_callback;
    out_access.m_Param = ifs.get();

    out_raw_doc = FPDF_LoadCustomDocument(&out_access, utf8_password);
    if (!out_raw_doc) {
        unsigned long err = FPDF_GetLastError();
        set_last_error(format_fpdf_error(err));
        return (err == FPDF_ERR_PASSWORD) ? PV_ERR_PASSWORD_REQUIRED : PV_ERR_OPEN_FAILED;
    }

    out_stream = std::move(ifs);
    return PV_OK;
}

struct FileWriteContext {
    FPDF_FILEWRITE base;
    std::ofstream* stream;
    bool ok;
};

int write_block_callback(FPDF_FILEWRITE* self, const void* data, unsigned long size) {
    auto* ctx = reinterpret_cast<FileWriteContext*>(self);
    if (!ctx || !ctx->stream || !ctx->ok) return 0;
    ctx->stream->write(static_cast<const char*>(data), static_cast<std::streamsize>(size));
    if (!ctx->stream->good()) {
        ctx->ok = false;
        return 0;
    }
    return 1;
}

pv_status_t save_raw_document_locked(FPDF_DOCUMENT raw_doc, const char* utf8_dest_path, int32_t incremental) {
    std::ofstream ofs(utf8_to_path(utf8_dest_path), std::ios::binary | std::ios::trunc);
    if (!ofs.is_open()) {
        set_last_error("Could not open destination file for writing");
        return PV_ERR_SAVE_FAILED;
    }

    FileWriteContext ctx{};
    ctx.base.version = 1;
    ctx.base.WriteBlock = &write_block_callback;
    ctx.stream = &ofs;
    ctx.ok = true;

    FPDF_DWORD flags = incremental ? FPDF_INCREMENTAL : FPDF_NO_INCREMENTAL;
    FPDF_BOOL saved = FPDF_SaveAsCopy(raw_doc, &ctx.base, flags);
    ofs.flush();
    ofs.close();

    if (!saved || !ctx.ok) {
        set_last_error("FPDF_SaveAsCopy failed");
        return PV_ERR_SAVE_FAILED;
    }
    return PV_OK;
}

std::string format_fpdf_error(unsigned long err) {
    switch (err) {
        case FPDF_ERR_SUCCESS: return "Success";
        case FPDF_ERR_FILE: return "File not found or could not be opened";
        case FPDF_ERR_FORMAT: return "File not in PDF format or corrupted";
        case FPDF_ERR_PASSWORD: return "Password required or incorrect password";
        case FPDF_ERR_SECURITY: return "Unsupported security scheme";
        case FPDF_ERR_PAGE: return "Page not found or content error";
        default: return "Unknown PDFium error (" + std::to_string(err) + ")";
    }
}

struct BookmarkEntry {
    int32_t level;
    int32_t page_index;
    std::string title;
};

void collect_bookmarks_recursive(
    FPDF_DOCUMENT doc,
    FPDF_BOOKMARK parent,
    int32_t level,
    std::vector<BookmarkEntry>& out,
    int32_t max_depth = 32) {
    if (level > max_depth) return;
    FPDF_BOOKMARK bm = FPDFBookmark_GetFirstChild(doc, parent);
    while (bm) {
        unsigned long bytes = FPDFBookmark_GetTitle(bm, nullptr, 0);
        std::string title;
        if (bytes > 0) {
            std::vector<unsigned short> buf((bytes / 2) + 1, 0);
            FPDFBookmark_GetTitle(bm, buf.data(), bytes);
            title = utf16_to_utf8(buf);
        }

        int32_t page_idx = -1;
        FPDF_DEST dest = FPDFBookmark_GetDest(doc, bm);
        if (!dest) {
            FPDF_ACTION action = FPDFBookmark_GetAction(bm);
            if (action) {
                dest = FPDFAction_GetDest(doc, action);
            }
        }
        if (dest) {
            page_idx = FPDFDest_GetDestPageIndex(doc, dest);
        }

        out.push_back(BookmarkEntry{level, page_idx, std::move(title)});
        collect_bookmarks_recursive(doc, bm, level + 1, out, max_depth);
        bm = FPDFBookmark_GetNextSibling(doc, bm);
    }
}

} // namespace

namespace pv_internal {
void set_last_error(const std::string& msg) {
    ::set_last_error(msg);
}
} // namespace pv_internal

extern "C" {

PV_API pv_status_t pv_engine_init(void) {
    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    if (g_init_refcount.fetch_add(1) == 0) {
        FPDF_LIBRARY_CONFIG config{};
        config.version = 2;
        config.m_pUserFontPaths = nullptr;
        config.m_pIsolate = nullptr;
        config.m_v8EmbedderSlot = 0;
        FPDF_InitLibraryWithConfig(&config);
    }
    set_last_error("");
    return PV_OK;
}

PV_API void pv_engine_shutdown(void) {
    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    int prev = g_init_refcount.load();
    if (prev <= 0) return;
    if (g_init_refcount.fetch_sub(1) == 1) {
        FPDF_DestroyLibrary();
    }
}

PV_API const char* pv_engine_version(void) {
    return "1.4.0";
}

PV_API const char* pv_get_last_error(void) {
    return g_last_error.c_str();
}

PV_API pv_status_t pv_doc_open(const char* utf8_path, const char* utf8_password, pv_document_handle* out_doc) {
    if (!utf8_path || !out_doc) {
        set_last_error("Invalid arguments to pv_doc_open");
        return PV_ERR_INVALID_ARG;
    }
    *out_doc = nullptr;

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    if (g_init_refcount.load() <= 0) {
        set_last_error("Engine not initialized");
        return PV_ERR_NOT_INITIALIZED;
    }

    auto* doc = new (std::nothrow) pv_document_t();
    if (!doc) {
        set_last_error("Out of memory allocating document handle");
        return PV_ERR_OUT_OF_MEMORY;
    }

    pv_status_t st = open_fpdf_from_utf8_path(utf8_path, utf8_password, doc->stream, doc->file_access, doc->raw);
    if (st != PV_OK) {
        delete doc;
        return st;
    }

    init_doc_form_fill(doc);
    *out_doc = doc;
    set_last_error("");
    return PV_OK;
}

PV_API pv_status_t pv_doc_create_blank(double width_pt, double height_pt, pv_document_handle* out_doc) {
    if (!out_doc || width_pt <= 0.0 || height_pt <= 0.0) {
        set_last_error("Invalid arguments to pv_doc_create_blank");
        return PV_ERR_INVALID_ARG;
    }
    *out_doc = nullptr;

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    if (g_init_refcount.load() <= 0) {
        set_last_error("Engine not initialized");
        return PV_ERR_NOT_INITIALIZED;
    }

    FPDF_DOCUMENT raw_doc = FPDF_CreateNewDocument();
    if (!raw_doc) {
        set_last_error("FPDF_CreateNewDocument failed");
        return PV_ERR_OUT_OF_MEMORY;
    }

    FPDF_PAGE page = FPDFPage_New(raw_doc, 0, width_pt, height_pt);
    if (!page) {
        FPDF_CloseDocument(raw_doc);
        set_last_error("FPDFPage_New failed");
        return PV_ERR_EDIT_FAILED;
    }
    FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);

    auto* doc = new (std::nothrow) pv_document_t();
    if (!doc) {
        FPDF_CloseDocument(raw_doc);
        return PV_ERR_OUT_OF_MEMORY;
    }
    doc->raw = raw_doc;
    init_doc_form_fill(doc);
    *out_doc = doc;
    return PV_OK;
}

PV_API pv_status_t pv_doc_append_blank_page(pv_document_handle doc, double width_pt, double height_pt, int32_t* out_page_index) {
    if (!doc || !doc->raw || width_pt <= 0.0 || height_pt <= 0.0) {
        set_last_error("Invalid arguments to pv_doc_append_blank_page");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    int idx = FPDF_GetPageCount(doc->raw);
    FPDF_PAGE page = FPDFPage_New(doc->raw, idx, width_pt, height_pt);
    if (!page) {
        set_last_error("FPDFPage_New failed");
        return PV_ERR_EDIT_FAILED;
    }
    FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);

    if (out_page_index) *out_page_index = idx;
    return PV_OK;
}

PV_API pv_status_t pv_doc_insert_blank_page(pv_document_handle doc, int32_t insert_at_index, double width_pt, double height_pt, int32_t* out_page_index) {
    if (!doc || !doc->raw || width_pt <= 0.0 || height_pt <= 0.0) {
        set_last_error("Invalid arguments to pv_doc_insert_blank_page");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    int count = FPDF_GetPageCount(doc->raw);
    int idx = std::clamp(insert_at_index, 0, count);
    FPDF_PAGE page = FPDFPage_New(doc->raw, idx, width_pt, height_pt);
    if (!page) {
        set_last_error("FPDFPage_New failed");
        return PV_ERR_EDIT_FAILED;
    }
    FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);

    if (out_page_index) *out_page_index = idx;
    return PV_OK;
}

PV_API pv_status_t pv_page_get_crop_box(pv_document_handle doc, int32_t page_index, pv_rect_t* out_box) {
    if (!doc || !doc->raw || !out_box || page_index < 0) {
        set_last_error("Invalid arguments to pv_page_get_crop_box");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    float l = 0, b = 0, r = 0, t = 0;
    if (!FPDFPage_GetCropBox(page, &l, &b, &r, &t)) {
        if (!FPDFPage_GetMediaBox(page, &l, &b, &r, &t)) {
            double w = 0, h = 0;
            FPDF_GetPageSizeByIndex(doc->raw, page_index, &w, &h);
            l = 0; b = 0; r = static_cast<float>(w); t = static_cast<float>(h);
        }
    }
    FPDF_ClosePage(page);
    out_box->left = l;
    out_box->bottom = b;
    out_box->right = r;
    out_box->top = t;
    return PV_OK;
}

PV_API pv_status_t pv_page_set_crop_box(pv_document_handle doc, int32_t page_index, const pv_rect_t* box) {
    if (!doc || !doc->raw || !box || page_index < 0) {
        set_last_error("Invalid arguments to pv_page_set_crop_box");
        return PV_ERR_INVALID_ARG;
    }

    float l = static_cast<float>(std::min(box->left, box->right));
    float r = static_cast<float>(std::max(box->left, box->right));
    float b = static_cast<float>(std::min(box->bottom, box->top));
    float t = static_cast<float>(std::max(box->bottom, box->top));
    if (r - l < 18.0f || t - b < 18.0f) {
        set_last_error("Crop box dimensions too small");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }
    FPDFPage_SetCropBox(page, l, b, r, t);
    FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);
    return PV_OK;
}

PV_API pv_status_t pv_doc_flatten_all(pv_document_handle doc) {
    if (!doc || !doc->raw) {
        set_last_error("Invalid arguments to pv_doc_flatten_all");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    if (doc->form_handle) {
        FPDFDOC_ExitFormFillEnvironment(doc->form_handle);
        doc->form_handle = nullptr;
    }

    int count = FPDF_GetPageCount(doc->raw);
    for (int i = 0; i < count; ++i) {
        FPDF_PAGE page = FPDF_LoadPage(doc->raw, i);
        if (!page) continue;
        int res = FPDFPage_Flatten(page, FLAT_NORMALDISPLAY);
        if (res == FLATTEN_SUCCESS) {
            FPDFPage_GenerateContent(page);
        }
        FPDF_ClosePage(page);
    }

    init_doc_form_fill(doc);
    return PV_OK;
}

PV_API void pv_doc_close(pv_document_handle doc) {
    if (!doc) return;
    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    if (doc->form_handle) {
        FPDFDOC_ExitFormFillEnvironment(doc->form_handle);
        doc->form_handle = nullptr;
    }
    if (doc->raw) {
        FPDF_CloseDocument(doc->raw);
        doc->raw = nullptr;
    }
    doc->stream.reset();
    delete doc;
}

PV_API int32_t pv_doc_get_page_count(pv_document_handle doc) {
    if (!doc || !doc->raw) return 0;
    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    return FPDF_GetPageCount(doc->raw);
}

PV_API pv_status_t pv_doc_get_page_size(pv_document_handle doc, int32_t page_index, pv_page_size_t* out_size) {
    if (!doc || !doc->raw || !out_size || page_index < 0) {
        set_last_error("Invalid arguments to pv_doc_get_page_size");
        return PV_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    double w = 0.0, h = 0.0;
    if (!FPDF_GetPageSizeByIndex(doc->raw, page_index, &w, &h)) {
        set_last_error("Invalid page index or failed to read page size");
        return PV_ERR_INVALID_PAGE;
    }
    out_size->width_pt = w;
    out_size->height_pt = h;
    return PV_OK;
}

PV_API pv_status_t pv_doc_save(pv_document_handle doc, const char* utf8_dest_path, int32_t incremental) {
    if (!doc || !doc->raw || !utf8_dest_path || *utf8_dest_path == '\0') {
        set_last_error("Invalid arguments to pv_doc_save");
        return PV_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    return save_raw_document_locked(doc->raw, utf8_dest_path, incremental);
}

PV_API pv_status_t pv_doc_get_page_rotation(pv_document_handle doc, int32_t page_index, int32_t* out_rotation) {
    if (!doc || !doc->raw || !out_rotation || page_index < 0) {
        set_last_error("Invalid arguments to pv_doc_get_page_rotation");
        return PV_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }
    int rot = FPDFPage_GetRotation(page);
    FPDF_ClosePage(page);
    if (rot < 0) {
        set_last_error("Failed to get page rotation");
        return PV_ERR_EDIT_FAILED;
    }
    *out_rotation = rot & 3;
    return PV_OK;
}

PV_API pv_status_t pv_doc_set_page_rotation(pv_document_handle doc, int32_t page_index, int32_t rotation) {
    if (!doc || !doc->raw || page_index < 0) {
        set_last_error("Invalid arguments to pv_doc_set_page_rotation");
        return PV_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }
    FPDFPage_SetRotation(page, ((rotation % 4) + 4) % 4);
    FPDF_ClosePage(page);
    return PV_OK;
}

PV_API pv_status_t pv_doc_delete_page(pv_document_handle doc, int32_t page_index) {
    if (!doc || !doc->raw || page_index < 0) {
        set_last_error("Invalid arguments to pv_doc_delete_page");
        return PV_ERR_INVALID_ARG;
    }
    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    int count = FPDF_GetPageCount(doc->raw);
    if (count <= 1) {
        set_last_error("Cannot delete the last remaining page of a PDF");
        return PV_ERR_EDIT_FAILED;
    }
    if (page_index >= count) {
        set_last_error("Page index out of range");
        return PV_ERR_INVALID_PAGE;
    }
    FPDFPage_Delete(doc->raw, page_index);
    return PV_OK;
}

PV_API pv_status_t pv_doc_move_page(pv_document_handle doc, int32_t from_index, int32_t to_index) {
    if (!doc || !doc->raw || from_index < 0 || to_index < 0) {
        set_last_error("Invalid arguments to pv_doc_move_page");
        return PV_ERR_INVALID_ARG;
    }
    if (from_index == to_index) return PV_OK;

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    int count = FPDF_GetPageCount(doc->raw);
    if (from_index >= count || to_index >= count) {
        set_last_error("Page index out of range for move");
        return PV_ERR_INVALID_PAGE;
    }
    int indices[1] = { from_index };
    // FPDF_MovePages dest_page_index is the index in the array AFTER removing the moved pages,
    // which for a single page equals to_index directly (0 .. count-1).
    if (!FPDF_MovePages(doc->raw, indices, 1, to_index)) {
        set_last_error("FPDF_MovePages failed");
        return PV_ERR_EDIT_FAILED;
    }
    return PV_OK;
}

PV_API pv_status_t pv_doc_import_pages(
    pv_document_handle dest_doc,
    const char* src_utf8_path,
    const char* src_utf8_password,
    int32_t insert_at_index,
    int32_t* out_imported_count) {
    if (!dest_doc || !dest_doc->raw || !src_utf8_path || *src_utf8_path == '\0') {
        set_last_error("Invalid arguments to pv_doc_import_pages");
        return PV_ERR_INVALID_ARG;
    }
    if (out_imported_count) *out_imported_count = 0;

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    std::unique_ptr<std::ifstream> src_stream;
    FPDF_FILEACCESS src_access{};
    FPDF_DOCUMENT src_doc = nullptr;
    pv_status_t open_st = open_fpdf_from_utf8_path(src_utf8_path, src_utf8_password, src_stream, src_access, src_doc);
    if (open_st != PV_OK) {
        return open_st;
    }

    int src_count = FPDF_GetPageCount(src_doc);
    int dest_count = FPDF_GetPageCount(dest_doc->raw);
    int target_idx = insert_at_index;
    if (target_idx < 0 || target_idx > dest_count) {
        target_idx = dest_count;
    }

    FPDF_BOOL ok = FPDF_ImportPagesByIndex(dest_doc->raw, src_doc, nullptr, 0, target_idx);
    FPDF_CloseDocument(src_doc);

    if (!ok) {
        set_last_error("FPDF_ImportPagesByIndex failed");
        return PV_ERR_EDIT_FAILED;
    }
    if (out_imported_count) *out_imported_count = src_count;
    return PV_OK;
}

PV_API pv_status_t pv_doc_extract_pages(
    pv_document_handle src_doc,
    const int32_t* page_indices,
    int32_t count,
    const char* dest_utf8_path) {
    if (!src_doc || !src_doc->raw || !page_indices || count <= 0 || !dest_utf8_path || *dest_utf8_path == '\0') {
        set_last_error("Invalid arguments to pv_doc_extract_pages");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_DOCUMENT new_doc = FPDF_CreateNewDocument();
    if (!new_doc) {
        set_last_error("Failed to create new PDF document");
        return PV_ERR_OUT_OF_MEMORY;
    }

    std::vector<int> idx_vec(page_indices, page_indices + count);
    if (!FPDF_ImportPagesByIndex(new_doc, src_doc->raw, idx_vec.data(), static_cast<unsigned long>(idx_vec.size()), 0)) {
        FPDF_CloseDocument(new_doc);
        set_last_error("Failed to import pages into extracted document");
        return PV_ERR_EDIT_FAILED;
    }

    pv_status_t st = save_raw_document_locked(new_doc, dest_utf8_path, 0);
    FPDF_CloseDocument(new_doc);
    return st;
}

PV_API int32_t pv_page_get_annot_count(pv_document_handle doc, int32_t page_index) {
    if (!doc || !doc->raw || page_index < 0) return 0;
    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) return 0;
    int count = FPDFPage_GetAnnotCount(page);
    FPDF_ClosePage(page);
    return count;
}

PV_API pv_status_t pv_page_add_highlight_annot(
    pv_document_handle doc,
    int32_t page_index,
    const pv_rect_t* rect,
    uint32_t rgba,
    int32_t* out_annot_index) {
    if (!doc || !doc->raw || !rect || page_index < 0) {
        set_last_error("Invalid arguments to pv_page_add_highlight_annot");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_ANNOTATION annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_HIGHLIGHT);
    if (!annot) {
        FPDF_ClosePage(page);
        set_last_error("Failed to create highlight annotation");
        return PV_ERR_EDIT_FAILED;
    }

    float left = static_cast<float>(std::min(rect->left, rect->right));
    float right = static_cast<float>(std::max(rect->left, rect->right));
    float bottom = static_cast<float>(std::min(rect->bottom, rect->top));
    float top = static_cast<float>(std::max(rect->bottom, rect->top));

    FS_RECTF fs_rect{left, top, right, bottom};
    FPDFAnnot_SetRect(annot, &fs_rect);

    FS_QUADPOINTSF quad{left, top, right, top, left, bottom, right, bottom};
    FPDFAnnot_AppendAttachmentPoints(annot, &quad);

    unsigned int r, g, b, a;
    unpack_rgba(rgba, r, g, b, a);
    FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, r, g, b, a);

    int idx = FPDFPage_GetAnnotCount(page) - 1;
    FPDFPage_CloseAnnot(annot);
    FPDF_ClosePage(page);

    if (out_annot_index) *out_annot_index = idx;
    return PV_OK;
}

PV_API pv_status_t pv_page_add_square_annot(
    pv_document_handle doc,
    int32_t page_index,
    const pv_rect_t* rect,
    uint32_t stroke_rgba,
    uint32_t fill_rgba,
    int32_t* out_annot_index) {
    if (!doc || !doc->raw || !rect || page_index < 0) {
        set_last_error("Invalid arguments to pv_page_add_square_annot");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_ANNOTATION annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_SQUARE);
    if (!annot) {
        FPDF_ClosePage(page);
        set_last_error("Failed to create square annotation");
        return PV_ERR_EDIT_FAILED;
    }

    float left = static_cast<float>(std::min(rect->left, rect->right));
    float right = static_cast<float>(std::max(rect->left, rect->right));
    float bottom = static_cast<float>(std::min(rect->bottom, rect->top));
    float top = static_cast<float>(std::max(rect->bottom, rect->top));

    FS_RECTF fs_rect{left, top, right, bottom};
    FPDFAnnot_SetRect(annot, &fs_rect);

    unsigned int sr, sg, sb, sa;
    unpack_rgba(stroke_rgba, sr, sg, sb, sa);
    FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, sr, sg, sb, sa);

    unsigned int fr, fg, fb, fa;
    unpack_rgba(fill_rgba, fr, fg, fb, fa);
    if (fa > 0) {
        FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_InteriorColor, fr, fg, fb, fa);
    }

    int idx = FPDFPage_GetAnnotCount(page) - 1;
    FPDFPage_CloseAnnot(annot);
    FPDF_ClosePage(page);

    if (out_annot_index) *out_annot_index = idx;
    return PV_OK;
}

PV_API pv_status_t pv_page_add_note_annot(
    pv_document_handle doc,
    int32_t page_index,
    const pv_rect_t* rect,
    const char* utf8_contents,
    uint32_t rgba,
    int32_t* out_annot_index) {
    if (!doc || !doc->raw || !rect || page_index < 0) {
        set_last_error("Invalid arguments to pv_page_add_note_annot");
        return PV_ERR_INVALID_ARG;
    }

    auto contents_utf16 = utf8_to_utf16(utf8_contents ? utf8_contents : "");

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_ANNOTATION annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_TEXT);
    if (!annot) {
        FPDF_ClosePage(page);
        set_last_error("Failed to create note annotation");
        return PV_ERR_EDIT_FAILED;
    }

    float left = static_cast<float>(std::min(rect->left, rect->right));
    float right = static_cast<float>(std::max(rect->left, rect->right));
    float bottom = static_cast<float>(std::min(rect->bottom, rect->top));
    float top = static_cast<float>(std::max(rect->bottom, rect->top));

    FS_RECTF fs_rect{left, top, right, bottom};
    FPDFAnnot_SetRect(annot, &fs_rect);

    unsigned int r, g, b, a;
    unpack_rgba(rgba, r, g, b, a);
    FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, r, g, b, a);
    FPDFAnnot_SetStringValue(annot, "Contents", contents_utf16.data());

    int idx = FPDFPage_GetAnnotCount(page) - 1;
    FPDFPage_CloseAnnot(annot);
    FPDF_ClosePage(page);

    if (out_annot_index) *out_annot_index = idx;
    return PV_OK;
}

PV_API pv_status_t pv_page_add_ink_annot(
    pv_document_handle doc,
    int32_t page_index,
    const pv_point_t* points,
    int32_t point_count,
    uint32_t rgba,
    int32_t* out_annot_index) {
    if (!doc || !doc->raw || !points || point_count < 2 || page_index < 0) {
        set_last_error("Invalid arguments to pv_page_add_ink_annot");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_ANNOTATION annot = FPDFPage_CreateAnnot(page, FPDF_ANNOT_INK);
    if (!annot) {
        FPDF_ClosePage(page);
        set_last_error("Failed to create ink annotation");
        return PV_ERR_EDIT_FAILED;
    }

    std::vector<FS_POINTF> fs_pts;
    fs_pts.reserve(static_cast<size_t>(point_count));
    float min_x = points[0].x, max_x = points[0].x;
    float min_y = points[0].y, max_y = points[0].y;
    for (int32_t i = 0; i < point_count; ++i) {
        fs_pts.push_back(FS_POINTF{points[i].x, points[i].y});
        min_x = std::min(min_x, points[i].x);
        max_x = std::max(max_x, points[i].x);
        min_y = std::min(min_y, points[i].y);
        max_y = std::max(max_y, points[i].y);
    }

    FS_RECTF bounds{min_x - 2.0f, max_y + 2.0f, max_x + 2.0f, min_y - 2.0f};
    FPDFAnnot_SetRect(annot, &bounds);
    FPDFAnnot_AddInkStroke(annot, fs_pts.data(), fs_pts.size());

    unsigned int r, g, b, a;
    unpack_rgba(rgba, r, g, b, a);
    FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, r, g, b, a);

    int idx = FPDFPage_GetAnnotCount(page) - 1;
    FPDFPage_CloseAnnot(annot);
    FPDF_ClosePage(page);

    if (out_annot_index) *out_annot_index = idx;
    return PV_OK;
}

PV_API pv_status_t pv_page_remove_annot(
    pv_document_handle doc,
    int32_t page_index,
    int32_t annot_index) {
    if (!doc || !doc->raw || page_index < 0 || annot_index < 0) {
        set_last_error("Invalid arguments to pv_page_remove_annot");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_BOOL ok = FPDFPage_RemoveAnnot(page, annot_index);
    FPDF_ClosePage(page);
    if (!ok) {
        set_last_error("Failed to remove annotation");
        return PV_ERR_EDIT_FAILED;
    }
    return PV_OK;
}

PV_API pv_status_t pv_page_get_annotations(
    pv_document_handle doc,
    int32_t page_index,
    pv_annot_info_t** out_annots,
    int32_t* out_count) {
    if (!doc || !doc->raw || page_index < 0 || !out_annots || !out_count) {
        set_last_error("Invalid arguments to pv_page_get_annotations");
        return PV_ERR_INVALID_ARG;
    }
    *out_annots = nullptr;
    *out_count = 0;

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    int count = FPDFPage_GetAnnotCount(page);
    if (count <= 0) {
        FPDF_ClosePage(page);
        return PV_OK;
    }

    auto* arr = static_cast<pv_annot_info_t*>(std::calloc(static_cast<size_t>(count), sizeof(pv_annot_info_t)));
    if (!arr) {
        FPDF_ClosePage(page);
        set_last_error("Out of memory allocating annotations");
        return PV_ERR_OUT_OF_MEMORY;
    }

    for (int i = 0; i < count; ++i) {
        arr[i].index = i;
        FPDF_ANNOTATION annot = FPDFPage_GetAnnot(page, i);
        if (!annot) continue;

        arr[i].subtype = FPDFAnnot_GetSubtype(annot);
        FS_RECTF r{};
        if (FPDFAnnot_GetRect(annot, &r)) {
            arr[i].rect = pv_rect_t{
                std::min(r.left, r.right),
                std::max(r.bottom, r.top),
                std::max(r.left, r.right),
                std::min(r.bottom, r.top)
            };
        }

        unsigned int cr = 255, cg = 215, cb = 0, ca = 255;
        if (FPDFAnnot_GetColor(annot, FPDFANNOT_COLORTYPE_Color, &cr, &cg, &cb, &ca)) {
            arr[i].color_rgba = ((cr & 0xFFu) << 24) | ((cg & 0xFFu) << 16) | ((cb & 0xFFu) << 8) | (ca & 0xFFu);
        } else {
            arr[i].color_rgba = 0xFFC107FFu;
        }

        unsigned long cbytes = FPDFAnnot_GetStringValue(annot, "Contents", nullptr, 0);
        if (cbytes > 2) {
            std::vector<unsigned short> wbuf((cbytes / 2) + 1, 0);
            FPDFAnnot_GetStringValue(annot, "Contents", wbuf.data(), cbytes);
            std::string utf8 = utf16_to_utf8(wbuf);
            arr[i].contents_utf8 = static_cast<char*>(std::malloc(utf8.size() + 1));
            if (arr[i].contents_utf8) std::memcpy(arr[i].contents_utf8, utf8.c_str(), utf8.size() + 1);
        }

        FPDFPage_CloseAnnot(annot);
    }

    FPDF_ClosePage(page);
    *out_annots = arr;
    *out_count = count;
    return PV_OK;
}

PV_API void pv_free_annotations(pv_annot_info_t* annots, int32_t count) {
    if (!annots) return;
    for (int32_t i = 0; i < count; ++i) {
        std::free(annots[i].contents_utf8);
    }
    std::free(annots);
}

PV_API pv_status_t pv_page_add_text_markup_annot(
    pv_document_handle doc,
    int32_t page_index,
    int32_t subtype,
    const pv_rect_t* quad_rects,
    int32_t rect_count,
    uint32_t rgba,
    int32_t* out_annot_index) {
    if (!doc || !doc->raw || page_index < 0 || !quad_rects || rect_count <= 0) {
        set_last_error("Invalid arguments to pv_page_add_text_markup_annot");
        return PV_ERR_INVALID_ARG;
    }
    if (subtype != FPDF_ANNOT_HIGHLIGHT && subtype != FPDF_ANNOT_UNDERLINE && subtype != FPDF_ANNOT_STRIKEOUT) {
        subtype = FPDF_ANNOT_HIGHLIGHT;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_ANNOTATION annot = FPDFPage_CreateAnnot(page, subtype);
    if (!annot) {
        FPDF_ClosePage(page);
        set_last_error("Failed to create text markup annotation");
        return PV_ERR_EDIT_FAILED;
    }

    float min_l = static_cast<float>(std::min(quad_rects[0].left, quad_rects[0].right));
    float max_r = static_cast<float>(std::max(quad_rects[0].left, quad_rects[0].right));
    float min_b = static_cast<float>(std::min(quad_rects[0].bottom, quad_rects[0].top));
    float max_t = static_cast<float>(std::max(quad_rects[0].bottom, quad_rects[0].top));

    for (int32_t i = 0; i < rect_count; ++i) {
        float l = static_cast<float>(std::min(quad_rects[i].left, quad_rects[i].right));
        float r = static_cast<float>(std::max(quad_rects[i].left, quad_rects[i].right));
        float b = static_cast<float>(std::min(quad_rects[i].bottom, quad_rects[i].top));
        float t = static_cast<float>(std::max(quad_rects[i].bottom, quad_rects[i].top));
        min_l = std::min(min_l, l);
        max_r = std::max(max_r, r);
        min_b = std::min(min_b, b);
        max_t = std::max(max_t, t);

        FS_QUADPOINTSF quad{l, t, r, t, l, b, r, b};
        FPDFAnnot_AppendAttachmentPoints(annot, &quad);
    }

    FS_RECTF bounds{min_l, max_t, max_r, min_b};
    FPDFAnnot_SetRect(annot, &bounds);

    unsigned int cr, cg, cb, ca;
    unpack_rgba(rgba, cr, cg, cb, ca);
    FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, cr, cg, cb, ca);

    int idx = FPDFPage_GetAnnotCount(page) - 1;
    FPDFPage_CloseAnnot(annot);
    FPDF_ClosePage(page);

    if (out_annot_index) *out_annot_index = idx;
    return PV_OK;
}

PV_API pv_status_t pv_page_update_annot(
    pv_document_handle doc,
    int32_t page_index,
    int32_t annot_index,
    const pv_rect_t* new_rect_or_null,
    const char* new_contents_or_null,
    int32_t update_color,
    uint32_t new_rgba) {
    if (!doc || !doc->raw || page_index < 0 || annot_index < 0) {
        set_last_error("Invalid arguments to pv_page_update_annot");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_ANNOTATION annot = FPDFPage_GetAnnot(page, annot_index);
    if (!annot) {
        FPDF_ClosePage(page);
        set_last_error("Invalid annotation index");
        return PV_ERR_EDIT_FAILED;
    }

    if (new_rect_or_null) {
        float l = static_cast<float>(std::min(new_rect_or_null->left, new_rect_or_null->right));
        float r = static_cast<float>(std::max(new_rect_or_null->left, new_rect_or_null->right));
        float b = static_cast<float>(std::min(new_rect_or_null->bottom, new_rect_or_null->top));
        float t = static_cast<float>(std::max(new_rect_or_null->bottom, new_rect_or_null->top));
        FS_RECTF fs_rect{l, t, r, b};
        FPDFAnnot_SetRect(annot, &fs_rect);
    }

    if (new_contents_or_null) {
        auto u16 = utf8_to_utf16(new_contents_or_null);
        FPDFAnnot_SetStringValue(annot, "Contents", u16.data());
    }

    if (update_color) {
        unsigned int cr, cg, cb, ca;
        unpack_rgba(new_rgba, cr, cg, cb, ca);
        FPDFAnnot_SetAP(annot, FPDF_ANNOT_APPEARANCEMODE_NORMAL, nullptr);
        FPDFAnnot_SetColor(annot, FPDFANNOT_COLORTYPE_Color, cr, cg, cb, ca);
    }

    FPDFPage_CloseAnnot(annot);
    FPDF_ClosePage(page);
    return PV_OK;
}

PV_API pv_status_t pv_page_render_bgra(
    pv_document_handle doc,
    int32_t page_index,
    const pv_render_options_t* options,
    uint8_t* out_bgra_buffer,
    int32_t stride_bytes) {
    if (!doc || !doc->raw || !options || !out_bgra_buffer ||
        options->width_px <= 0 || options->height_px <= 0 ||
        stride_bytes < options->width_px * 4) {
        set_last_error("Invalid arguments to pv_page_render_bgra");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Failed to load page " + std::to_string(page_index));
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_BITMAP bitmap = FPDFBitmap_CreateEx(
        options->width_px,
        options->height_px,
        FPDFBitmap_BGRA,
        out_bgra_buffer,
        stride_bytes);

    if (!bitmap) {
        FPDF_ClosePage(page);
        set_last_error("Failed to create PDFium bitmap wrapper");
        return PV_ERR_RENDER_FAILED;
    }

    FPDFBitmap_FillRect(bitmap, 0, 0, options->width_px, options->height_px, options->background_argb);

    int flags = 0;
    if (options->render_annotations) flags |= FPDF_ANNOT;
    if (options->lcd_text) flags |= FPDF_LCD_TEXT;

    FPDF_RenderPageBitmap(
        bitmap,
        page,
        0,
        0,
        options->width_px,
        options->height_px,
        options->rotation,
        flags);

    if (options->render_annotations && doc->form_handle) {
        doc->active_page_index = page_index;
        doc->active_page = page;
        FORM_OnAfterLoadPage(page, doc->form_handle);
        FPDF_FFLDraw(
            doc->form_handle,
            bitmap,
            page,
            0,
            0,
            options->width_px,
            options->height_px,
            options->rotation,
            flags);
        FORM_OnBeforeClosePage(page, doc->form_handle);
        doc->active_page = nullptr;
        doc->active_page_index = -1;
    }

    FPDFBitmap_Destroy(bitmap);
    FPDF_ClosePage(page);
    return PV_OK;
}

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
    int32_t stride_bytes) {
    if (!doc || !doc->raw || !out_bgra_buffer ||
        full_page_width_px <= 0 || full_page_height_px <= 0 ||
        tile_width_px <= 0 || tile_height_px <= 0 ||
        stride_bytes < tile_width_px * 4) {
        set_last_error("Invalid arguments to pv_page_render_tile_bgra");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Failed to load page " + std::to_string(page_index));
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_BITMAP bitmap = FPDFBitmap_CreateEx(
        tile_width_px,
        tile_height_px,
        FPDFBitmap_BGRA,
        out_bgra_buffer,
        stride_bytes);

    if (!bitmap) {
        FPDF_ClosePage(page);
        set_last_error("Failed to create PDFium bitmap wrapper for tile");
        return PV_ERR_RENDER_FAILED;
    }

    FPDFBitmap_FillRect(bitmap, 0, 0, tile_width_px, tile_height_px, background_argb);

    int flags = 0;
    if (render_annotations) flags |= FPDF_ANNOT;
    if (lcd_text) flags |= FPDF_LCD_TEXT;

    FPDF_RenderPageBitmap(
        bitmap,
        page,
        -tile_x,
        -tile_y,
        full_page_width_px,
        full_page_height_px,
        rotation,
        flags);

    if (render_annotations && doc->form_handle) {
        doc->active_page_index = page_index;
        doc->active_page = page;
        FORM_OnAfterLoadPage(page, doc->form_handle);
        FPDF_FFLDraw(
            doc->form_handle,
            bitmap,
            page,
            -tile_x,
            -tile_y,
            full_page_width_px,
            full_page_height_px,
            rotation,
            flags);
        FORM_OnBeforeClosePage(page, doc->form_handle);
        doc->active_page = nullptr;
        doc->active_page_index = -1;
    }

    FPDFBitmap_Destroy(bitmap);
    FPDF_ClosePage(page);
    return PV_OK;
}

PV_API pv_status_t pv_page_extract_text(
    pv_document_handle doc,
    int32_t page_index,
    char** out_utf8_text) {
    if (!doc || !doc->raw || !out_utf8_text) {
        set_last_error("Invalid arguments to pv_page_extract_text");
        return PV_ERR_INVALID_ARG;
    }
    *out_utf8_text = nullptr;

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Failed to load page for text extraction");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_TEXTPAGE text_page = FPDFText_LoadPage(page);
    if (!text_page) {
        FPDF_ClosePage(page);
        set_last_error("Failed to load text page");
        return PV_ERR_UNKNOWN;
    }

    int char_count = FPDFText_CountChars(text_page);
    std::string utf8;
    if (char_count > 0) {
        std::vector<unsigned short> buf(static_cast<size_t>(char_count) + 1, 0);
        int written = FPDFText_GetText(text_page, 0, char_count, buf.data());
        if (written > 0) {
            buf.resize(static_cast<size_t>(written));
            utf8 = utf16_to_utf8(buf);
        }
    }

    FPDFText_ClosePage(text_page);
    FPDF_ClosePage(page);

    char* result = static_cast<char*>(std::malloc(utf8.size() + 1));
    if (!result) {
        set_last_error("Out of memory allocating extracted text");
        return PV_ERR_OUT_OF_MEMORY;
    }
    std::memcpy(result, utf8.c_str(), utf8.size() + 1);
    *out_utf8_text = result;
    return PV_OK;
}

PV_API pv_status_t pv_page_search_text(
    pv_document_handle doc,
    int32_t page_index,
    const char* utf8_query,
    int32_t match_case,
    int32_t whole_word,
    pv_rect_t** out_rects,
    int32_t* out_count) {
    if (!doc || !doc->raw || !utf8_query || !out_rects || !out_count) {
        set_last_error("Invalid arguments to pv_page_search_text");
        return PV_ERR_INVALID_ARG;
    }
    *out_rects = nullptr;
    *out_count = 0;

    if (*utf8_query == '\0') {
        return PV_OK;
    }

    auto query_utf16 = utf8_to_utf16(utf8_query);

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Failed to load page for search");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_TEXTPAGE text_page = FPDFText_LoadPage(page);
    if (!text_page) {
        FPDF_ClosePage(page);
        set_last_error("Failed to load text page for search");
        return PV_ERR_UNKNOWN;
    }

    unsigned long flags = 0;
    if (match_case) flags |= FPDF_MATCHCASE;
    if (whole_word) flags |= FPDF_MATCHWHOLEWORD;

    std::vector<pv_rect_t> rects;
    FPDF_SCHHANDLE search = FPDFText_FindStart(text_page, query_utf16.data(), flags, 0);
    if (search) {
        while (FPDFText_FindNext(search)) {
            int start_idx = FPDFText_GetSchResultIndex(search);
            int count = FPDFText_GetSchCount(search);
            int rect_count = FPDFText_CountRects(text_page, start_idx, count);
            for (int r = 0; r < rect_count; ++r) {
                double left = 0, top = 0, right = 0, bottom = 0;
                if (FPDFText_GetRect(text_page, r, &left, &top, &right, &bottom)) {
                    rects.push_back(pv_rect_t{left, top, right, bottom});
                }
            }
        }
        FPDFText_FindClose(search);
    }

    FPDFText_ClosePage(text_page);
    FPDF_ClosePage(page);

    if (!rects.empty()) {
        auto* arr = static_cast<pv_rect_t*>(std::malloc(sizeof(pv_rect_t) * rects.size()));
        if (!arr) {
            set_last_error("Out of memory allocating search rects");
            return PV_ERR_OUT_OF_MEMORY;
        }
        std::memcpy(arr, rects.data(), sizeof(pv_rect_t) * rects.size());
        *out_rects = arr;
        *out_count = static_cast<int32_t>(rects.size());
    }

    return PV_OK;
}

PV_API pv_status_t pv_doc_get_bookmarks(
    pv_document_handle doc,
    pv_bookmark_t** out_bookmarks,
    int32_t* out_count) {
    if (!doc || !doc->raw || !out_bookmarks || !out_count) {
        set_last_error("Invalid arguments to pv_doc_get_bookmarks");
        return PV_ERR_INVALID_ARG;
    }
    *out_bookmarks = nullptr;
    *out_count = 0;

    std::vector<BookmarkEntry> entries;
    {
        std::lock_guard<std::mutex> lock(g_pdfium_mutex);
        collect_bookmarks_recursive(doc->raw, nullptr, 0, entries);
    }

    if (entries.empty()) {
        return PV_OK;
    }

    auto* arr = static_cast<pv_bookmark_t*>(std::calloc(entries.size(), sizeof(pv_bookmark_t)));
    if (!arr) {
        set_last_error("Out of memory allocating bookmarks");
        return PV_ERR_OUT_OF_MEMORY;
    }

    for (size_t i = 0; i < entries.size(); ++i) {
        arr[i].level = entries[i].level;
        arr[i].page_index = entries[i].page_index;
        arr[i].title_utf8 = static_cast<char*>(std::malloc(entries[i].title.size() + 1));
        if (arr[i].title_utf8) {
            std::memcpy(arr[i].title_utf8, entries[i].title.c_str(), entries[i].title.size() + 1);
        }
    }

    *out_bookmarks = arr;
    *out_count = static_cast<int32_t>(entries.size());
    return PV_OK;
}

PV_API void pv_free_string(char* str) {
    std::free(str);
}

PV_API void pv_free_rects(pv_rect_t* rects) {
    std::free(rects);
}

PV_API void pv_free_bookmarks(pv_bookmark_t* bookmarks, int32_t count) {
    if (!bookmarks) return;
    for (int32_t i = 0; i < count; ++i) {
        std::free(bookmarks[i].title_utf8);
    }
    std::free(bookmarks);
}

PV_API pv_status_t pv_doc_get_signatures(
    pv_document_handle doc,
    pv_signature_info_t** out_sigs,
    int32_t* out_count) {
    if (!doc || !doc->raw || !out_sigs || !out_count) {
        set_last_error("Invalid arguments to pv_doc_get_signatures");
        return PV_ERR_INVALID_ARG;
    }
    *out_sigs = nullptr;
    *out_count = 0;

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    int count = FPDF_GetSignatureCount(doc->raw);
    if (count <= 0) {
        return PV_OK;
    }

    auto* arr = static_cast<pv_signature_info_t*>(std::calloc(static_cast<size_t>(count), sizeof(pv_signature_info_t)));
    if (!arr) {
        set_last_error("Out of memory allocating signatures");
        return PV_ERR_OUT_OF_MEMORY;
    }

    for (int i = 0; i < count; ++i) {
        FPDF_SIGNATURE sig = FPDF_GetSignatureObject(doc->raw, i);
        if (!sig) continue;

        // SubFilter (ASCII)
        unsigned long sf_len = FPDFSignatureObj_GetSubFilter(sig, nullptr, 0);
        if (sf_len > 0) {
            std::string sf(sf_len, '\0');
            FPDFSignatureObj_GetSubFilter(sig, sf.data(), sf_len);
            arr[i].subfilter_utf8 = static_cast<char*>(std::malloc(sf.size() + 1));
            if (arr[i].subfilter_utf8) std::memcpy(arr[i].subfilter_utf8, sf.c_str(), sf.size() + 1);
        }

        // Reason (UTF-16LE)
        unsigned long r_bytes = FPDFSignatureObj_GetReason(sig, nullptr, 0);
        if (r_bytes > 0) {
            std::vector<unsigned short> buf((r_bytes / 2) + 1, 0);
            FPDFSignatureObj_GetReason(sig, buf.data(), r_bytes);
            std::string reason = utf16_to_utf8(buf);
            arr[i].reason_utf8 = static_cast<char*>(std::malloc(reason.size() + 1));
            if (arr[i].reason_utf8) std::memcpy(arr[i].reason_utf8, reason.c_str(), reason.size() + 1);
        }

        // Time (ASCII)
        unsigned long t_len = FPDFSignatureObj_GetTime(sig, nullptr, 0);
        if (t_len > 0) {
            std::string tm(t_len, '\0');
            FPDFSignatureObj_GetTime(sig, tm.data(), t_len);
            arr[i].time_utf8 = static_cast<char*>(std::malloc(tm.size() + 1));
            if (arr[i].time_utf8) std::memcpy(arr[i].time_utf8, tm.c_str(), tm.size() + 1);
        }

        // ByteRange (4 ints)
        unsigned long br_len = FPDFSignatureObj_GetByteRange(sig, nullptr, 0);
        if (br_len >= 4) {
            std::vector<int> br(br_len, 0);
            FPDFSignatureObj_GetByteRange(sig, br.data(), br_len);
            for (int k = 0; k < 4; ++k) arr[i].byte_range[k] = br[static_cast<size_t>(k)];
        }

        // DER Contents
        unsigned long c_len = FPDFSignatureObj_GetContents(sig, nullptr, 0);
        if (c_len > 0) {
            auto* der = static_cast<uint8_t*>(std::malloc(c_len));
            if (der) {
                FPDFSignatureObj_GetContents(sig, der, c_len);
                arr[i].contents_der = der;
                arr[i].contents_len = static_cast<int32_t>(c_len);
            }
        }
    }

    *out_sigs = arr;
    *out_count = count;
    return PV_OK;
}

PV_API void pv_free_signatures(pv_signature_info_t* sigs, int32_t count) {
    if (!sigs) return;
    for (int32_t i = 0; i < count; ++i) {
        std::free(sigs[i].subfilter_utf8);
        std::free(sigs[i].reason_utf8);
        std::free(sigs[i].time_utf8);
        std::free(sigs[i].contents_der);
    }
    std::free(sigs);
}

PV_API pv_status_t pv_page_get_objects(
    pv_document_handle doc,
    int32_t page_index,
    pv_page_object_info_t** out_objects,
    int32_t* out_count) {
    if (!doc || !doc->raw || page_index < 0 || !out_objects || !out_count) {
        set_last_error("Invalid arguments to pv_page_get_objects");
        return PV_ERR_INVALID_ARG;
    }
    *out_objects = nullptr;
    *out_count = 0;

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    int count = FPDFPage_CountObjects(page);
    if (count <= 0) {
        FPDF_ClosePage(page);
        return PV_OK;
    }

    FPDF_TEXTPAGE text_page = FPDFText_LoadPage(page);
    auto* arr = static_cast<pv_page_object_info_t*>(
        std::calloc(static_cast<size_t>(count), sizeof(pv_page_object_info_t)));
    if (!arr) {
        if (text_page) FPDFText_ClosePage(text_page);
        FPDF_ClosePage(page);
        set_last_error("Out of memory allocating page objects");
        return PV_ERR_OUT_OF_MEMORY;
    }

    for (int i = 0; i < count; ++i) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, i);
        arr[i].index = i;
        if (!obj) continue;

        int type = FPDFPageObj_GetType(obj);
        arr[i].type = type;

        float l = 0, b = 0, r = 0, t = 0;
        if (FPDFPageObj_GetBounds(obj, &l, &b, &r, &t)) {
            arr[i].bounds = pv_rect_t{l, t, r, b};
        }

        if (type == FPDF_PAGEOBJ_TEXT) {
            float fsize = 0.0f;
            if (FPDFTextObj_GetFontSize(obj, &fsize)) {
                arr[i].font_size = fsize;
            }
            if (text_page) {
                unsigned long bytes = FPDFTextObj_GetText(obj, text_page, nullptr, 0);
                if (bytes > 0) {
                    std::vector<unsigned short> buf((bytes / 2) + 1, 0);
                    FPDFTextObj_GetText(obj, text_page, buf.data(), bytes);
                    std::string utf8 = utf16_to_utf8(buf);
                    arr[i].text_utf8 = static_cast<char*>(std::malloc(utf8.size() + 1));
                    if (arr[i].text_utf8) {
                        std::memcpy(arr[i].text_utf8, utf8.c_str(), utf8.size() + 1);
                    }
                }
            }
        }
    }

    if (text_page) FPDFText_ClosePage(text_page);
    FPDF_ClosePage(page);

    *out_objects = arr;
    *out_count = count;
    return PV_OK;
}

PV_API pv_status_t pv_page_edit_text_object(
    pv_document_handle doc,
    int32_t page_index,
    int32_t obj_index,
    const char* new_utf8_text) {
    if (!doc || !doc->raw || page_index < 0 || obj_index < 0 || !new_utf8_text || *new_utf8_text == '\0') {
        set_last_error("Invalid arguments to pv_page_edit_text_object");
        return PV_ERR_INVALID_ARG;
    }

    auto utf16 = utf8_to_utf16(new_utf8_text);

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, obj_index);
    if (!obj || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_TEXT) {
        FPDF_ClosePage(page);
        set_last_error("Object index is not a text object");
        return PV_ERR_EDIT_FAILED;
    }

    FPDF_BOOL ok = FPDFText_SetText(obj, utf16.data());
    if (ok) {
        ok = FPDFPage_GenerateContent(page);
    }
    FPDF_ClosePage(page);

    if (!ok) {
        set_last_error("Failed to edit text object or regenerate content");
        return PV_ERR_EDIT_FAILED;
    }
    return PV_OK;
}

PV_API pv_status_t pv_page_move_object(
    pv_document_handle doc,
    int32_t page_index,
    int32_t obj_index,
    double dx_pt,
    double dy_pt) {
    if (!doc || !doc->raw || page_index < 0 || obj_index < 0) {
        set_last_error("Invalid arguments to pv_page_move_object");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, obj_index);
    if (!obj) {
        FPDF_ClosePage(page);
        set_last_error("Invalid object index");
        return PV_ERR_EDIT_FAILED;
    }

    FPDFPageObj_Transform(obj, 1.0, 0.0, 0.0, 1.0, dx_pt, dy_pt);
    FPDF_BOOL ok = FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);

    if (!ok) {
        set_last_error("Failed to regenerate content after moving object");
        return PV_ERR_EDIT_FAILED;
    }
    return PV_OK;
}

PV_API pv_status_t pv_page_set_image_bounds(
    pv_document_handle doc,
    int32_t page_index,
    int32_t obj_index,
    const pv_rect_t* new_rect) {
    if (!doc || !doc->raw || page_index < 0 || obj_index < 0 || !new_rect) {
        set_last_error("Invalid arguments to pv_page_set_image_bounds");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, obj_index);
    if (!obj || FPDFPageObj_GetType(obj) != FPDF_PAGEOBJ_IMAGE) {
        FPDF_ClosePage(page);
        set_last_error("Object is not an image");
        return PV_ERR_EDIT_FAILED;
    }

    double left = std::min(new_rect->left, new_rect->right);
    double right = std::max(new_rect->left, new_rect->right);
    double bottom = std::min(new_rect->bottom, new_rect->top);
    double top = std::max(new_rect->bottom, new_rect->top);
    double w = std::max(2.0, right - left);
    double h = std::max(2.0, top - bottom);

    FPDFImageObj_SetMatrix(obj, w, 0.0, 0.0, h, left, bottom);
    FPDF_BOOL ok = FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);
    if (!ok) {
        set_last_error("Failed to regenerate content after resizing image");
        return PV_ERR_EDIT_FAILED;
    }
    return PV_OK;
}

PV_API pv_status_t pv_doc_get_attachments(
    pv_document_handle doc,
    pv_attachment_info_t** out_attachments,
    int32_t* out_count) {
    if (!doc || !doc->raw || !out_attachments || !out_count) {
        set_last_error("Invalid arguments to pv_doc_get_attachments");
        return PV_ERR_INVALID_ARG;
    }
    *out_attachments = nullptr;
    *out_count = 0;

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    int count = FPDFDoc_GetAttachmentCount(doc->raw);
    if (count <= 0) return PV_OK;

    auto* arr = static_cast<pv_attachment_info_t*>(std::calloc(static_cast<size_t>(count), sizeof(pv_attachment_info_t)));
    if (!arr) {
        set_last_error("Out of memory allocating attachments");
        return PV_ERR_OUT_OF_MEMORY;
    }

    for (int i = 0; i < count; ++i) {
        arr[i].index = i;
        FPDF_ATTACHMENT att = FPDFDoc_GetAttachment(doc->raw, i);
        if (!att) continue;

        unsigned long nbytes = FPDFAttachment_GetName(att, nullptr, 0);
        if (nbytes > 0) {
            std::vector<unsigned short> wbuf((nbytes / 2) + 1, 0);
            FPDFAttachment_GetName(att, wbuf.data(), nbytes);
            std::string utf8 = utf16_to_utf8(wbuf);
            arr[i].name_utf8 = static_cast<char*>(std::malloc(utf8.size() + 1));
            if (arr[i].name_utf8) std::memcpy(arr[i].name_utf8, utf8.c_str(), utf8.size() + 1);
        }

        unsigned long fsize = 0;
        if (FPDFAttachment_GetFile(att, nullptr, 0, &fsize)) {
            arr[i].size_bytes = static_cast<int64_t>(fsize);
        }
    }

    *out_attachments = arr;
    *out_count = count;
    return PV_OK;
}

PV_API void pv_free_attachments(pv_attachment_info_t* attachments, int32_t count) {
    if (!attachments) return;
    for (int32_t i = 0; i < count; ++i) {
        std::free(attachments[i].name_utf8);
    }
    std::free(attachments);
}

PV_API pv_status_t pv_doc_add_attachment(
    pv_document_handle doc,
    const char* name_utf8,
    const uint8_t* data,
    int32_t data_len) {
    if (!doc || !doc->raw || !name_utf8 || *name_utf8 == '\0' || (!data && data_len > 0) || data_len < 0) {
        set_last_error("Invalid arguments to pv_doc_add_attachment");
        return PV_ERR_INVALID_ARG;
    }

    auto name_u16 = utf8_to_utf16(name_utf8);
    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_ATTACHMENT att = FPDFDoc_AddAttachment(doc->raw, name_u16.data());
    if (!att) {
        set_last_error("Failed to add embedded attachment (duplicate name or unsupported)");
        return PV_ERR_EDIT_FAILED;
    }
    if (!FPDFAttachment_SetFile(att, doc->raw, data, static_cast<unsigned long>(data_len))) {
        set_last_error("Failed to set attachment data");
        return PV_ERR_EDIT_FAILED;
    }
    return PV_OK;
}

PV_API pv_status_t pv_doc_extract_attachment(
    pv_document_handle doc,
    int32_t index,
    const char* dest_utf8_path) {
    if (!doc || !doc->raw || index < 0 || !dest_utf8_path || *dest_utf8_path == '\0') {
        set_last_error("Invalid arguments to pv_doc_extract_attachment");
        return PV_ERR_INVALID_ARG;
    }

    std::vector<uint8_t> buf;
    {
        std::lock_guard<std::mutex> lock(g_pdfium_mutex);
        FPDF_ATTACHMENT att = FPDFDoc_GetAttachment(doc->raw, index);
        if (!att) {
            set_last_error("Attachment index out of range");
            return PV_ERR_INVALID_ARG;
        }
        unsigned long out_len = 0;
        if (!FPDFAttachment_GetFile(att, nullptr, 0, &out_len)) {
            set_last_error("Failed to query attachment size");
            return PV_ERR_EDIT_FAILED;
        }
        buf.resize(out_len);
        if (out_len > 0 && !FPDFAttachment_GetFile(att, buf.data(), out_len, &out_len)) {
            set_last_error("Failed to read attachment payload");
            return PV_ERR_EDIT_FAILED;
        }
    }

    std::ofstream ofs(utf8_to_path(dest_utf8_path), std::ios::binary | std::ios::trunc);
    if (!ofs.is_open()) {
        set_last_error("Could not open destination file for attachment");
        return PV_ERR_SAVE_FAILED;
    }
    if (!buf.empty()) {
        ofs.write(reinterpret_cast<const char*>(buf.data()), static_cast<std::streamsize>(buf.size()));
    }
    ofs.close();
    return PV_OK;
}

PV_API pv_status_t pv_doc_delete_attachment(
    pv_document_handle doc,
    int32_t index) {
    if (!doc || !doc->raw || index < 0) {
        set_last_error("Invalid arguments to pv_doc_delete_attachment");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    if (!FPDFDoc_DeleteAttachment(doc->raw, index)) {
        set_last_error("Failed to delete attachment");
        return PV_ERR_EDIT_FAILED;
    }
    return PV_OK;
}

PV_API pv_status_t pv_page_remove_object(
    pv_document_handle doc,
    int32_t page_index,
    int32_t obj_index) {
    if (!doc || !doc->raw || page_index < 0 || obj_index < 0) {
        set_last_error("Invalid arguments to pv_page_remove_object");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, obj_index);
    if (!obj) {
        FPDF_ClosePage(page);
        set_last_error("Invalid object index");
        return PV_ERR_EDIT_FAILED;
    }

    FPDF_BOOL ok = FPDFPage_RemoveObject(page, obj);
    if (ok) {
        FPDFPageObj_Destroy(obj);
        ok = FPDFPage_GenerateContent(page);
    }
    FPDF_ClosePage(page);

    if (!ok) {
        set_last_error("Failed to remove object");
        return PV_ERR_EDIT_FAILED;
    }
    return PV_OK;
}

PV_API pv_status_t pv_page_add_text_object(
    pv_document_handle doc,
    int32_t page_index,
    const char* utf8_text,
    const char* font_name_ascii,
    float font_size,
    double x_pt,
    double y_pt,
    uint32_t rgba,
    int32_t* out_obj_index) {
    if (!doc || !doc->raw || page_index < 0 || !utf8_text || *utf8_text == '\0' || font_size <= 0.0f) {
        set_last_error("Invalid arguments to pv_page_add_text_object");
        return PV_ERR_INVALID_ARG;
    }

    auto utf16 = utf8_to_utf16(utf8_text);
    const char* font = (font_name_ascii && *font_name_ascii != '\0') ? font_name_ascii : "Helvetica";

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_PAGEOBJECT text_obj = FPDFPageObj_NewTextObj(doc->raw, font, font_size);
    if (!text_obj) {
        FPDF_ClosePage(page);
        set_last_error("Failed to create text object");
        return PV_ERR_EDIT_FAILED;
    }

    if (!FPDFText_SetText(text_obj, utf16.data())) {
        FPDFPageObj_Destroy(text_obj);
        FPDF_ClosePage(page);
        set_last_error("FPDFText_SetText failed");
        return PV_ERR_EDIT_FAILED;
    }

    unsigned int r, g, b, a;
    unpack_rgba(rgba, r, g, b, a);
    FPDFPageObj_SetFillColor(text_obj, r, g, b, a);
    FPDFPageObj_Transform(text_obj, 1.0, 0.0, 0.0, 1.0, x_pt, y_pt);

    FPDFPage_InsertObject(page, text_obj);
    int idx = FPDFPage_CountObjects(page) - 1;
    FPDF_BOOL ok = FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);

    if (!ok) {
        set_last_error("Failed to regenerate content after adding text");
        return PV_ERR_EDIT_FAILED;
    }
    if (out_obj_index) *out_obj_index = idx;
    return PV_OK;
}

PV_API pv_status_t pv_page_add_image_bgra(
    pv_document_handle doc,
    int32_t page_index,
    const uint8_t* bgra_pixels,
    int32_t img_width_px,
    int32_t img_height_px,
    const pv_rect_t* dest_rect,
    int32_t* out_obj_index) {
    if (!doc || !doc->raw || page_index < 0 || !bgra_pixels ||
        img_width_px <= 0 || img_height_px <= 0 || !dest_rect) {
        set_last_error("Invalid arguments to pv_page_add_image_bgra");
        return PV_ERR_INVALID_ARG;
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_BITMAP bitmap = FPDFBitmap_Create(img_width_px, img_height_px, 1);
    if (!bitmap) {
        FPDF_ClosePage(page);
        set_last_error("Failed to create PDFium bitmap for image");
        return PV_ERR_OUT_OF_MEMORY;
    }

    void* dst_buf = FPDFBitmap_GetBuffer(bitmap);
    int dst_stride = FPDFBitmap_GetStride(bitmap);
    int src_stride = img_width_px * 4;
    for (int y = 0; y < img_height_px; ++y) {
        std::memcpy(
            static_cast<uint8_t*>(dst_buf) + static_cast<size_t>(y) * dst_stride,
            bgra_pixels + static_cast<size_t>(y) * src_stride,
            static_cast<size_t>(src_stride));
    }

    FPDF_PAGEOBJECT img_obj = FPDFPageObj_NewImageObj(doc->raw);
    if (!img_obj) {
        FPDFBitmap_Destroy(bitmap);
        FPDF_ClosePage(page);
        set_last_error("Failed to create image object");
        return PV_ERR_EDIT_FAILED;
    }

    FPDF_BOOL ok = FPDFImageObj_SetBitmap(&page, 1, img_obj, bitmap);
    FPDFBitmap_Destroy(bitmap);

    if (!ok) {
        FPDFPageObj_Destroy(img_obj);
        FPDF_ClosePage(page);
        set_last_error("FPDFImageObj_SetBitmap failed");
        return PV_ERR_EDIT_FAILED;
    }

    double left = std::min(dest_rect->left, dest_rect->right);
    double right = std::max(dest_rect->left, dest_rect->right);
    double bottom = std::min(dest_rect->bottom, dest_rect->top);
    double top = std::max(dest_rect->bottom, dest_rect->top);
    double w = std::max(1.0, right - left);
    double h = std::max(1.0, top - bottom);

    FPDFImageObj_SetMatrix(img_obj, w, 0.0, 0.0, h, left, bottom);
    FPDFPage_InsertObject(page, img_obj);
    int idx = FPDFPage_CountObjects(page) - 1;
    ok = FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);

    if (!ok) {
        set_last_error("Failed to regenerate content after adding image");
        return PV_ERR_EDIT_FAILED;
    }
    if (out_obj_index) *out_obj_index = idx;
    return PV_OK;
}

PV_API void pv_free_page_objects(pv_page_object_info_t* objects, int32_t count) {
    if (!objects) return;
    for (int32_t i = 0; i < count; ++i) {
        std::free(objects[i].text_utf8);
    }
    std::free(objects);
}

namespace {

int redact_form_object_recursive(
    FPDF_PAGE page,
    FPDF_PAGEOBJECT form_obj,
    float r_left,
    float r_bottom,
    float r_right,
    float r_top,
    int depth = 0) {
    if (!form_obj || depth > 16) return 0;
    int sub_count = FPDFFormObj_CountObjects(form_obj);
    if (sub_count <= 0) return 0;

    std::vector<FPDF_PAGEOBJECT> sub_remove;
    int affected = 0;
    for (int i = 0; i < sub_count; ++i) {
        FPDF_PAGEOBJECT sub = FPDFFormObj_GetObject(form_obj, static_cast<unsigned long>(i));
        if (!sub) continue;
        float ol = 0, ob = 0, or_ = 0, ot = 0;
        if (!FPDFPageObj_GetBounds(sub, &ol, &ob, &or_, &ot)) continue;
        bool intersects = !(or_ < r_left || ol > r_right || ot < r_bottom || ob > r_top);
        if (!intersects) continue;

        int sub_type = FPDFPageObj_GetType(sub);
        if (sub_type == FPDF_PAGEOBJ_FORM) {
            bool fully_covered = (ol >= r_left && or_ <= r_right && ob >= r_bottom && ot <= r_top);
            if (fully_covered) {
                sub_remove.push_back(sub);
            } else {
                affected += redact_form_object_recursive(page, sub, r_left, r_bottom, r_right, r_top, depth + 1);
            }
        } else if (sub_type == FPDF_PAGEOBJ_IMAGE) {
            bool fully_covered = (ol >= r_left && or_ <= r_right && ob >= r_bottom && ot <= r_top);
            if (fully_covered) {
                sub_remove.push_back(sub);
            } else {
                FPDF_BITMAP img_bmp = FPDFImageObj_GetBitmap(sub);
                if (img_bmp) {
                    int bw = FPDFBitmap_GetWidth(img_bmp);
                    int bh = FPDFBitmap_GetHeight(img_bmp);
                    int stride = FPDFBitmap_GetStride(img_bmp);
                    int fmt = FPDFBitmap_GetFormat(img_bmp);
                    uint8_t* buf = static_cast<uint8_t*>(FPDFBitmap_GetBuffer(img_bmp));
                    float iw = std::max(0.001f, or_ - ol);
                    float ih = std::max(0.001f, ot - ob);
                    if (buf && bw > 0 && bh > 0 && (fmt == FPDFBitmap_BGRA || fmt == FPDFBitmap_BGRx || fmt == FPDFBitmap_BGR)) {
                        int bpp = (fmt == FPDFBitmap_BGR) ? 3 : 4;
                        int x0 = std::clamp(static_cast<int>(std::floor((std::max(r_left, ol) - ol) / iw * bw)), 0, bw);
                        int x1 = std::clamp(static_cast<int>(std::ceil((std::min(r_right, or_) - ol) / iw * bw)), 0, bw);
                        int y0 = std::clamp(static_cast<int>(std::floor((ot - std::min(r_top, ot)) / ih * bh)), 0, bh);
                        int y1 = std::clamp(static_cast<int>(std::ceil((ot - std::max(r_bottom, ob)) / ih * bh)), 0, bh);
                        for (int y = y0; y < y1; ++y) {
                            uint8_t* row = buf + static_cast<size_t>(y) * stride;
                            for (int x = x0; x < x1; ++x) {
                                row[x * bpp + 0] = 0;
                                row[x * bpp + 1] = 0;
                                row[x * bpp + 2] = 0;
                                if (bpp == 4) row[x * bpp + 3] = 255;
                            }
                        }
                        FPDFImageObj_SetBitmap(&page, 1, sub, img_bmp);
                        ++affected;
                    }
                    FPDFBitmap_Destroy(img_bmp);
                }
            }
        } else {
            // Text, vector paths (outlined fonts/logos), shadings
            sub_remove.push_back(sub);
        }
    }

    for (FPDF_PAGEOBJECT sub : sub_remove) {
        if (FPDFFormObj_RemoveObject(form_obj, sub)) {
            FPDFPageObj_Destroy(sub);
            ++affected;
        }
    }
    return affected;
}

} // namespace

PV_API pv_status_t pv_page_apply_redaction(
    pv_document_handle doc,
    int32_t page_index,
    const pv_rect_t* rect,
    uint32_t fill_rgba,
    int32_t* out_removed_objects) {
    if (!doc || !doc->raw || page_index < 0 || !rect) {
        set_last_error("Invalid arguments to pv_page_apply_redaction");
        return PV_ERR_INVALID_ARG;
    }
    if (out_removed_objects) *out_removed_objects = 0;

    float r_left = static_cast<float>(std::min(rect->left, rect->right));
    float r_right = static_cast<float>(std::max(rect->left, rect->right));
    float r_bottom = static_cast<float>(std::min(rect->bottom, rect->top));
    float r_top = static_cast<float>(std::max(rect->bottom, rect->top));

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    // 1. Physically remove intersecting text/path/shading objects, recurse into Form XObjects, and mask/remove images
    std::vector<FPDF_PAGEOBJECT> to_remove;
    int count = FPDFPage_CountObjects(page);
    int affected_extra = 0;
    for (int i = 0; i < count; ++i) {
        FPDF_PAGEOBJECT obj = FPDFPage_GetObject(page, i);
        if (!obj) continue;
        float ol = 0, ob = 0, or_ = 0, ot = 0;
        if (!FPDFPageObj_GetBounds(obj, &ol, &ob, &or_, &ot)) continue;

        bool intersects = !(or_ < r_left || ol > r_right || ot < r_bottom || ob > r_top);
        if (!intersects) continue;

        int type = FPDFPageObj_GetType(obj);
        if (type == FPDF_PAGEOBJ_FORM) {
            bool fully_covered = (ol >= r_left && or_ <= r_right && ob >= r_bottom && ot <= r_top);
            if (fully_covered) {
                to_remove.push_back(obj);
            } else {
                affected_extra += redact_form_object_recursive(page, obj, r_left, r_bottom, r_right, r_top, 0);
            }
        } else if (type == FPDF_PAGEOBJ_IMAGE) {
            bool fully_covered = (ol >= r_left && or_ <= r_right && ob >= r_bottom && ot <= r_top);
            if (fully_covered) {
                to_remove.push_back(obj);
            } else {
                // Surgical pixel-level masking inside the intersecting sub-rectangle of the image
                FPDF_BITMAP img_bmp = FPDFImageObj_GetBitmap(obj);
                if (img_bmp) {
                    int bw = FPDFBitmap_GetWidth(img_bmp);
                    int bh = FPDFBitmap_GetHeight(img_bmp);
                    int stride = FPDFBitmap_GetStride(img_bmp);
                    int fmt = FPDFBitmap_GetFormat(img_bmp);
                    uint8_t* buf = static_cast<uint8_t*>(FPDFBitmap_GetBuffer(img_bmp));
                    float iw = std::max(0.001f, or_ - ol);
                    float ih = std::max(0.001f, ot - ob);
                    if (buf && bw > 0 && bh > 0 && (fmt == FPDFBitmap_BGRA || fmt == FPDFBitmap_BGRx || fmt == FPDFBitmap_BGR)) {
                        int bpp = (fmt == FPDFBitmap_BGR) ? 3 : 4;
                        int x0 = std::clamp(static_cast<int>(std::floor((std::max(r_left, ol) - ol) / iw * bw)), 0, bw);
                        int x1 = std::clamp(static_cast<int>(std::ceil((std::min(r_right, or_) - ol) / iw * bw)), 0, bw);
                        // PDF Y is bottom-up, bitmap Y is top-down
                        int y0 = std::clamp(static_cast<int>(std::floor((ot - std::min(r_top, ot)) / ih * bh)), 0, bh);
                        int y1 = std::clamp(static_cast<int>(std::ceil((ot - std::max(r_bottom, ob)) / ih * bh)), 0, bh);
                        for (int y = y0; y < y1; ++y) {
                            uint8_t* row = buf + static_cast<size_t>(y) * stride;
                            for (int x = x0; x < x1; ++x) {
                                row[x * bpp + 0] = 0;
                                row[x * bpp + 1] = 0;
                                row[x * bpp + 2] = 0;
                                if (bpp == 4) row[x * bpp + 3] = 255;
                            }
                        }
                        FPDFImageObj_SetBitmap(&page, 1, obj, img_bmp);
                        ++affected_extra;
                    }
                    FPDFBitmap_Destroy(img_bmp);
                }
            }
        } else {
            // Text, vector paths (including outlined glyphs/curves), shadings
            to_remove.push_back(obj);
        }
    }

    int removed = affected_extra;
    for (FPDF_PAGEOBJECT obj : to_remove) {
        if (FPDFPage_RemoveObject(page, obj)) {
            FPDFPageObj_Destroy(obj);
            ++removed;
        }
    }

    // 1b. Also remove intersecting annotations (notes, highlights, links, form widgets, etc.)
    int annot_count = FPDFPage_GetAnnotCount(page);
    for (int a = annot_count - 1; a >= 0; --a) {
        FPDF_ANNOTATION annot = FPDFPage_GetAnnot(page, a);
        if (!annot) continue;
        FS_RECTF ar{};
        bool has_rect = FPDFAnnot_GetRect(annot, &ar) != 0;
        FPDFPage_CloseAnnot(annot);
        if (!has_rect) continue;
        float al = std::min(ar.left, ar.right);
        float aright = std::max(ar.left, ar.right);
        float ab = std::min(ar.bottom, ar.top);
        float at = std::max(ar.bottom, ar.top);
        bool intersects = !(aright < r_left || al > r_right || at < r_bottom || ab > r_top);
        if (intersects && FPDFPage_RemoveAnnot(page, a)) {
            ++removed;
        }
    }

    // 2. Insert opaque redaction bar over the redacted region
    FPDF_PAGEOBJECT bar = FPDFPageObj_CreateNewRect(r_left, r_bottom, r_right - r_left, r_top - r_bottom);
    if (bar) {
        unsigned int r, g, b, a;
        unpack_rgba(fill_rgba, r, g, b, a);
        FPDFPageObj_SetFillColor(bar, r, g, b, a);
        FPDFPath_SetDrawMode(bar, FPDF_FILLMODE_WINDING, 0);
        FPDFPage_InsertObject(page, bar);
    }

    FPDF_BOOL ok = FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);

    if (!ok) {
        set_last_error("Failed to regenerate content after redaction");
        return PV_ERR_EDIT_FAILED;
    }
    if (out_removed_objects) *out_removed_objects = removed;
    return PV_OK;
}

PV_API pv_status_t pv_page_add_invisible_text(
    pv_document_handle doc,
    int32_t page_index,
    const char* utf8_text,
    float font_size,
    double x_pt,
    double y_pt,
    int32_t* out_obj_index) {
    if (!doc || !doc->raw || page_index < 0 || !utf8_text || *utf8_text == '\0' || font_size <= 0.0f) {
        set_last_error("Invalid arguments to pv_page_add_invisible_text");
        return PV_ERR_INVALID_ARG;
    }

    auto utf16 = utf8_to_utf16(utf8_text);

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_PAGEOBJECT text_obj = FPDFPageObj_NewTextObj(doc->raw, "Helvetica", font_size);
    if (!text_obj) {
        FPDF_ClosePage(page);
        set_last_error("Failed to create invisible OCR text object");
        return PV_ERR_EDIT_FAILED;
    }

    if (!FPDFText_SetText(text_obj, utf16.data())) {
        FPDFPageObj_Destroy(text_obj);
        FPDF_ClosePage(page);
        set_last_error("FPDFText_SetText failed for OCR text");
        return PV_ERR_EDIT_FAILED;
    }

    // ISO 32000 Tr 3 = Invisible text rendering mode (searchable & copyable, not painted)
    FPDFTextObj_SetTextRenderMode(text_obj, FPDF_TEXTRENDERMODE_INVISIBLE);
    FPDFPageObj_Transform(text_obj, 1.0, 0.0, 0.0, 1.0, x_pt, y_pt);
    FPDFPage_InsertObject(page, text_obj);
    int idx = FPDFPage_CountObjects(page) - 1;
    FPDF_BOOL ok = FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);

    if (!ok) {
        set_last_error("Failed to regenerate content after adding OCR text");
        return PV_ERR_EDIT_FAILED;
    }
    if (out_obj_index) *out_obj_index = idx;
    return PV_OK;
}

PV_API pv_status_t pv_page_add_invisible_text_batch(
    pv_document_handle doc,
    int32_t page_index,
    const pv_ocr_word_t* words,
    int32_t word_count,
    const char* ttf_font_path_utf8,
    int32_t* out_first_obj_index,
    int32_t* out_added_count) {
    if (out_first_obj_index) *out_first_obj_index = -1;
    if (out_added_count) *out_added_count = 0;
    if (!doc || !doc->raw || page_index < 0 || (!words && word_count > 0) || word_count < 0) {
        set_last_error("Invalid arguments to pv_page_add_invisible_text_batch");
        return PV_ERR_INVALID_ARG;
    }
    if (word_count == 0) return PV_OK;

    std::vector<uint8_t> font_data;
    if (ttf_font_path_utf8 && *ttf_font_path_utf8 != '\0') {
        std::ifstream ff(utf8_to_path(ttf_font_path_utf8), std::ios::binary);
        if (ff.is_open()) {
            font_data.assign((std::istreambuf_iterator<char>(ff)), std::istreambuf_iterator<char>());
        }
    }

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_FONT unicode_font = nullptr;
    if (!font_data.empty()) {
        unicode_font = FPDFText_LoadFont(
            doc->raw,
            font_data.data(),
            static_cast<uint32_t>(font_data.size()),
            FPDF_FONT_TRUETYPE,
            1);
    }

    int first_idx = -1;
    int added = 0;
    for (int32_t i = 0; i < word_count; ++i) {
        const auto& w = words[i];
        if (!w.utf8_text || *w.utf8_text == '\0' || w.font_size <= 0.0f) continue;

        bool has_non_ascii = false;
        for (const unsigned char* p = reinterpret_cast<const unsigned char*>(w.utf8_text); *p; ++p) {
            if (*p > 127) { has_non_ascii = true; break; }
        }

        FPDF_PAGEOBJECT text_obj = (has_non_ascii && unicode_font)
            ? FPDFPageObj_CreateTextObj(doc->raw, unicode_font, w.font_size)
            : FPDFPageObj_NewTextObj(doc->raw, "Helvetica", w.font_size);
        if (!text_obj) continue;

        auto utf16 = utf8_to_utf16(w.utf8_text);
        if (!FPDFText_SetText(text_obj, utf16.data())) {
            FPDFPageObj_Destroy(text_obj);
            continue;
        }

        FPDFTextObj_SetTextRenderMode(text_obj, FPDF_TEXTRENDERMODE_INVISIBLE);
        FPDFPageObj_Transform(text_obj, 1.0, 0.0, 0.0, 1.0, w.x_pt, w.y_pt);
        FPDFPage_InsertObject(page, text_obj);
        int cur_idx = FPDFPage_CountObjects(page) - 1;
        if (first_idx < 0) first_idx = cur_idx;
        ++added;
    }

    if (unicode_font) {
        FPDFFont_Close(unicode_font);
    }

    FPDF_BOOL ok = (added == 0) ? 1 : FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);

    if (!ok) {
        set_last_error("Failed to regenerate content after batch OCR insertion");
        return PV_ERR_EDIT_FAILED;
    }
    if (out_first_obj_index) *out_first_obj_index = first_idx;
    if (out_added_count) *out_added_count = added;
    return PV_OK;
}

PV_API pv_status_t pv_page_get_link_at_point(
    pv_document_handle doc,
    int32_t page_index,
    double x_pt,
    double y_pt,
    pv_link_info_t* out_link) {
    if (!doc || !doc->raw || page_index < 0 || !out_link) {
        set_last_error("Invalid arguments to pv_page_get_link_at_point");
        return PV_ERR_INVALID_ARG;
    }
    out_link->found = 0;
    out_link->dest_page_index = -1;
    out_link->uri_utf8 = nullptr;

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_LINK link = FPDFLink_GetLinkAtPoint(page, x_pt, y_pt);
    if (!link) {
        FPDF_ClosePage(page);
        return PV_OK;
    }

    FPDF_DEST dest = FPDFLink_GetDest(doc->raw, link);
    if (dest) {
        int dst_page = FPDFDest_GetDestPageIndex(doc->raw, dest);
        if (dst_page >= 0) {
            out_link->found = 1;
            out_link->dest_page_index = dst_page;
            FPDF_ClosePage(page);
            return PV_OK;
        }
    }

    FPDF_ACTION action = FPDFLink_GetAction(link);
    if (action) {
        unsigned long act_type = FPDFAction_GetType(action);
        if (act_type == PDFACTION_GOTO) {
            FPDF_DEST act_dest = FPDFAction_GetDest(doc->raw, action);
            if (act_dest) {
                int dst_page = FPDFDest_GetDestPageIndex(doc->raw, act_dest);
                if (dst_page >= 0) {
                    out_link->found = 1;
                    out_link->dest_page_index = dst_page;
                }
            }
        } else if (act_type == PDFACTION_URI) {
            unsigned long uri_len = FPDFAction_GetURIPath(doc->raw, action, nullptr, 0);
            if (uri_len > 1) {
                auto* uri_buf = static_cast<char*>(std::malloc(uri_len));
                if (uri_buf) {
                    FPDFAction_GetURIPath(doc->raw, action, uri_buf, uri_len);
                    uri_buf[uri_len - 1] = '\0';
                    out_link->found = 1;
                    out_link->uri_utf8 = uri_buf;
                }
            }
        }
    }

    FPDF_ClosePage(page);
    return PV_OK;
}

PV_API pv_status_t pv_page_get_char_index_at_pos(
    pv_document_handle doc,
    int32_t page_index,
    double x_pt,
    double y_pt,
    double tolerance_pt,
    int32_t* out_char_index) {
    if (!doc || !doc->raw || page_index < 0 || !out_char_index) {
        set_last_error("Invalid arguments to pv_page_get_char_index_at_pos");
        return PV_ERR_INVALID_ARG;
    }
    *out_char_index = -1;

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }
    FPDF_TEXTPAGE tp = FPDFText_LoadPage(page);
    if (tp) {
        *out_char_index = FPDFText_GetCharIndexAtPos(tp, x_pt, y_pt, tolerance_pt, tolerance_pt);
        FPDFText_ClosePage(tp);
    }
    FPDF_ClosePage(page);
    return PV_OK;
}

PV_API pv_status_t pv_page_get_text_range(
    pv_document_handle doc,
    int32_t page_index,
    int32_t start_char,
    int32_t char_count,
    char** out_utf8_text) {
    if (!doc || !doc->raw || page_index < 0 || start_char < 0 || char_count <= 0 || !out_utf8_text) {
        set_last_error("Invalid arguments to pv_page_get_text_range");
        return PV_ERR_INVALID_ARG;
    }
    *out_utf8_text = nullptr;

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }
    FPDF_TEXTPAGE tp = FPDFText_LoadPage(page);
    if (!tp) {
        FPDF_ClosePage(page);
        return PV_ERR_UNKNOWN;
    }

    std::vector<unsigned short> buf(static_cast<size_t>(char_count) + 1, 0);
    int written = FPDFText_GetText(tp, start_char, char_count, buf.data());
    std::string utf8;
    if (written > 0) {
        buf.resize(static_cast<size_t>(written));
        utf8 = utf16_to_utf8(buf);
    }
    FPDFText_ClosePage(tp);
    FPDF_ClosePage(page);

    char* result = static_cast<char*>(std::malloc(utf8.size() + 1));
    if (!result) {
        set_last_error("Out of memory allocating text range");
        return PV_ERR_OUT_OF_MEMORY;
    }
    std::memcpy(result, utf8.c_str(), utf8.size() + 1);
    *out_utf8_text = result;
    return PV_OK;
}

PV_API pv_status_t pv_page_get_text_range_rects(
    pv_document_handle doc,
    int32_t page_index,
    int32_t start_char,
    int32_t char_count,
    pv_rect_t** out_rects,
    int32_t* out_count) {
    if (!doc || !doc->raw || page_index < 0 || start_char < 0 || char_count <= 0 || !out_rects || !out_count) {
        set_last_error("Invalid arguments to pv_page_get_text_range_rects");
        return PV_ERR_INVALID_ARG;
    }
    *out_rects = nullptr;
    *out_count = 0;

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }
    FPDF_TEXTPAGE tp = FPDFText_LoadPage(page);
    if (!tp) {
        FPDF_ClosePage(page);
        return PV_ERR_UNKNOWN;
    }

    int rect_count = FPDFText_CountRects(tp, start_char, char_count);
    std::vector<pv_rect_t> rects;
    rects.reserve(static_cast<size_t>(std::max(0, rect_count)));
    for (int i = 0; i < rect_count; ++i) {
        double l = 0, t = 0, r = 0, b = 0;
        if (FPDFText_GetRect(tp, i, &l, &t, &r, &b)) {
            rects.push_back(pv_rect_t{l, t, r, b});
        }
    }
    FPDFText_ClosePage(tp);
    FPDF_ClosePage(page);

    if (!rects.empty()) {
        auto* arr = static_cast<pv_rect_t*>(std::malloc(sizeof(pv_rect_t) * rects.size()));
        if (!arr) {
            set_last_error("Out of memory allocating selection rects");
            return PV_ERR_OUT_OF_MEMORY;
        }
        std::memcpy(arr, rects.data(), sizeof(pv_rect_t) * rects.size());
        *out_rects = arr;
        *out_count = static_cast<int32_t>(rects.size());
    }
    return PV_OK;
}

PV_API pv_status_t pv_page_add_unicode_text_object(
    pv_document_handle doc,
    int32_t page_index,
    const char* utf8_text,
    const char* ttf_font_path_utf8,
    float font_size,
    double x_pt,
    double y_pt,
    uint32_t rgba,
    int32_t* out_obj_index) {
    if (!doc || !doc->raw || page_index < 0 || !utf8_text || *utf8_text == '\0' ||
        !ttf_font_path_utf8 || *ttf_font_path_utf8 == '\0' || font_size <= 0.0f) {
        set_last_error("Invalid arguments to pv_page_add_unicode_text_object");
        return PV_ERR_INVALID_ARG;
    }

    std::ifstream font_file(utf8_to_path(ttf_font_path_utf8), std::ios::binary);
    if (!font_file.is_open()) {
        set_last_error("Could not open TrueType font file");
        return PV_ERR_OPEN_FAILED;
    }
    std::vector<uint8_t> font_data((std::istreambuf_iterator<char>(font_file)), std::istreambuf_iterator<char>());
    if (font_data.empty()) {
        set_last_error("TrueType font file is empty");
        return PV_ERR_OPEN_FAILED;
    }

    auto utf16 = utf8_to_utf16(utf8_text);

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_FONT font = FPDFText_LoadFont(
        doc->raw,
        font_data.data(),
        static_cast<uint32_t>(font_data.size()),
        FPDF_FONT_TRUETYPE,
        /*cid*/ 1);
    if (!font) {
        FPDF_ClosePage(page);
        set_last_error("FPDFText_LoadFont failed for TrueType font");
        return PV_ERR_EDIT_FAILED;
    }

    FPDF_PAGEOBJECT text_obj = FPDFPageObj_CreateTextObj(doc->raw, font, font_size);
    FPDFFont_Close(font);
    if (!text_obj) {
        FPDF_ClosePage(page);
        set_last_error("FPDFPageObj_CreateTextObj failed");
        return PV_ERR_EDIT_FAILED;
    }

    if (!FPDFText_SetText(text_obj, utf16.data())) {
        FPDFPageObj_Destroy(text_obj);
        FPDF_ClosePage(page);
        set_last_error("FPDFText_SetText failed for Unicode text");
        return PV_ERR_EDIT_FAILED;
    }

    unsigned int r, g, b, a;
    unpack_rgba(rgba, r, g, b, a);
    FPDFPageObj_SetFillColor(text_obj, r, g, b, a);
    FPDFPageObj_Transform(text_obj, 1.0, 0.0, 0.0, 1.0, x_pt, y_pt);
    FPDFPage_InsertObject(page, text_obj);
    int idx = FPDFPage_CountObjects(page) - 1;
    FPDF_BOOL ok = FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);

    if (!ok) {
        set_last_error("Failed to regenerate content after adding Unicode text");
        return PV_ERR_EDIT_FAILED;
    }
    if (out_obj_index) *out_obj_index = idx;
    return PV_OK;
}

PV_API pv_status_t pv_page_add_rotated_text(
    pv_document_handle doc,
    int32_t page_index,
    const char* utf8_text,
    float font_size,
    double x_pt,
    double y_pt,
    double angle_degrees,
    uint32_t rgba) {
    if (!doc || !doc->raw || page_index < 0 || !utf8_text || *utf8_text == '\0' || font_size <= 0.0f) {
        set_last_error("Invalid arguments to pv_page_add_rotated_text");
        return PV_ERR_INVALID_ARG;
    }

    auto utf16 = utf8_to_utf16(utf8_text);

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_PAGEOBJECT text_obj = FPDFPageObj_NewTextObj(doc->raw, "Helvetica-Bold", font_size);
    if (!text_obj) {
        FPDF_ClosePage(page);
        set_last_error("Failed to create watermark text object");
        return PV_ERR_EDIT_FAILED;
    }

    if (!FPDFText_SetText(text_obj, utf16.data())) {
        FPDFPageObj_Destroy(text_obj);
        FPDF_ClosePage(page);
        set_last_error("FPDFText_SetText failed for watermark");
        return PV_ERR_EDIT_FAILED;
    }

    unsigned int r, g, b, a;
    unpack_rgba(rgba, r, g, b, a);
    FPDFPageObj_SetFillColor(text_obj, r, g, b, a);

    double rad = angle_degrees * 3.14159265358979323846 / 180.0;
    double c = std::cos(rad);
    double s = std::sin(rad);
    FPDFPageObj_Transform(text_obj, c, s, -s, c, x_pt, y_pt);

    FPDFPage_InsertObject(page, text_obj);
    FPDF_BOOL ok = FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);

    if (!ok) {
        set_last_error("Failed to regenerate content after adding rotated text");
        return PV_ERR_EDIT_FAILED;
    }
    return PV_OK;
}

PV_API pv_status_t pv_page_add_rotated_unicode_text(
    pv_document_handle doc,
    int32_t page_index,
    const char* utf8_text,
    const char* ttf_font_path_utf8,
    float font_size,
    double x_pt,
    double y_pt,
    double angle_degrees,
    uint32_t rgba,
    int32_t* out_obj_index) {
    if (out_obj_index) *out_obj_index = -1;
    if (!doc || !doc->raw || page_index < 0 || !utf8_text || *utf8_text == '\0' || font_size <= 0.0f) {
        set_last_error("Invalid arguments to pv_page_add_rotated_unicode_text");
        return PV_ERR_INVALID_ARG;
    }

    std::vector<uint8_t> font_data;
    if (ttf_font_path_utf8 && *ttf_font_path_utf8 != '\0') {
        std::ifstream ff(utf8_to_path(ttf_font_path_utf8), std::ios::binary);
        if (ff.is_open()) {
            font_data.assign((std::istreambuf_iterator<char>(ff)), std::istreambuf_iterator<char>());
        }
    }

    auto utf16 = utf8_to_utf16(utf8_text);

    std::lock_guard<std::mutex> lock(g_pdfium_mutex);
    FPDF_PAGE page = FPDF_LoadPage(doc->raw, page_index);
    if (!page) {
        set_last_error("Invalid page index");
        return PV_ERR_INVALID_PAGE;
    }

    FPDF_FONT font = nullptr;
    if (!font_data.empty()) {
        font = FPDFText_LoadFont(
            doc->raw,
            font_data.data(),
            static_cast<uint32_t>(font_data.size()),
            FPDF_FONT_TRUETYPE,
            1);
    }

    FPDF_PAGEOBJECT text_obj = font
        ? FPDFPageObj_CreateTextObj(doc->raw, font, font_size)
        : FPDFPageObj_NewTextObj(doc->raw, "Helvetica-Bold", font_size);
    if (font) {
        FPDFFont_Close(font);
    }

    if (!text_obj) {
        FPDF_ClosePage(page);
        set_last_error("Failed to create rotated text object");
        return PV_ERR_EDIT_FAILED;
    }

    if (!FPDFText_SetText(text_obj, utf16.data())) {
        FPDFPageObj_Destroy(text_obj);
        FPDF_ClosePage(page);
        set_last_error("FPDFText_SetText failed for rotated Unicode text");
        return PV_ERR_EDIT_FAILED;
    }

    unsigned int r, g, b, a;
    unpack_rgba(rgba, r, g, b, a);
    FPDFPageObj_SetFillColor(text_obj, r, g, b, a);

    double rad = angle_degrees * 3.14159265358979323846 / 180.0;
    double c = std::cos(rad);
    double s = std::sin(rad);
    FPDFPageObj_Transform(text_obj, c, s, -s, c, x_pt, y_pt);

    FPDFPage_InsertObject(page, text_obj);
    int idx = FPDFPage_CountObjects(page) - 1;
    FPDF_BOOL ok = FPDFPage_GenerateContent(page);
    FPDF_ClosePage(page);

    if (!ok) {
        set_last_error("Failed to regenerate content after adding rotated Unicode text");
        return PV_ERR_EDIT_FAILED;
    }
    if (out_obj_index) *out_obj_index = idx;
    return PV_OK;
}

} // extern "C"
