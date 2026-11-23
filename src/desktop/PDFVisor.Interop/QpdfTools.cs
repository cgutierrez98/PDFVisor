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
                result.Add(new PdfFormField(name, type, val, n.IsChecked != 0));
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

    public static void PreparePadesSignature(
        string sourcePath,
        string destinationPath,
        int pageIndex,
        PvRect rect,
        string? signerLabel = null,
        string? reason = null,
        string? location = null,
        int reservedBytes = 12288,
        string? sourcePassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

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
}
