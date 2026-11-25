using System.Runtime.InteropServices;

namespace PDFVisor.Interop;

public static class QpdfTools
{
    public static void Optimize(
        string sourcePath,
        string destinationPath,
        bool linearize = true,
        bool recompressStreams = true,
        bool objectStreams = true,
        string? sourcePassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var status = NativeMethods.pv_qpdf_optimize(
            sourcePath,
            sourcePassword,
            destinationPath,
            linearize ? 1 : 0,
            recompressStreams ? 1 : 0,
            objectStreams ? 1 : 0);
        PdfEngine.ThrowIfFailed(status);
    }

    public static void EncryptAes256(
        string sourcePath,
        string destinationPath,
        string userPassword,
        string ownerPassword,
        bool allowPrint = true,
        bool allowModify = false,
        bool allowExtract = false,
        string? sourcePassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPassword);

        var status = NativeMethods.pv_qpdf_encrypt_aes256(
            sourcePath,
            sourcePassword,
            destinationPath,
            userPassword,
            ownerPassword,
            allowPrint ? 1 : 0,
            allowModify ? 1 : 0,
            allowExtract ? 1 : 0);
        PdfEngine.ThrowIfFailed(status);
    }

    public static bool IsLinearized(string pdfPath, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var status = NativeMethods.pv_qpdf_is_linearized(pdfPath, password, out int linearized);
        PdfEngine.ThrowIfFailed(status);
        return linearized != 0;
    }

    public static IReadOnlyList<PdfFormField> GetFormFields(string pdfPath, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var status = NativeMethods.pv_qpdf_get_form_fields(pdfPath, password, out var ptr, out int count);
        PdfEngine.ThrowIfFailed(status);

        if (ptr == nint.Zero || count <= 0) return Array.Empty<PdfFormField>();

        try
        {
            var result = new List<PdfFormField>(count);
            int structSize = Marshal.SizeOf<PvFormFieldNative>();
            for (int i = 0; i < count; i++)
            {
                var n = Marshal.PtrToStructure<PvFormFieldNative>(ptr + i * structSize);
                string name = n.NameUtf8 != nint.Zero ? Marshal.PtrToStringUTF8(n.NameUtf8) ?? "" : "";
                string type = n.FieldTypeUtf8 != nint.Zero ? Marshal.PtrToStringUTF8(n.FieldTypeUtf8) ?? "" : "";
                string val = n.ValueUtf8 != nint.Zero ? Marshal.PtrToStringUTF8(n.ValueUtf8) ?? "" : "";
                result.Add(new PdfFormField(name, type, val, n.IsChecked != 0, n.PageIndex, n.Rect));
            }
            return result;
        }
        finally
        {
            NativeMethods.pv_free_form_fields(ptr, count);
        }
    }

    public static void SetFormFieldValue(
        string sourcePath,
        string destinationPath,
        string fieldName,
        string value,
        string? sourcePassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        var status = NativeMethods.pv_qpdf_set_form_field_value(
            sourcePath,
            sourcePassword,
            destinationPath,
            fieldName,
            value ?? string.Empty);
        PdfEngine.ThrowIfFailed(status);
    }

    public static void AddTextField(
        string sourcePath,
        string destinationPath,
        int pageIndex,
        string fieldName,
        PvRect rect,
        string? defaultValue = null,
        string? sourcePassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        var status = NativeMethods.pv_qpdf_add_text_field(
            sourcePath,
            sourcePassword,
            destinationPath,
            pageIndex,
            fieldName,
            ref rect,
            defaultValue);
        PdfEngine.ThrowIfFailed(status);
    }

    public static void SetFormFieldRect(
        string sourcePath,
        string destinationPath,
        string fieldName,
        PvRect newRect,
        string? sourcePassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        var status = NativeMethods.pv_qpdf_set_form_field_rect(
            sourcePath,
            sourcePassword,
            destinationPath,
            fieldName,
            ref newRect);
        PdfEngine.ThrowIfFailed(status);
    }

    public static void RemoveFormField(
        string sourcePath,
        string destinationPath,
        string fieldName,
        string? sourcePassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        var status = NativeMethods.pv_qpdf_remove_form_field(
            sourcePath,
            sourcePassword,
            destinationPath,
            fieldName);
        PdfEngine.ThrowIfFailed(status);
    }

    public static void AddCheckboxField(
        string sourcePath,
        string destinationPath,
        int pageIndex,
        string fieldName,
        PvRect rect,
        bool initiallyChecked = false,
        string? sourcePassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        var status = NativeMethods.pv_qpdf_add_checkbox_field(
            sourcePath,
            sourcePassword,
            destinationPath,
            pageIndex,
            fieldName,
            ref rect,
            initiallyChecked ? 1 : 0);
        PdfEngine.ThrowIfFailed(status);
    }

    public static void Decrypt(
        string sourcePath,
        string destinationPath,
        string? sourcePassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var status = NativeMethods.pv_qpdf_decrypt(sourcePath, sourcePassword, destinationPath);
        PdfEngine.ThrowIfFailed(status);
    }

    public static void SetBookmarks(
        string sourcePath,
        string destinationPath,
        IReadOnlyList<PdfBookmark> bookmarks,
        string? sourcePassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(bookmarks);

        var ptrs = new List<nint>(bookmarks.Count);
        var nativeArr = new PvBookmarkNative[bookmarks.Count];
        try
        {
            for (int i = 0; i < bookmarks.Count; i++)
            {
                nint strPtr = Marshal.StringToCoTaskMemUTF8(bookmarks[i].Title ?? "Marcador");
                ptrs.Add(strPtr);
                nativeArr[i] = new PvBookmarkNative
                {
                    Level = bookmarks[i].Level,
                    PageIndex = bookmarks[i].PageIndex,
                    TitleUtf8 = strPtr
                };
            }

            unsafe
            {
                fixed (PvBookmarkNative* pArr = nativeArr)
                {
                    var status = NativeMethods.pv_qpdf_set_bookmarks(
                        sourcePath,
                        sourcePassword,
                        destinationPath,
                        pArr,
                        nativeArr.Length);
                    PdfEngine.ThrowIfFailed(status);
                }
            }
        }
        finally
        {
            foreach (var p in ptrs)
            {
                if (p != nint.Zero) Marshal.FreeCoTaskMem(p);
            }
        }
    }

    public static void PreparePadesSignature(
        string sourcePath,
        string destinationPath,
        int pageIndex,
        PvRect rect,
        string? signerLabel = null,
        string? reason = null,
        string? location = null,
        int reservedBytes = 12288,
        string? sourcePassword = null,
        (byte[] BgraPixels, int WidthPx, int HeightPx)? stampImage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (stampImage is { } img && img.BgraPixels.Length >= img.WidthPx * img.HeightPx * 4 && img.WidthPx > 0 && img.HeightPx > 0)
        {
            unsafe
            {
                fixed (byte* pImg = img.BgraPixels)
                {
                    var st = NativeMethods.pv_qpdf_prepare_pades_signature_ex(
                        sourcePath,
                        sourcePassword,
                        destinationPath,
                        pageIndex,
                        ref rect,
                        signerLabel,
                        reason,
                        location,
                        reservedBytes,
                        pImg,
                        img.WidthPx,
                        img.HeightPx);
                    PdfEngine.ThrowIfFailed(st);
                    return;
                }
            }
        }

        var status = NativeMethods.pv_qpdf_prepare_pades_signature(
            sourcePath,
            sourcePassword,
            destinationPath,
            pageIndex,
            ref rect,
            signerLabel,
            reason,
            location,
            reservedBytes);
        PdfEngine.ThrowIfFailed(status);
    }

    public static void SanitizeMetadata(string sourcePath, string destinationPath, string? sourcePassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var status = NativeMethods.pv_qpdf_sanitize_metadata(sourcePath, sourcePassword, destinationPath);
        PdfEngine.ThrowIfFailed(status);
    }

    public static PdfMetadata GetMetadata(string pdfPath, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var status = NativeMethods.pv_qpdf_get_metadata(pdfPath, password, out var native);
        PdfEngine.ThrowIfFailed(status);
        try
        {
            return new PdfMetadata(
                native.TitleUtf8 != nint.Zero ? Marshal.PtrToStringUTF8(native.TitleUtf8) ?? "" : "",
                native.AuthorUtf8 != nint.Zero ? Marshal.PtrToStringUTF8(native.AuthorUtf8) ?? "" : "",
                native.SubjectUtf8 != nint.Zero ? Marshal.PtrToStringUTF8(native.SubjectUtf8) ?? "" : "",
                native.KeywordsUtf8 != nint.Zero ? Marshal.PtrToStringUTF8(native.KeywordsUtf8) ?? "" : "",
                native.PdfVersionUtf8 != nint.Zero ? Marshal.PtrToStringUTF8(native.PdfVersionUtf8) ?? "" : "",
                native.IsEncrypted != 0,
                native.IsLinearized != 0);
        }
        finally
        {
            NativeMethods.pv_free_metadata(ref native);
        }
    }

    public static void SetMetadata(
        string sourcePath,
        string destinationPath,
        string title,
        string author,
        string subject,
        string keywords,
        string? sourcePassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var status = NativeMethods.pv_qpdf_set_metadata(
            sourcePath, sourcePassword, destinationPath, title, author, subject, keywords);
        PdfEngine.ThrowIfFailed(status);
    }
}
