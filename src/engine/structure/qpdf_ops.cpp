#define PDFVISOR_ENGINE_EXPORTS
#include "pdfvisor/pdfvisor_capi.h"

#include <qpdf/QPDF.hh>
#include <qpdf/QPDFWriter.hh>
#include <qpdf/QPDFPageDocumentHelper.hh>
#include <qpdf/QPDFAcroFormDocumentHelper.hh>
#include <qpdf/QPDFFormFieldObjectHelper.hh>
#include <qpdf/QPDFAnnotationObjectHelper.hh>
#include <qpdf/Constants.h>

#include <algorithm>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <exception>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <map>
#include <sstream>
#include <string>
#include <vector>

#ifdef _WIN32
#include <windows.h>
#endif

namespace pv_internal {
void set_last_error(const std::string& msg);
}

namespace {

char* dup_cstr(const std::string& s) {
    char* out = static_cast<char*>(std::malloc(s.size() + 1));
    if (out) {
        std::memcpy(out, s.c_str(), s.size() + 1);
    }
    return out;
}

std::string current_pdf_date_utc() {
    std::time_t now = std::time(nullptr);
    std::tm utc{};
#ifdef _WIN32
    gmtime_s(&utc, &now);
#else
    gmtime_r(&now, &utc);
#endif
    std::ostringstream oss;
    oss << "D:" << std::put_time(&utc, "%Y%m%d%H%M%SZ");
    return oss.str();
}

std::filesystem::path utf8_to_fs_path(const char* utf8) {
    if (!utf8 || *utf8 == '\0') return {};
#ifdef _WIN32
    int needed = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
    if (needed <= 0) return std::filesystem::path(utf8);
    std::wstring w(static_cast<size_t>(needed), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, w.data(), needed);
    if (!w.empty() && w.back() == L'\0') w.pop_back();
    return std::filesystem::path(w);
#else
    return std::filesystem::path(utf8);
#endif
}

// Encodes UTF-8 text into escaped WinAnsiEncoding (Windows-1252) literal string bytes for PDF Type1 fonts
std::string utf8_to_winansi_pdf_literal(const char* utf8) {
    if (!utf8 || *utf8 == '\0') return {};
    std::string raw_winansi;
#ifdef _WIN32
    int wlen = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
    if (wlen > 1) {
        std::wstring wstr(static_cast<size_t>(wlen - 1), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, utf8, -1, wstr.data(), wlen - 1);
        int alen = WideCharToMultiByte(1252, 0, wstr.data(), wlen - 1, nullptr, 0, "?", nullptr);
        if (alen > 0) {
            raw_winansi.resize(static_cast<size_t>(alen));
            WideCharToMultiByte(1252, 0, wstr.data(), wlen - 1, raw_winansi.data(), alen, "?", nullptr);
        }
    }
#else
    raw_winansi = utf8;
#endif
    std::ostringstream escaped;
    for (unsigned char ch : raw_winansi) {
        if (ch == '(' || ch == ')' || ch == '\\') {
            escaped << '\\' << static_cast<char>(ch);
        } else if (ch >= 32 && ch <= 126) {
            escaped << static_cast<char>(ch);
        } else if (ch >= 128) {
            escaped << '\\' << std::oct << std::setw(3) << std::setfill('0') << static_cast<int>(ch) << std::dec;
        }
    }
    return escaped.str();
}

bool patch_prepared_pades_file(const std::filesystem::path& dest_path, size_t hex_len) {
    std::fstream fs(dest_path, std::ios::in | std::ios::out | std::ios::binary);
    if (!fs.is_open()) {
        pv_internal::set_last_error("Could not reopen prepared PDF for ByteRange patching");
        return false;
    }
    std::string file_bytes((std::istreambuf_iterator<char>(fs)), std::istreambuf_iterator<char>());

    std::string hex_placeholder = "<" + std::string(hex_len, '0') + ">";
    size_t contents_pos = file_bytes.rfind(hex_placeholder);
    if (contents_pos == std::string::npos) {
        pv_internal::set_last_error("Failed to locate signature /Contents hex placeholder");
        return false;
    }

    size_t br_key = file_bytes.rfind("1111111111 2222222222 3333333333");
    if (br_key == std::string::npos) {
        pv_internal::set_last_error("Failed to locate signature /ByteRange placeholder");
        return false;
    }
    size_t br_open = file_bytes.rfind('[', br_key);
    size_t br_close = file_bytes.find(']', br_key);
    if (br_open == std::string::npos || br_close == std::string::npos || br_close <= br_open) {
        pv_internal::set_last_error("Malformed /ByteRange placeholder");
        return false;
    }

    size_t br_slot_len = br_close - br_open + 1;
    size_t off1 = contents_pos;
    size_t off2 = contents_pos + hex_placeholder.size();
    size_t len2 = file_bytes.size() - off2;

    std::ostringstream br_oss;
    br_oss << "[0 " << off1 << " " << off2 << " " << len2 << "]";
    std::string br_real = br_oss.str();
    if (br_real.size() > br_slot_len) {
        pv_internal::set_last_error("Computed /ByteRange exceeds reserved slot");
        return false;
    }
    br_real.insert(br_real.size() - 1, br_slot_len - br_real.size(), ' ');
    file_bytes.replace(br_open, br_slot_len, br_real);

    fs.clear();
    fs.seekp(0, std::ios::beg);
    fs.write(file_bytes.data(), static_cast<std::streamsize>(file_bytes.size()));
    fs.flush();
    fs.close();
    return true;
}

bool has_existing_signatures(QPDF& pdf) {
    auto root = pdf.getRoot();
    if (!root.hasKey("/AcroForm")) return false;
    auto af = root.getKey("/AcroForm");
    if (!af.isDictionary() || !af.hasKey("/Fields")) return false;
    auto fields = af.getKey("/Fields");
    if (!fields.isArray()) return false;
    for (auto item : QPDFObjectHandle::QPDFArrayItems(fields)) {
        if (item.isDictionary() &&
            item.hasKey("/FT") &&
            item.getKey("/FT").isName() &&
            item.getKey("/FT").getName() == "/Sig" &&
            item.hasKey("/V") &&
            !item.getKey("/V").isNull()) {
            return true;
        }
    }
    return false;
}

std::string serialize_dict_with_override(
    QPDFObjectHandle dict,
    const std::map<std::string, std::string>& raw_overrides) {
    std::ostringstream oss;
    oss << "<<";
    for (auto& kv : QPDFObjectHandle::QPDFDictItems(dict)) {
        if (raw_overrides.find(kv.first) != raw_overrides.end()) continue;
        oss << " " << kv.first << " " << kv.second.unparse();
    }
    for (const auto& ov : raw_overrides) {
        oss << " " << ov.first << " " << ov.second;
    }
    oss << " >>";
    return oss.str();
}

std::string serialize_array_with_appended_ref(QPDFObjectHandle arr, int new_obj_id) {
    std::ostringstream oss;
    oss << "[";
    if (arr.isArray()) {
        for (auto item : QPDFObjectHandle::QPDFArrayItems(arr)) {
            oss << " " << item.unparse();
        }
    }
    oss << " " << new_obj_id << " 0 R ]";
    return oss.str();
}

bool append_incremental_pades_revision(
    QPDF& pdf,
    const std::filesystem::path& src_path,
    const std::filesystem::path& dest_path,
    QPDFObjectHandle page_obj,
    const pv_rect_t* rect,
    const std::string& ap_stream_str,
    double w,
    double h,
    const char* reason_utf8,
    const char* location_utf8,
    size_t hex_len) {
    std::ifstream ifs(src_path, std::ios::binary);
    if (!ifs.is_open()) return false;
    std::string buf((std::istreambuf_iterator<char>(ifs)), std::istreambuf_iterator<char>());
    ifs.close();

    size_t sx_pos = buf.rfind("startxref");
    if (sx_pos == std::string::npos) return false;
    size_t num_start = buf.find_first_of("0123456789", sx_pos + 9);
    if (num_start == std::string::npos) return false;
    size_t num_end = buf.find_first_not_of("0123456789", num_start);
    size_t prev_xref = std::stoull(buf.substr(num_start, num_end - num_start));
    if (prev_xref >= buf.size()) return false;

    // Detect whether previous revision used classic "xref" table or PDF 1.5+ XRef Stream
    size_t probe = buf.find_first_not_of(" \t\r\n", prev_xref);
    bool prev_is_xref_table = (probe != std::string::npos && buf.compare(probe, 4, "xref") == 0);

    int max_id = 1;
    for (const auto& kv : pdf.getXRefTable()) {
        if (kv.first.getObj() > max_id) max_id = kv.first.getObj();
    }
    auto trailer = pdf.getTrailer();
    if (trailer.hasKey("/Size") && trailer.getKey("/Size").isInteger()) {
        max_id = std::max(max_id, static_cast<int>(trailer.getKey("/Size").getIntValue()) - 1);
    }

    int next_id = max_id + 1;
    int font_id = next_id++;
    int ap_id = next_id++;
    int sig_dict_id = next_id++;
    int sig_field_id = next_id++;

    auto root = pdf.getRoot();
    int root_id = root.getObjectID();
    int root_gen = root.getGeneration();
    int page_id = page_obj.getObjectID();
    int page_gen = page_obj.getGeneration();

    if (!buf.empty() && buf.back() != '\n' && buf.back() != '\r') {
        buf.push_back('\n');
    }

    struct WrittenObj {
        int id;
        int gen;
        size_t offset;
    };
    std::vector<WrittenObj> written;
    auto emit_obj = [&](int id, int gen, const std::string& body) {
        size_t off = buf.size();
        written.push_back({id, gen, off});
        std::ostringstream o;
        o << id << " " << gen << " obj\n" << body << "\nendobj\n";
        buf += o.str();
    };

    // 1. Font object (Helvetica with WinAnsiEncoding for Spanish/European accents)
    emit_obj(font_id, 0, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    // 2. Visible signature appearance Form XObject (/AP /N)
    {
        std::ostringstream ap_obj_body;
        ap_obj_body << "<< /Type /XObject /Subtype /Form /BBox [0 0 " << w << " " << h << "]"
                    << " /Resources << /Font << /F1 " << font_id << " 0 R >> >>"
                    << " /Length " << ap_stream_str.size() << " >>\nstream\n"
                    << ap_stream_str << "\nendstream";
        emit_obj(ap_id, 0, ap_obj_body.str());
    }

    // 3. /Sig dictionary with reserved <0000...> and /ByteRange sentinel
    {
        std::ostringstream sig_body;
        sig_body << "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /ETSI.CAdES.detached"
                 << " /ByteRange [ 0 1111111111 2222222222 3333333333 ]"
                 << " /Contents <" << std::string(hex_len, '0') << ">"
                 << " /M (" << current_pdf_date_utc() << ")";
        if (reason_utf8 && *reason_utf8 != '\0') {
            sig_body << " /Reason " << QPDFObjectHandle::newUnicodeString(reason_utf8).unparse();
        }
        if (location_utf8 && *location_utf8 != '\0') {
            sig_body << " /Location " << QPDFObjectHandle::newUnicodeString(location_utf8).unparse();
        }
        sig_body << " >>";
        emit_obj(sig_dict_id, 0, sig_body.str());
    }

    // 4. Signature Widget field
    int existing_field_count = 0;
    QPDFObjectHandle acroform = root.getKey("/AcroForm");
    if (acroform.isDictionary() && acroform.hasKey("/Fields") && acroform.getKey("/Fields").isArray()) {
        existing_field_count = static_cast<int>(acroform.getKey("/Fields").getArrayNItems());
    }
    {
        std::ostringstream fld_body;
        fld_body << "<< /Type /Annot /Subtype /Widget /FT /Sig"
                 << " /T (PDFVisorSignature_" << (existing_field_count + 1) << ")"
                 << " /V " << sig_dict_id << " 0 R"
                 << " /F 132"
                 << " /P " << page_id << " " << page_gen << " R"
                 << " /Rect [" << rect->left << " " << rect->bottom << " " << rect->right << " " << rect->top << "]"
                 << " /AP << /N " << ap_id << " 0 R >> >>";
        emit_obj(sig_field_id, 0, fld_body.str());
    }

    // 5. Update Page /Annots cleanly preserving indirect structure
    QPDFObjectHandle annots = page_obj.getKey("/Annots");
    if (annots.isIndirect() && annots.isArray()) {
        emit_obj(annots.getObjectID(), annots.getGeneration(), serialize_array_with_appended_ref(annots, sig_field_id));
    } else {
        std::map<std::string, std::string> page_ov;
        page_ov["/Annots"] = serialize_array_with_appended_ref(annots, sig_field_id);
        emit_obj(page_id, page_gen, serialize_dict_with_override(page_obj, page_ov));
    }

    // 6. Update /AcroForm and /Fields cleanly preserving indirect structure
    if (acroform.isIndirect() && acroform.isDictionary()) {
        QPDFObjectHandle fields = acroform.getKey("/Fields");
        std::map<std::string, std::string> af_ov;
        af_ov["/SigFlags"] = "3";
        if (fields.isIndirect() && fields.isArray()) {
            emit_obj(fields.getObjectID(), fields.getGeneration(), serialize_array_with_appended_ref(fields, sig_field_id));
        } else {
            af_ov["/Fields"] = serialize_array_with_appended_ref(fields, sig_field_id);
        }
        emit_obj(acroform.getObjectID(), acroform.getGeneration(), serialize_dict_with_override(acroform, af_ov));
    } else {
        std::string fields_raw = acroform.isDictionary()
            ? serialize_array_with_appended_ref(acroform.getKey("/Fields"), sig_field_id)
            : ("[ " + std::to_string(sig_field_id) + " 0 R ]");
        std::map<std::string, std::string> af_ov;
        af_ov["/SigFlags"] = "3";
        af_ov["/Fields"] = fields_raw;
        std::string af_dict_raw = acroform.isDictionary()
            ? serialize_dict_with_override(acroform, af_ov)
            : ("<< /SigFlags 3 /Fields " + fields_raw + " >>");
        std::map<std::string, std::string> root_ov;
        root_ov["/AcroForm"] = af_dict_raw;
        emit_obj(root_id, root_gen, serialize_dict_with_override(root, root_ov));
    }

    // 7. Write cross-reference update matching original format (xref table vs XRef stream)
    size_t xref_offset = 0;
    if (prev_is_xref_table) {
        std::sort(written.begin(), written.end(), [](const WrittenObj& a, const WrittenObj& b) { return a.id < b.id; });
        xref_offset = buf.size();
        std::ostringstream xr;
        xr << "xref\n";
        size_t idx = 0;
        while (idx < written.size()) {
            size_t run_end = idx + 1;
            while (run_end < written.size() && written[run_end].id == written[run_end - 1].id + 1) {
                ++run_end;
            }
            xr << written[idx].id << " " << (run_end - idx) << "\n";
            for (size_t k = idx; k < run_end; ++k) {
                xr << std::setw(10) << std::setfill('0') << written[k].offset << " "
                   << std::setw(5) << std::setfill('0') << written[k].gen << " n \n";
            }
            idx = run_end;
        }
        xr << "trailer\n<< /Size " << next_id
           << " /Root " << root_id << " " << root_gen << " R"
           << " /Prev " << prev_xref;
        if (trailer.hasKey("/Info")) xr << " /Info " << trailer.getKey("/Info").unparse();
        if (trailer.hasKey("/ID")) xr << " /ID " << trailer.getKey("/ID").unparse();
        xr << " >>\nstartxref\n" << xref_offset << "\n%%EOF\n";
        buf += xr.str();
    } else {
        int xref_obj_id = next_id++;
        xref_offset = buf.size();
        written.push_back({xref_obj_id, 0, xref_offset});
        std::sort(written.begin(), written.end(), [](const WrittenObj& a, const WrittenObj& b) { return a.id < b.id; });

        std::ostringstream index_arr;
        index_arr << "[";
        std::string stream_bytes;
        size_t idx = 0;
        while (idx < written.size()) {
            size_t run_end = idx + 1;
            while (run_end < written.size() && written[run_end].id == written[run_end - 1].id + 1) {
                ++run_end;
            }
            index_arr << " " << written[idx].id << " " << (run_end - idx);
            for (size_t k = idx; k < run_end; ++k) {
                uint64_t off64 = static_cast<uint64_t>(written[k].offset);
                uint16_t gen16 = static_cast<uint16_t>(written[k].gen);
                stream_bytes.push_back(static_cast<char>(1)); // type 1 = uncompressed object
                stream_bytes.push_back(static_cast<char>((off64 >> 24) & 0xFF));
                stream_bytes.push_back(static_cast<char>((off64 >> 16) & 0xFF));
                stream_bytes.push_back(static_cast<char>((off64 >> 8) & 0xFF));
                stream_bytes.push_back(static_cast<char>(off64 & 0xFF));
                stream_bytes.push_back(static_cast<char>((gen16 >> 8) & 0xFF));
                stream_bytes.push_back(static_cast<char>(gen16 & 0xFF));
            }
            idx = run_end;
        }
        index_arr << " ]";

        std::ostringstream xr_obj;
        xr_obj << xref_obj_id << " 0 obj\n"
               << "<< /Type /XRef /Size " << next_id
               << " /W [1 4 2]"
               << " /Index " << index_arr.str()
               << " /Root " << root_id << " " << root_gen << " R"
               << " /Prev " << prev_xref;
        if (trailer.hasKey("/Info")) xr_obj << " /Info " << trailer.getKey("/Info").unparse();
        if (trailer.hasKey("/ID")) xr_obj << " /ID " << trailer.getKey("/ID").unparse();
        xr_obj << " /Length " << stream_bytes.size() << " >>\nstream\n";
        buf += xr_obj.str();
        buf.append(stream_bytes.data(), stream_bytes.size());
        buf += "\nendstream\nendobj\nstartxref\n" + std::to_string(xref_offset) + "\n%%EOF\n";
    }

    std::ofstream ofs(dest_path, std::ios::binary | std::ios::trunc);
    if (!ofs.is_open()) return false;
    ofs.write(buf.data(), static_cast<std::streamsize>(buf.size()));
    ofs.flush();
    ofs.close();

    return patch_prepared_pades_file(dest_path, hex_len);
}

} // namespace

extern "C" {

PV_API pv_status_t pv_qpdf_optimize(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path,
    int32_t linearize,
    int32_t recompress_streams,
    int32_t object_streams) {
    if (!src_utf8_path || *src_utf8_path == '\0' || !dest_utf8_path || *dest_utf8_path == '\0') {
        pv_internal::set_last_error("Invalid arguments to pv_qpdf_optimize");
        return PV_ERR_INVALID_ARG;
    }

    try {
        QPDF pdf;
        pdf.processFile(src_utf8_path, src_utf8_password ? src_utf8_password : "");

        QPDFWriter writer(pdf, dest_utf8_path);
        writer.setCompressStreams(true);
        writer.setRecompressFlate(recompress_streams != 0);
        writer.setObjectStreamMode(object_streams ? qpdf_o_generate : qpdf_o_preserve);
        writer.setLinearization(linearize != 0);
        writer.write();

        pv_internal::set_last_error("");
        return PV_OK;
    } catch (const std::exception& ex) {
        pv_internal::set_last_error(std::string("QPDF optimize failed: ") + ex.what());
        return PV_ERR_QPDF_FAILED;
    } catch (...) {
        pv_internal::set_last_error("QPDF optimize failed with unknown exception");
        return PV_ERR_QPDF_FAILED;
    }
}

PV_API pv_status_t pv_qpdf_encrypt_aes256(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path,
    const char* user_password,
    const char* owner_password,
    int32_t allow_print,
    int32_t allow_modify,
    int32_t allow_extract) {
    if (!src_utf8_path || *src_utf8_path == '\0' ||
        !dest_utf8_path || *dest_utf8_path == '\0' ||
        !owner_password || *owner_password == '\0') {
        pv_internal::set_last_error("Invalid arguments to pv_qpdf_encrypt_aes256");
        return PV_ERR_INVALID_ARG;
    }

    try {
        QPDF pdf;
        pdf.processFile(src_utf8_path, src_utf8_password ? src_utf8_password : "");

        QPDFWriter writer(pdf, dest_utf8_path);
        writer.setCompressStreams(true);
        writer.setObjectStreamMode(qpdf_o_generate);
        writer.setR6EncryptionParameters(
            user_password ? user_password : "",
            owner_password,
            /*allow_accessibility*/ true,
            /*allow_extract*/ allow_extract != 0,
            /*allow_assemble*/ allow_modify != 0,
            /*allow_annotate_and_form*/ allow_modify != 0,
            /*allow_form_filling*/ allow_modify != 0,
            /*allow_modify_other*/ allow_modify != 0,
            /*print*/ allow_print ? qpdf_r3p_full : qpdf_r3p_none,
            /*encrypt_metadata_aes*/ true);
        writer.write();

        pv_internal::set_last_error("");
        return PV_OK;
    } catch (const std::exception& ex) {
        pv_internal::set_last_error(std::string("QPDF encrypt failed: ") + ex.what());
        return PV_ERR_QPDF_FAILED;
    } catch (...) {
        pv_internal::set_last_error("QPDF encrypt failed with unknown exception");
        return PV_ERR_QPDF_FAILED;
    }
}

PV_API pv_status_t pv_qpdf_is_linearized(
    const char* utf8_path,
    const char* utf8_password,
    int32_t* out_is_linearized) {
    if (!utf8_path || *utf8_path == '\0' || !out_is_linearized) {
        pv_internal::set_last_error("Invalid arguments to pv_qpdf_is_linearized");
        return PV_ERR_INVALID_ARG;
    }
    *out_is_linearized = 0;

    try {
        QPDF pdf;
        pdf.processFile(utf8_path, utf8_password ? utf8_password : "");
        *out_is_linearized = pdf.isLinearized() ? 1 : 0;
        pv_internal::set_last_error("");
        return PV_OK;
    } catch (const std::exception& ex) {
        pv_internal::set_last_error(std::string("QPDF check failed: ") + ex.what());
        return PV_ERR_QPDF_FAILED;
    } catch (...) {
        pv_internal::set_last_error("QPDF check failed with unknown exception");
        return PV_ERR_QPDF_FAILED;
    }
}

PV_API pv_status_t pv_qpdf_get_form_fields(
    const char* utf8_path,
    const char* utf8_password,
    pv_form_field_t** out_fields,
    int32_t* out_count) {
    if (!utf8_path || *utf8_path == '\0' || !out_fields || !out_count) {
        pv_internal::set_last_error("Invalid arguments to pv_qpdf_get_form_fields");
        return PV_ERR_INVALID_ARG;
    }
    *out_fields = nullptr;
    *out_count = 0;

    try {
        QPDF pdf;
        pdf.processFile(utf8_path, utf8_password ? utf8_password : "");
        QPDFAcroFormDocumentHelper afdh(pdf);
        auto fields = afdh.getFormFields();
        if (fields.empty()) {
            return PV_OK;
        }

        // Map page object IDs and annotation object IDs -> 0-based page index
        QPDFPageDocumentHelper pdh(pdf);
        auto all_pages = pdh.getAllPages();
        std::map<QPDFObjGen, int32_t> page_to_index;
        std::map<QPDFObjGen, int32_t> annot_to_page;
        for (size_t p = 0; p < all_pages.size(); ++p) {
            auto ph = all_pages[p].getObjectHandle();
            if (ph.isIndirect()) {
                page_to_index[ph.getObjGen()] = static_cast<int32_t>(p);
            }
            auto annots = ph.getKey("/Annots");
            if (annots.isArray()) {
                for (auto item : QPDFObjectHandle::QPDFArrayItems(annots)) {
                    if (item.isIndirect()) {
                        annot_to_page[item.getObjGen()] = static_cast<int32_t>(p);
                    }
                }
            }
        }

        auto* arr = static_cast<pv_form_field_t*>(std::calloc(fields.size(), sizeof(pv_form_field_t)));
        if (!arr) {
            pv_internal::set_last_error("Out of memory allocating form fields");
            return PV_ERR_OUT_OF_MEMORY;
        }

        for (size_t i = 0; i < fields.size(); ++i) {
            auto& f = fields[i];
            arr[i].name_utf8 = dup_cstr(f.getFullyQualifiedName());
            arr[i].field_type_utf8 = dup_cstr(f.getFieldType());
            arr[i].value_utf8 = dup_cstr(f.getValueAsString());
            arr[i].is_checked = (f.isCheckbox() && f.isChecked()) ? 1 : 0;
            arr[i].page_index = -1;

            auto annots = afdh.getAnnotationsForField(f);
            if (!annots.empty()) {
                auto a = annots[0];
                auto r = a.getRect();
                arr[i].rect = pv_rect_t{
                    std::min(r.llx, r.urx),
                    std::max(r.lly, r.ury),
                    std::max(r.llx, r.urx),
                    std::min(r.lly, r.ury)
                };

                auto ah = a.getObjectHandle();
                if (ah.isIndirect()) {
                    auto it = annot_to_page.find(ah.getObjGen());
                    if (it != annot_to_page.end()) {
                        arr[i].page_index = it->second;
                    }
                }
                if (arr[i].page_index < 0 && ah.isDictionary() && ah.hasKey("/P")) {
                    auto p_ref = ah.getKey("/P");
                    if (p_ref.isIndirect()) {
                        auto it = page_to_index.find(p_ref.getObjGen());
                        if (it != page_to_index.end()) {
                            arr[i].page_index = it->second;
                        }
                    }
                }
            }
        }

        *out_fields = arr;
        *out_count = static_cast<int32_t>(fields.size());
        return PV_OK;
    } catch (const std::exception& ex) {
        pv_internal::set_last_error(std::string("QPDF get form fields failed: ") + ex.what());
        return PV_ERR_QPDF_FAILED;
    }
}

PV_API pv_status_t pv_qpdf_set_form_field_value(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path,
    const char* field_name_utf8,
    const char* value_utf8) {
    if (!src_utf8_path || !dest_utf8_path || !field_name_utf8 || !value_utf8) {
        pv_internal::set_last_error("Invalid arguments to pv_qpdf_set_form_field_value");
        return PV_ERR_INVALID_ARG;
    }

    try {
        QPDF pdf;
        pdf.processFile(src_utf8_path, src_utf8_password ? src_utf8_password : "");
        QPDFAcroFormDocumentHelper afdh(pdf);
        auto fields = afdh.getFormFields();

        bool found = false;
        for (auto& f : fields) {
            if (f.getFullyQualifiedName() == field_name_utf8) {
                if (f.isCheckbox()) {
                    bool check = (std::string(value_utf8) == "true" || std::string(value_utf8) == "Yes" || std::string(value_utf8) == "1");
                    f.setV(QPDFObjectHandle::newName(check ? "/Yes" : "/Off"), true);
                } else {
                    f.setV(std::string(value_utf8), true);
                    auto annots = afdh.getAnnotationsForField(f);
                    for (auto& a : annots) {
                        f.generateAppearance(a);
                    }
                }
                found = true;
                break;
            }
        }

        if (!found) {
            pv_internal::set_last_error(std::string("Form field not found: ") + field_name_utf8);
            return PV_ERR_EDIT_FAILED;
        }

        QPDFWriter writer(pdf, dest_utf8_path);
        writer.setCompressStreams(true);
        writer.write();
        return PV_OK;
    } catch (const std::exception& ex) {
        pv_internal::set_last_error(std::string("QPDF set form field failed: ") + ex.what());
        return PV_ERR_QPDF_FAILED;
    }
}

PV_API pv_status_t pv_qpdf_add_text_field(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path,
    int32_t page_index,
    const char* field_name_utf8,
    const pv_rect_t* rect,
    const char* default_value_utf8) {
    if (!src_utf8_path || !dest_utf8_path || !field_name_utf8 || !rect || page_index < 0) {
        pv_internal::set_last_error("Invalid arguments to pv_qpdf_add_text_field");
        return PV_ERR_INVALID_ARG;
    }

    try {
        QPDF pdf;
        pdf.processFile(src_utf8_path, src_utf8_password ? src_utf8_password : "");
        QPDFPageDocumentHelper pdh(pdf);
        auto pages = pdh.getAllPages();
        if (static_cast<size_t>(page_index) >= pages.size()) {
            pv_internal::set_last_error("Page index out of range");
            return PV_ERR_INVALID_PAGE;
        }

        auto page_obj = pages[static_cast<size_t>(page_index)].getObjectHandle();

        // Ensure /AcroForm exists in catalog
        auto root = pdf.getRoot();
        QPDFObjectHandle acroform = root.getKey("/AcroForm");
        if (acroform.isNull()) {
            acroform = pdf.makeIndirectObject(QPDFObjectHandle::newDictionary());
            acroform.replaceKey("/Fields", QPDFObjectHandle::newArray());
            acroform.replaceKey("/NeedAppearances", QPDFObjectHandle::newBool(true));
            auto dr = QPDFObjectHandle::newDictionary();
            auto fonts = QPDFObjectHandle::newDictionary();
            auto helv = pdf.makeIndirectObject(
                QPDFObjectHandle::parse("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
            fonts.replaceKey("/Helv", helv);
            dr.replaceKey("/Font", fonts);
            acroform.replaceKey("/DR", dr);
            root.replaceKey("/AcroForm", acroform);
        } else {
            acroform.replaceKey("/NeedAppearances", QPDFObjectHandle::newBool(true));
        }

        auto fields_arr = acroform.getKey("/Fields");
        if (!fields_arr.isArray()) {
            fields_arr = QPDFObjectHandle::newArray();
            acroform.replaceKey("/Fields", fields_arr);
        }

        // Create merged Widget + /Tx field object
        auto field = pdf.makeIndirectObject(QPDFObjectHandle::newDictionary());
        field.replaceKey("/Type", QPDFObjectHandle::newName("/Annot"));
        field.replaceKey("/Subtype", QPDFObjectHandle::newName("/Widget"));
        field.replaceKey("/FT", QPDFObjectHandle::newName("/Tx"));
        field.replaceKey("/T", QPDFObjectHandle::newUnicodeString(field_name_utf8));
        field.replaceKey("/V", QPDFObjectHandle::newUnicodeString(default_value_utf8 ? default_value_utf8 : ""));
        field.replaceKey("/DA", QPDFObjectHandle::newString("/Helv 11 Tf 0 g"));
        field.replaceKey("/F", QPDFObjectHandle::newInteger(4)); // Print flag
        field.replaceKey("/P", page_obj);

        auto rect_arr = QPDFObjectHandle::newArray();
        rect_arr.appendItem(QPDFObjectHandle::newReal(rect->left));
        rect_arr.appendItem(QPDFObjectHandle::newReal(rect->bottom));
        rect_arr.appendItem(QPDFObjectHandle::newReal(rect->right));
        rect_arr.appendItem(QPDFObjectHandle::newReal(rect->top));
        field.replaceKey("/Rect", rect_arr);

        // Add subtle border style
        auto mk = QPDFObjectHandle::newDictionary();
        mk.replaceKey("/BC", QPDFObjectHandle::parse("[0.2 0.4 0.7]"));
        mk.replaceKey("/BG", QPDFObjectHandle::parse("[0.95 0.97 1.0]"));
        field.replaceKey("/MK", mk);

        fields_arr.appendItem(field);

        auto annots = page_obj.getKey("/Annots");
        if (!annots.isArray()) {
            annots = QPDFObjectHandle::newArray();
            page_obj.replaceKey("/Annots", annots);
        }
        annots.appendItem(field);

        QPDFAcroFormDocumentHelper afdh(pdf);
        afdh.validate(true);
        QPDFFormFieldObjectHelper ffh(field);
        QPDFAnnotationObjectHelper aoh(field);
        ffh.generateAppearance(aoh);

        QPDFWriter writer(pdf, dest_utf8_path);
        writer.setCompressStreams(true);
        writer.write();
        return PV_OK;
    } catch (const std::exception& ex) {
        pv_internal::set_last_error(std::string("QPDF add text field failed: ") + ex.what());
        return PV_ERR_QPDF_FAILED;
    }
}

PV_API pv_status_t pv_qpdf_set_form_field_rect(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path,
    const char* field_name_utf8,
    const pv_rect_t* new_rect) {
    if (!src_utf8_path || !dest_utf8_path || !field_name_utf8 || !new_rect) {
        pv_internal::set_last_error("Invalid arguments to pv_qpdf_set_form_field_rect");
        return PV_ERR_INVALID_ARG;
    }

    try {
        QPDF pdf;
        pdf.processFile(src_utf8_path, src_utf8_password ? src_utf8_password : "");
        QPDFAcroFormDocumentHelper afdh(pdf);
        auto fields = afdh.getFormFields();

        double left = std::min(new_rect->left, new_rect->right);
        double right = std::max(new_rect->left, new_rect->right);
        double bottom = std::min(new_rect->bottom, new_rect->top);
        double top = std::max(new_rect->bottom, new_rect->top);

        bool found = false;
        for (auto& f : fields) {
            if (f.getFullyQualifiedName() == field_name_utf8) {
                auto annots = afdh.getAnnotationsForField(f);
                for (auto& a : annots) {
                    auto ah = a.getObjectHandle();
                    auto rect_arr = QPDFObjectHandle::newArray();
                    rect_arr.appendItem(QPDFObjectHandle::newReal(left));
                    rect_arr.appendItem(QPDFObjectHandle::newReal(bottom));
                    rect_arr.appendItem(QPDFObjectHandle::newReal(right));
                    rect_arr.appendItem(QPDFObjectHandle::newReal(top));
                    ah.replaceKey("/Rect", rect_arr);
                    if (!f.isCheckbox()) {
                        f.generateAppearance(a);
                    }
                }
                found = true;
                break;
            }
        }

        if (!found) {
            pv_internal::set_last_error(std::string("Form field not found: ") + field_name_utf8);
            return PV_ERR_EDIT_FAILED;
        }

        QPDFWriter writer(pdf, dest_utf8_path);
        writer.setCompressStreams(true);
        writer.write();
        return PV_OK;
    } catch (const std::exception& ex) {
        pv_internal::set_last_error(std::string("QPDF move form field failed: ") + ex.what());
        return PV_ERR_QPDF_FAILED;
    }
}

PV_API pv_status_t pv_qpdf_remove_form_field(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path,
    const char* field_name_utf8) {
    if (!src_utf8_path || !dest_utf8_path || !field_name_utf8) {
        pv_internal::set_last_error("Invalid arguments to pv_qpdf_remove_form_field");
        return PV_ERR_INVALID_ARG;
    }

    try {
        QPDF pdf;
        pdf.processFile(src_utf8_path, src_utf8_password ? src_utf8_password : "");
        QPDFAcroFormDocumentHelper afdh(pdf);
        auto fields = afdh.getFormFields();

        std::vector<QPDFObjGen> targets;
        for (auto& f : fields) {
            if (f.getFullyQualifiedName() == field_name_utf8) {
                auto fh = f.getObjectHandle();
                if (fh.isIndirect()) targets.push_back(fh.getObjGen());
                for (auto& a : afdh.getAnnotationsForField(f)) {
                    auto ah = a.getObjectHandle();
                    if (ah.isIndirect()) targets.push_back(ah.getObjGen());
                }
                break;
            }
        }

        if (targets.empty()) {
            pv_internal::set_last_error(std::string("Form field not found: ") + field_name_utf8);
            return PV_ERR_EDIT_FAILED;
        }

        auto is_target = [&](const QPDFObjectHandle& h) {
            if (!h.isIndirect()) return false;
            auto og = h.getObjGen();
            return std::find(targets.begin(), targets.end(), og) != targets.end();
        };

        auto root = pdf.getRoot();
        if (root.hasKey("/AcroForm")) {
            auto af = root.getKey("/AcroForm");
            if (af.isDictionary() && af.hasKey("/Fields") && af.getKey("/Fields").isArray()) {
                auto arr = af.getKey("/Fields");
                for (int i = arr.getArrayNItems() - 1; i >= 0; --i) {
                    if (is_target(arr.getArrayItem(i))) {
                        arr.eraseItem(i);
                    }
                }
            }
        }

        QPDFPageDocumentHelper pdh(pdf);
        for (auto& p : pdh.getAllPages()) {
            auto ph = p.getObjectHandle();
            if (ph.hasKey("/Annots") && ph.getKey("/Annots").isArray()) {
                auto annots = ph.getKey("/Annots");
                for (int i = annots.getArrayNItems() - 1; i >= 0; --i) {
                    if (is_target(annots.getArrayItem(i))) {
                        annots.eraseItem(i);
                    }
                }
            }
        }

        QPDFWriter writer(pdf, dest_utf8_path);
        writer.setCompressStreams(true);
        writer.write();
        return PV_OK;
    } catch (const std::exception& ex) {
        pv_internal::set_last_error(std::string("QPDF remove form field failed: ") + ex.what());
        return PV_ERR_QPDF_FAILED;
    }
}

PV_API pv_status_t pv_qpdf_prepare_pades_signature(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path,
    int32_t page_index,
    const pv_rect_t* rect,
    const char* signer_label_utf8,
    const char* reason_utf8,
    const char* location_utf8,
    int32_t reserved_bytes) {
    if (!src_utf8_path || !dest_utf8_path || !rect || page_index < 0 || reserved_bytes < 1024) {
        pv_internal::set_last_error("Invalid arguments to pv_qpdf_prepare_pades_signature");
        return PV_ERR_INVALID_ARG;
    }

    try {
        QPDF pdf;
        pdf.processFile(src_utf8_path, src_utf8_password ? src_utf8_password : "");
        QPDFPageDocumentHelper pdh(pdf);
        auto pages = pdh.getAllPages();
        if (static_cast<size_t>(page_index) >= pages.size()) {
            pv_internal::set_last_error("Page index out of range for signature");
            return PV_ERR_INVALID_PAGE;
        }

        auto page_obj = pages[static_cast<size_t>(page_index)].getObjectHandle();
        // Generate visible Form XObject appearance stream (/AP /N) for the signature widget
        double w = std::max(40.0, rect->right - rect->left);
        double h = std::max(24.0, rect->top - rect->bottom);
        std::string line1 = "Firmado: " + utf8_to_winansi_pdf_literal((signer_label_utf8 && *signer_label_utf8) ? signer_label_utf8 : "PDFVisor");
        std::string line2 = utf8_to_winansi_pdf_literal((reason_utf8 && *reason_utf8) ? reason_utf8 : "Firma digital PAdES");
        std::string line3 = "Fecha: " + current_pdf_date_utc();

        std::ostringstream ap_stream;
        ap_stream << "q\n"
                  << "0.95 0.97 1.0 rg 0 0 " << w << " " << h << " re f\n"
                  << "0.15 0.38 0.72 RG 1 w 0.5 0.5 " << (w - 1.0) << " " << (h - 1.0) << " re S\n"
                  << "BT /F1 8 Tf 0.08 0.16 0.35 rg 6 " << (h - 13.0) << " Td (" << line1 << ") Tj ET\n"
                  << "BT /F1 7 Tf 0.2 0.2 0.2 rg 6 " << (h - 24.0) << " Td (" << line2 << ") Tj ET\n"
                  << "BT /F1 6.5 Tf 0.35 0.35 0.35 rg 6 " << (h - 34.0) << " Td (" << line3 << ") Tj ET\n"
                  << "Q\n";

        const size_t hex_len = static_cast<size_t>(reserved_bytes) * 2;
        auto src_fs_path = utf8_to_fs_path(src_utf8_path);
        auto dest_fs_path = utf8_to_fs_path(dest_utf8_path);

        // True incremental co-signing: if the PDF already contains digital signatures, append an ISO 32000
        // incremental update at EOF so all prior signatures remain byte-for-byte intact and valid!
        if (has_existing_signatures(pdf) && !pdf.isEncrypted()) {
            if (!append_incremental_pades_revision(
                    pdf, src_fs_path, dest_fs_path, page_obj, rect, ap_stream.str(), w, h, reason_utf8, location_utf8, hex_len)) {
                return PV_ERR_QPDF_FAILED;
            }
            pv_internal::set_last_error("");
            return PV_OK;
        }

        auto root = pdf.getRoot();
        QPDFObjectHandle acroform = root.getKey("/AcroForm");
        if (acroform.isNull()) {
            acroform = pdf.makeIndirectObject(QPDFObjectHandle::newDictionary());
            acroform.replaceKey("/Fields", QPDFObjectHandle::newArray());
            root.replaceKey("/AcroForm", acroform);
        }
        // SigFlags = 3 (SignaturesExist | AppendOnly)
        acroform.replaceKey("/SigFlags", QPDFObjectHandle::newInteger(3));
        auto fields_arr = acroform.getKey("/Fields");
        if (!fields_arr.isArray()) {
            fields_arr = QPDFObjectHandle::newArray();
            acroform.replaceKey("/Fields", fields_arr);
        }

        // Reserve exact NUL-byte string: QPDFWriter emits binary strings with NUL bytes as <0000...0000>
        std::string zero_bytes(static_cast<size_t>(reserved_bytes), '\0');
        const std::string br_sentinel = "[ 0 1111111111 2222222222 3333333333 ]";

        auto sig_dict = pdf.makeIndirectObject(QPDFObjectHandle::newDictionary());
        sig_dict.replaceKey("/Type", QPDFObjectHandle::newName("/Sig"));
        sig_dict.replaceKey("/Filter", QPDFObjectHandle::newName("/Adobe.PPKLite"));
        sig_dict.replaceKey("/SubFilter", QPDFObjectHandle::newName("/ETSI.CAdES.detached"));
        sig_dict.replaceKey("/ByteRange", QPDFObjectHandle::parse(br_sentinel));
        sig_dict.replaceKey("/Contents", QPDFObjectHandle::newString(zero_bytes));
        sig_dict.replaceKey("/M", QPDFObjectHandle::newString(current_pdf_date_utc()));
        if (reason_utf8 && *reason_utf8 != '\0') {
            sig_dict.replaceKey("/Reason", QPDFObjectHandle::newUnicodeString(reason_utf8));
        }
        if (location_utf8 && *location_utf8 != '\0') {
            sig_dict.replaceKey("/Location", QPDFObjectHandle::newUnicodeString(location_utf8));
        }

        auto sig_field = pdf.makeIndirectObject(QPDFObjectHandle::newDictionary());
        sig_field.replaceKey("/Type", QPDFObjectHandle::newName("/Annot"));
        sig_field.replaceKey("/Subtype", QPDFObjectHandle::newName("/Widget"));
        sig_field.replaceKey("/FT", QPDFObjectHandle::newName("/Sig"));
        sig_field.replaceKey("/T", QPDFObjectHandle::newString("PDFVisorSignature_" + std::to_string(fields_arr.getArrayNItems() + 1)));
        sig_field.replaceKey("/V", sig_dict);
        sig_field.replaceKey("/F", QPDFObjectHandle::newInteger(132)); // Print | Locked
        sig_field.replaceKey("/P", page_obj);

        auto rect_arr = QPDFObjectHandle::newArray();
        rect_arr.appendItem(QPDFObjectHandle::newReal(rect->left));
        rect_arr.appendItem(QPDFObjectHandle::newReal(rect->bottom));
        rect_arr.appendItem(QPDFObjectHandle::newReal(rect->right));
        rect_arr.appendItem(QPDFObjectHandle::newReal(rect->top));
        sig_field.replaceKey("/Rect", rect_arr);

        auto ap_dict = QPDFObjectHandle::newDictionary();
        ap_dict.replaceKey("/Type", QPDFObjectHandle::newName("/XObject"));
        ap_dict.replaceKey("/Subtype", QPDFObjectHandle::newName("/Form"));
        ap_dict.replaceKey("/BBox", QPDFObjectHandle::parse("[0 0 " + std::to_string(w) + " " + std::to_string(h) + "]"));
        auto ap_res = QPDFObjectHandle::newDictionary();
        auto ap_fonts = QPDFObjectHandle::newDictionary();
        ap_fonts.replaceKey("/F1", pdf.makeIndirectObject(
            QPDFObjectHandle::parse("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")));
        ap_res.replaceKey("/Font", ap_fonts);
        ap_dict.replaceKey("/Resources", ap_res);
        auto ap_obj = QPDFObjectHandle::newStream(&pdf, ap_stream.str());
        ap_obj.replaceDict(ap_dict);

        auto ap_wrapper = QPDFObjectHandle::newDictionary();
        ap_wrapper.replaceKey("/N", ap_obj);
        sig_field.replaceKey("/AP", ap_wrapper);

        fields_arr.appendItem(sig_field);

        auto annots = page_obj.getKey("/Annots");
        if (!annots.isArray()) {
            annots = QPDFObjectHandle::newArray();
            page_obj.replaceKey("/Annots", annots);
        }
        annots.appendItem(sig_field);

        // Write with object streams disabled so /Sig dictionary is plain top-level PDF
        QPDFWriter writer(pdf, dest_utf8_path);
        writer.setCompressStreams(true);
        writer.setObjectStreamMode(qpdf_o_disable);
        writer.write();

        if (!patch_prepared_pades_file(dest_fs_path, hex_len)) {
            return PV_ERR_QPDF_FAILED;
        }

        pv_internal::set_last_error("");
        return PV_OK;
    } catch (const std::exception& ex) {
        pv_internal::set_last_error(std::string("QPDF prepare PAdES failed: ") + ex.what());
        return PV_ERR_QPDF_FAILED;
    }
}

PV_API void pv_free_form_fields(pv_form_field_t* fields, int32_t count) {
    if (!fields) return;
    for (int32_t i = 0; i < count; ++i) {
        std::free(fields[i].name_utf8);
        std::free(fields[i].field_type_utf8);
        std::free(fields[i].value_utf8);
    }
    std::free(fields);
}

PV_API pv_status_t pv_qpdf_sanitize_metadata(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path) {
    if (!src_utf8_path || *src_utf8_path == '\0' || !dest_utf8_path || *dest_utf8_path == '\0') {
        pv_internal::set_last_error("Invalid arguments to pv_qpdf_sanitize_metadata");
        return PV_ERR_INVALID_ARG;
    }

    try {
        QPDF pdf;
        pdf.processFile(src_utf8_path, src_utf8_password ? src_utf8_password : "");

        // Strip XMP /Metadata, /PieceInfo from catalog & pages, and /Info dictionary from trailer to remove hidden data
        auto root = pdf.getRoot();
        if (root.hasKey("/Metadata")) root.removeKey("/Metadata");
        if (root.hasKey("/PieceInfo")) root.removeKey("/PieceInfo");
        QPDFPageDocumentHelper pdh(pdf);
        for (auto& p : pdh.getAllPages()) {
            auto ph = p.getObjectHandle();
            if (ph.hasKey("/PieceInfo")) ph.removeKey("/PieceInfo");
            if (ph.hasKey("/Metadata")) ph.removeKey("/Metadata");
        }
        auto trailer = pdf.getTrailer();
        if (trailer.hasKey("/Info")) {
            trailer.removeKey("/Info");
        }

        // Full rewrite eliminates any incremental history / deleted objects
        QPDFWriter writer(pdf, dest_utf8_path);
        writer.setCompressStreams(true);
        writer.setObjectStreamMode(qpdf_o_generate);
        writer.write();

        pv_internal::set_last_error("");
        return PV_OK;
    } catch (const std::exception& ex) {
        pv_internal::set_last_error(std::string("QPDF sanitize failed: ") + ex.what());
        return PV_ERR_QPDF_FAILED;
    }
}

PV_API pv_status_t pv_qpdf_get_metadata(
    const char* utf8_path,
    const char* utf8_password,
    pv_metadata_t* out_meta) {
    if (!utf8_path || *utf8_path == '\0' || !out_meta) {
        pv_internal::set_last_error("Invalid arguments to pv_qpdf_get_metadata");
        return PV_ERR_INVALID_ARG;
    }
    std::memset(out_meta, 0, sizeof(pv_metadata_t));

    try {
        QPDF pdf;
        pdf.processFile(utf8_path, utf8_password ? utf8_password : "");
        out_meta->pdf_version_utf8 = dup_cstr(pdf.getPDFVersion());
        out_meta->is_encrypted = pdf.isEncrypted() ? 1 : 0;
        out_meta->is_linearized = pdf.isLinearized() ? 1 : 0;

        auto read_info_str = [&](const char* key) -> char* {
            auto trailer = pdf.getTrailer();
            if (!trailer.hasKey("/Info")) return dup_cstr("");
            auto info = trailer.getKey("/Info");
            if (!info.isDictionary() || !info.hasKey(key)) return dup_cstr("");
            auto val = info.getKey(key);
            if (!val.isString()) return dup_cstr("");
            return dup_cstr(val.getUTF8Value());
        };

        out_meta->title_utf8 = read_info_str("/Title");
        out_meta->author_utf8 = read_info_str("/Author");
        out_meta->subject_utf8 = read_info_str("/Subject");
        out_meta->keywords_utf8 = read_info_str("/Keywords");
        return PV_OK;
    } catch (const std::exception& ex) {
        pv_internal::set_last_error(std::string("QPDF get metadata failed: ") + ex.what());
        return PV_ERR_QPDF_FAILED;
    }
}

PV_API pv_status_t pv_qpdf_set_metadata(
    const char* src_utf8_path,
    const char* src_utf8_password,
    const char* dest_utf8_path,
    const char* title_utf8,
    const char* author_utf8,
    const char* subject_utf8,
    const char* keywords_utf8) {
    if (!src_utf8_path || !dest_utf8_path) {
        pv_internal::set_last_error("Invalid arguments to pv_qpdf_set_metadata");
        return PV_ERR_INVALID_ARG;
    }

    try {
        QPDF pdf;
        pdf.processFile(src_utf8_path, src_utf8_password ? src_utf8_password : "");

        auto trailer = pdf.getTrailer();
        QPDFObjectHandle info;
        if (trailer.hasKey("/Info") && trailer.getKey("/Info").isDictionary()) {
            info = trailer.getKey("/Info");
        } else {
            info = pdf.makeIndirectObject(QPDFObjectHandle::newDictionary());
            trailer.replaceKey("/Info", info);
        }

        auto set_key = [&](const char* key, const char* val) {
            if (val && *val != '\0') info.replaceKey(key, QPDFObjectHandle::newUnicodeString(val));
            else if (info.hasKey(key)) info.removeKey(key);
        };

        set_key("/Title", title_utf8);
        set_key("/Author", author_utf8);
        set_key("/Subject", subject_utf8);
        set_key("/Keywords", keywords_utf8);

        QPDFWriter writer(pdf, dest_utf8_path);
        writer.setCompressStreams(true);
        writer.write();
        return PV_OK;
    } catch (const std::exception& ex) {
        pv_internal::set_last_error(std::string("QPDF set metadata failed: ") + ex.what());
        return PV_ERR_QPDF_FAILED;
    }
}

PV_API void pv_free_metadata(pv_metadata_t* meta) {
    if (!meta) return;
    std::free(meta->title_utf8);
    std::free(meta->author_utf8);
    std::free(meta->subject_utf8);
    std::free(meta->keywords_utf8);
    std::free(meta->pdf_version_utf8);
    std::memset(meta, 0, sizeof(pv_metadata_t));
}

} // extern "C"
