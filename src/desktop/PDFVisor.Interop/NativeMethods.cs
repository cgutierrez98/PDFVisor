using System.Runtime.InteropServices;

namespace PDFVisor.Interop;

public enum PvStatus : int
{
    Ok = 0,
    InvalidArg = 1,
    NotInitialized = 2,
    OpenFailed = 3,
    PasswordRequired = 4,
    InvalidPage = 5,
    RenderFailed = 6,
    OutOfMemory = 7,
    SaveFailed = 8,
    EditFailed = 9,
    QpdfFailed = 10,
    Unknown = 99
}

[StructLayout(LayoutKind.Sequential)]
public struct PvPageSize
{
    public double WidthPt;
    public double HeightPt;
}

[StructLayout(LayoutKind.Sequential)]
public struct PvRect
{
    public double Left;
    public double Top;
    public double Right;
    public double Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct PvPoint
{
    public float X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PvBookmarkNative
{
    public int Level;
    public int PageIndex;
    public nint TitleUtf8;
}

public sealed record PdfBookmark(int Level, int PageIndex, string Title);

[StructLayout(LayoutKind.Sequential)]
internal struct PvFormFieldNative
{
    public nint NameUtf8;
    public nint FieldTypeUtf8;
    public nint ValueUtf8;
    public int IsChecked;
}

public sealed record PdfFormField(string Name, string FieldType, string Value, bool IsChecked);

[StructLayout(LayoutKind.Sequential)]
internal struct PvSignatureInfoNative
{
    public nint SubFilterUtf8;
    public nint ReasonUtf8;
    public nint TimeUtf8;
    public int ByteRange0;
    public int ByteRange1;
    public int ByteRange2;
    public int ByteRange3;
    public nint ContentsDer;
    public int ContentsLen;
}

public sealed record PdfSignatureRaw(
    string SubFilter,
    string Reason,
    string Time,
    int[] ByteRange,
    byte[] ContentsDer);

public enum PdfPageObjectType : int
{
    Unknown = 0,
    Text = 1,
    Path = 2,
    Image = 3,
    Shading = 4,
    Form = 5
}

[StructLayout(LayoutKind.Sequential)]
internal struct PvPageObjectInfoNative
{
    public int Index;
    public int Type;
    public PvRect Bounds;
    public float FontSize;
    public nint TextUtf8;
}

public sealed record PdfPageObjectInfo(
    int Index,
    PdfPageObjectType Type,
    PvRect Bounds,
    float FontSize,
    string Text)
{
    public string DisplayLabel => Type switch
    {
        PdfPageObjectType.Text => $"[Texto {FontSize:0.#}pt] {(Text.Length > 40 ? Text[..40] + "…" : Text)}",
        PdfPageObjectType.Image => $"[Imagen] {Math.Round(Bounds.Right - Bounds.Left)}×{Math.Round(Bounds.Top - Bounds.Bottom)} pt",
        PdfPageObjectType.Path => $"[Vector] #{Index}",
        _ => $"[{Type}] #{Index}"
    };
}

[StructLayout(LayoutKind.Sequential)]
public struct PvRenderOptions
{
    public int WidthPx;
    public int HeightPx;
    public int Rotation;
    public uint BackgroundArgb;
    public int RenderAnnotations;
    public int LcdText;
}

internal static class NativeMethods
{
    private const string EngineDll = "PDFVisor.Engine.dll";

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_engine_init();

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pv_engine_shutdown();

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint pv_engine_version();

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint pv_get_last_error();

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_doc_open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8Path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? utf8Password,
        out nint outDoc);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_doc_create_blank(double widthPt, double heightPt, out nint outDoc);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_doc_append_blank_page(nint doc, double widthPt, double heightPt, out int outPageIndex);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pv_doc_close(nint doc);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pv_doc_get_page_count(nint doc);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_doc_get_page_size(nint doc, int pageIndex, out PvPageSize outSize);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_doc_save(
        nint doc,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8DestPath,
        int incremental);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_doc_get_page_rotation(nint doc, int pageIndex, out int outRotation);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_doc_set_page_rotation(nint doc, int pageIndex, int rotation);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_doc_delete_page(nint doc, int pageIndex);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_doc_move_page(nint doc, int fromIndex, int toIndex);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_doc_import_pages(
        nint destDoc,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string srcUtf8Path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? srcUtf8Password,
        int insertAtIndex,
        out int outImportedCount);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_doc_extract_pages(
        nint srcDoc,
        [In] int[] pageIndices,
        int count,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destUtf8Path);

    /* Annotations */
    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pv_page_get_annot_count(nint doc, int pageIndex);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_add_highlight_annot(
        nint doc,
        int pageIndex,
        ref PvRect rect,
        uint rgba,
        out int outAnnotIndex);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_add_square_annot(
        nint doc,
        int pageIndex,
        ref PvRect rect,
        uint strokeRgba,
        uint fillRgba,
        out int outAnnotIndex);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_add_note_annot(
        nint doc,
        int pageIndex,
        ref PvRect rect,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8Contents,
        uint rgba,
        out int outAnnotIndex);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_add_ink_annot(
        nint doc,
        int pageIndex,
        [In] PvPoint[] points,
        int pointCount,
        uint rgba,
        out int outAnnotIndex);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_remove_annot(
        nint doc,
        int pageIndex,
        int annotIndex);

    /* QPDF Optimization & AES-256 Encryption */
    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_qpdf_optimize(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string srcUtf8Path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? srcUtf8Password,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destUtf8Path,
        int linearize,
        int recompressStreams,
        int objectStreams);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_qpdf_encrypt_aes256(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string srcUtf8Path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? srcUtf8Password,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destUtf8Path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? userPassword,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ownerPassword,
        int allowPrint,
        int allowModify,
        int allowExtract);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_qpdf_is_linearized(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8Path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? utf8Password,
        out int outIsLinearized);

    /* Phase 3: AcroForms & Signatures */
    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_qpdf_get_form_fields(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8Path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? utf8Password,
        out nint outFields,
        out int outCount);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_qpdf_set_form_field_value(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string srcUtf8Path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? srcUtf8Password,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destUtf8Path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fieldNameUtf8,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string valueUtf8);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_qpdf_add_text_field(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string srcUtf8Path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? srcUtf8Password,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destUtf8Path,
        int pageIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fieldNameUtf8,
        ref PvRect rect,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? defaultValueUtf8);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_qpdf_prepare_pades_signature(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string srcUtf8Path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? srcUtf8Password,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destUtf8Path,
        int pageIndex,
        ref PvRect rect,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? signerLabelUtf8,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? reasonUtf8,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? locationUtf8,
        int reservedBytes);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_doc_get_signatures(
        nint doc,
        out nint outSigs,
        out int outCount);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pv_free_form_fields(nint fields, int count);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pv_free_signatures(nint sigs, int count);

    /* Phase 4: Direct Content Editing */
    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_get_objects(
        nint doc,
        int pageIndex,
        out nint outObjects,
        out int outCount);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_edit_text_object(
        nint doc,
        int pageIndex,
        int objIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newUtf8Text);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_move_object(
        nint doc,
        int pageIndex,
        int objIndex,
        double dxPt,
        double dyPt);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_remove_object(
        nint doc,
        int pageIndex,
        int objIndex);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_add_text_object(
        nint doc,
        int pageIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8Text,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string fontNameAscii,
        float fontSize,
        double xPt,
        double yPt,
        uint rgba,
        out int outObjIndex);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe PvStatus pv_page_add_image_bgra(
        nint doc,
        int pageIndex,
        byte* bgraPixels,
        int imgWidthPx,
        int imgHeightPx,
        ref PvRect destRect,
        out int outObjIndex);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pv_free_page_objects(nint objects, int count);

    /* Phase 5: Permanent Redaction, Sanitization & Invisible OCR Layer */
    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_apply_redaction(
        nint doc,
        int pageIndex,
        ref PvRect rect,
        uint fillRgba,
        out int outRemovedObjects);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_add_invisible_text(
        nint doc,
        int pageIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8Text,
        float fontSize,
        double xPt,
        double yPt,
        out int outObjIndex);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_qpdf_sanitize_metadata(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string srcUtf8Path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? srcUtf8Password,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destUtf8Path);

    /* Blocks 1-2: Text selection & TrueType Unicode text */
    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_get_char_index_at_pos(
        nint doc,
        int pageIndex,
        double xPt,
        double yPt,
        double tolerancePt,
        out int outCharIndex);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_get_text_range(
        nint doc,
        int pageIndex,
        int startChar,
        int charCount,
        out nint outUtf8Text);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_get_text_range_rects(
        nint doc,
        int pageIndex,
        int startChar,
        int charCount,
        out nint outRects,
        out int outCount);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_add_unicode_text_object(
        nint doc,
        int pageIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8Text,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ttfFontPathUtf8,
        float fontSize,
        double xPt,
        double yPt,
        uint rgba,
        out int outObjIndex);

    /* Rendering & Text */
    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe PvStatus pv_page_render_bgra(
        nint doc,
        int pageIndex,
        ref PvRenderOptions options,
        byte* outBgraBuffer,
        int strideBytes);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe PvStatus pv_page_render_tile_bgra(
        nint doc,
        int pageIndex,
        int fullPageWidthPx,
        int fullPageHeightPx,
        int tileX,
        int tileY,
        int tileWidthPx,
        int tileHeightPx,
        int rotation,
        uint backgroundArgb,
        int renderAnnotations,
        int lcdText,
        byte* outBgraBuffer,
        int strideBytes);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_extract_text(
        nint doc,
        int pageIndex,
        out nint outUtf8Text);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_page_search_text(
        nint doc,
        int pageIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string utf8Query,
        int matchCase,
        int wholeWord,
        out nint outRects,
        out int outCount);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern PvStatus pv_doc_get_bookmarks(
        nint doc,
        out nint outBookmarks,
        out int outCount);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pv_free_string(nint str);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pv_free_rects(nint rects);

    [DllImport(EngineDll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pv_free_bookmarks(nint bookmarks, int count);
}
