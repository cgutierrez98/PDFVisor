#define PDFVISOR_ENGINE_EXPORTS
#include "pdfvisor/pdfvisor_capi.h"

#include <qpdf/QPDF.hh>
#include <qpdf/QPDFWriter.hh>
#include <qpdf/QPDFPageDocumentHelper.hh>
#include <qpdf/QPDFAcroFormDocumentHelper.hh>
#include <qpdf/QPDFFormFieldObjectHelper.hh>
#include <qpdf/QPDFAnnotationObjectHelper.hh>
#include <qpdf/Constants.h>

#include <cstdlib>
#include <cstring>
#include <ctime>
#include <exception>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <string>
#include <vector>

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
        const size_t hex_len = static_cast<size_t>(reserved_bytes) * 2;
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

        // Generate visible Form XObject appearance stream (/AP /N) for the signature widget
        double w = std::max(40.0, rect->right - rect->left);
        double h = std::max(24.0, rect->top - rect->bottom);
        auto escape_pdf = [](const char* s) {
            std::string out;
            if (!s) return out;
            for (const char* p = s; *p; ++p) {
                unsigned char ch = static_cast<unsigned char>(*p);
                if (ch == '(' || ch == ')' || ch == '\\') { out.push_back('\\'); out.push_back(static_cast<char>(ch)); }
                else if (ch >= 32 && ch < 127) { out.push_back(static_cast<char>(ch)); }
                else { out.push_back('?'); }
            }
            return out;
        };
        std::string line1 = "Firmado: " + escape_pdf((signer_label_utf8 && *signer_label_utf8) ? signer_label_utf8 : "PDFVisor");
        std::string line2 = escape_pdf((reason_utf8 && *reason_utf8) ? reason_utf8 : "Firma digital PAdES");
        std::string line3 = "Fecha: " + current_pdf_date_utc();

        std::ostringstream ap_stream;
        ap_stream << "q\n"
                  << "0.95 0.97 1.0 rg 0 0 " << w << " " << h << " re f\n"
                  << "0.15 0.38 0.72 RG 1 w 0.5 0.5 " << (w - 1.0) << " " << (h - 1.0) << " re S\n"
                  << "BT /F1 8 Tf 0.08 0.16 0.35 rg 6 " << (h - 13.0) << " Td (" << line1 << ") Tj ET\n"
                  << "BT /F1 7 Tf 0.2 0.2 0.2 rg 6 " << (h - 24.0) << " Td (" << line2 << ") Tj ET\n"
                  << "BT /F1 6.5 Tf 0.35 0.35 0.35 rg 6 " << (h - 34.0) << " Td (" << line3 << ") Tj ET\n"
                  << "Q\n";

        auto ap_dict = QPDFObjectHandle::newDictionary();
        ap_dict.replaceKey("/Type", QPDFObjectHandle::newName("/XObject"));
        ap_dict.replaceKey("/Subtype", QPDFObjectHandle::newName("/Form"));
        ap_dict.replaceKey("/BBox", QPDFObjectHandle::parse("[0 0 " + std::to_string(w) + " " + std::to_string(h) + "]"));
        auto ap_res = QPDFObjectHandle::newDictionary();
        auto ap_fonts = QPDFObjectHandle::newDictionary();
        ap_fonts.replaceKey("/F1", pdf.makeIndirectObject(
            QPDFObjectHandle::parse("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")));
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

        // Patch (AAAA...) -> <0000...> and /ByteRange in-place with identical byte lengths!
        std::fstream fs(dest_utf8_path, std::ios::in | std::ios::out | std::ios::binary);
        if (!fs.is_open()) {
            pv_internal::set_last_error("Could not reopen prepared PDF for ByteRange patching");
            return PV_ERR_SAVE_FAILED;
        }
        std::string file_bytes((std::istreambuf_iterator<char>(fs)), std::istreambuf_iterator<char>());

        std::string hex_placeholder = "<" + std::string(hex_len, '0') + ">";
        size_t contents_pos = file_bytes.find(hex_placeholder);
        if (contents_pos == std::string::npos) {
            pv_internal::set_last_error("Failed to locate signature /Contents hex placeholder");
            return PV_ERR_QPDF_FAILED;
        }

        // Locate /ByteRange array emitted by QPDF (may be "[ 0 1111111111 2222222222 3333333333 ]" or "[0 1111111111 2222222222 3333333333]")
        size_t br_key = file_bytes.find("1111111111 2222222222 3333333333");
        if (br_key == std::string::npos) {
            pv_internal::set_last_error("Failed to locate signature /ByteRange placeholder");
            return PV_ERR_QPDF_FAILED;
        }
        size_t br_open = file_bytes.rfind('[', br_key);
        size_t br_close = file_bytes.find(']', br_key);
        if (br_open == std::string::npos || br_close == std::string::npos || br_close <= br_open) {
            pv_internal::set_last_error("Malformed /ByteRange placeholder");
            return PV_ERR_QPDF_FAILED;
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
            return PV_ERR_QPDF_FAILED;
        }
        // Pad with spaces before ']' to preserve exact byte length
        br_real.insert(br_real.size() - 1, br_slot_len - br_real.size(), ' ');
        file_bytes.replace(br_open, br_slot_len, br_real);

        fs.seekp(0);
        fs.write(file_bytes.data(), static_cast<std::streamsize>(file_bytes.size()));
        fs.flush();
        fs.close();

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

        // Strip XMP /Metadata from catalog and /Info dictionary from trailer to remove hidden data
        auto root = pdf.getRoot();
        if (root.hasKey("/Metadata")) {
            root.removeKey("/Metadata");
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
