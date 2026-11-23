using System.Runtime.InteropServices;

namespace PDFVisor.Interop;

internal sealed class PdfDocumentSafeHandle : SafeHandle
{
    public PdfDocumentSafeHandle() : base(nint.Zero, ownsHandle: true) { }

    public PdfDocumentSafeHandle(nint handle) : base(nint.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == nint.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.pv_doc_close(handle);
        return true;
    }
}

public sealed class PdfDocument : IDisposable
{
    private PdfDocumentSafeHandle _handle;
    private readonly string? _password;

    public string FilePath { get; private set; }
    public int PageCount => _handle.IsClosed ? 0 : NativeMethods.pv_doc_get_page_count(_handle.DangerousGetHandle());

    private PdfDocument(string filePath, string? password, PdfDocumentSafeHandle handle)
    {
        FilePath = filePath;
        _password = password;
        _handle = handle;
    }

    public static PdfDocument Open(string filePath, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var status = NativeMethods.pv_doc_open(filePath, password, out var rawHandle);
        PdfEngine.ThrowIfFailed(status);
        return new PdfDocument(filePath, password, new PdfDocumentSafeHandle(rawHandle));
    }

    public static PdfDocument CreateBlank(double widthPt = 595.0, double heightPt = 842.0)
    {
        var status = NativeMethods.pv_doc_create_blank(widthPt, heightPt, out var rawHandle);
        PdfEngine.ThrowIfFailed(status);
        return new PdfDocument(string.Empty, null, new PdfDocumentSafeHandle(rawHandle));
    }

    public int AppendBlankPage(double widthPt = 595.0, double heightPt = 842.0)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_doc_append_blank_page(_handle.DangerousGetHandle(), widthPt, heightPt, out int idx);
        PdfEngine.ThrowIfFailed(status);
        return idx;
    }

    public PvPageSize GetPageSize(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_doc_get_page_size(_handle.DangerousGetHandle(), pageIndex, out var size);
        PdfEngine.ThrowIfFailed(status);
        return size;
    }

    public int GetPageRotation(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_doc_get_page_rotation(_handle.DangerousGetHandle(), pageIndex, out int rot);
        PdfEngine.ThrowIfFailed(status);
        return rot;
    }

    public void SetPageRotation(int pageIndex, int rotation)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_doc_set_page_rotation(_handle.DangerousGetHandle(), pageIndex, rotation);
        PdfEngine.ThrowIfFailed(status);
    }

    public void DeletePage(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_doc_delete_page(_handle.DangerousGetHandle(), pageIndex);
        PdfEngine.ThrowIfFailed(status);
    }

    public void MovePage(int fromIndex, int toIndex)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_doc_move_page(_handle.DangerousGetHandle(), fromIndex, toIndex);
        PdfEngine.ThrowIfFailed(status);
    }

    public int ImportPages(string sourcePdfPath, int insertAtIndex, string? sourcePassword = null)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePdfPath);
        var status = NativeMethods.pv_doc_import_pages(
            _handle.DangerousGetHandle(),
            sourcePdfPath,
            sourcePassword,
            insertAtIndex,
            out int imported);
        PdfEngine.ThrowIfFailed(status);
        return imported;
    }

    public void ExtractPages(int[] pageIndices, string destinationPdfPath)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (pageIndices is null || pageIndices.Length == 0)
            throw new ArgumentException("At least one page index is required.", nameof(pageIndices));
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPdfPath);

        var status = NativeMethods.pv_doc_extract_pages(
            _handle.DangerousGetHandle(),
            pageIndices,
            pageIndices.Length,
            destinationPdfPath);
        PdfEngine.ThrowIfFailed(status);
    }

    public int GetAnnotationCount(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        return NativeMethods.pv_page_get_annot_count(_handle.DangerousGetHandle(), pageIndex);
    }

    public int AddHighlightAnnotation(int pageIndex, PvRect rect, uint rgba = 0xFFEA0080)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_page_add_highlight_annot(_handle.DangerousGetHandle(), pageIndex, ref rect, rgba, out int idx);
        PdfEngine.ThrowIfFailed(status);
        return idx;
    }

    public int AddSquareAnnotation(int pageIndex, PvRect rect, uint strokeRgba = 0xE53935FF, uint fillRgba = 0x00000000)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_page_add_square_annot(_handle.DangerousGetHandle(), pageIndex, ref rect, strokeRgba, fillRgba, out int idx);
        PdfEngine.ThrowIfFailed(status);
        return idx;
    }

    public int AddNoteAnnotation(int pageIndex, PvRect rect, string contents, uint rgba = 0xFFC107FF)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_page_add_note_annot(_handle.DangerousGetHandle(), pageIndex, ref rect, contents ?? string.Empty, rgba, out int idx);
        PdfEngine.ThrowIfFailed(status);
        return idx;
    }

    public int AddInkAnnotation(int pageIndex, PvPoint[] points, uint rgba = 0x1E88E5FF)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (points is null || points.Length < 2)
            throw new ArgumentException("At least 2 points are required for an ink stroke.", nameof(points));
        var status = NativeMethods.pv_page_add_ink_annot(_handle.DangerousGetHandle(), pageIndex, points, points.Length, rgba, out int idx);
        PdfEngine.ThrowIfFailed(status);
        return idx;
    }

    public void RemoveAnnotation(int pageIndex, int annotIndex)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_page_remove_annot(_handle.DangerousGetHandle(), pageIndex, annotIndex);
        PdfEngine.ThrowIfFailed(status);
    }

    public void SaveAs(string destinationPath, bool incremental = false)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        bool sameFile = !string.IsNullOrWhiteSpace(FilePath) &&
            string.Equals(
                Path.GetFullPath(destinationPath),
                Path.GetFullPath(FilePath),
                StringComparison.OrdinalIgnoreCase);

        if (sameFile)
        {
            SaveInPlace(incremental);
            return;
        }

        var status = NativeMethods.pv_doc_save(_handle.DangerousGetHandle(), destinationPath, incremental ? 1 : 0);
        PdfEngine.ThrowIfFailed(status);
    }

    /// <summary>
    /// Atomically saves over the currently open file by writing to a sibling temporary file,
    /// closing the active PDFium handle, replacing the file, and reopening the handle.
    /// </summary>
    public void SaveInPlace(bool incremental = false)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        string dir = Path.GetDirectoryName(FilePath) ?? Path.GetTempPath();
        string tmpPath = Path.Combine(dir, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var saveStatus = NativeMethods.pv_doc_save(_handle.DangerousGetHandle(), tmpPath, incremental ? 1 : 0);
            PdfEngine.ThrowIfFailed(saveStatus);

            _handle.Dispose();
            File.Move(tmpPath, FilePath, overwrite: true);

            var openStatus = NativeMethods.pv_doc_open(FilePath, _password, out var newRawHandle);
            PdfEngine.ThrowIfFailed(openStatus);
            _handle = new PdfDocumentSafeHandle(newRawHandle);
        }
        catch
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { }
            }
            throw;
        }
    }

    public byte[] RenderPageBgra(
        int pageIndex,
        int widthPx,
        int heightPx,
        int rotation = 0,
        uint backgroundArgb = 0xFFFFFFFF,
        bool renderAnnotations = true,
        bool lcdText = true)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (widthPx <= 0 || heightPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(widthPx), "Dimensions must be positive.");

        int stride = widthPx * 4;
        byte[] buffer = new byte[stride * heightPx];
        var opts = new PvRenderOptions
        {
            WidthPx = widthPx,
            HeightPx = heightPx,
            Rotation = rotation & 3,
            BackgroundArgb = backgroundArgb,
            RenderAnnotations = renderAnnotations ? 1 : 0,
            LcdText = lcdText ? 1 : 0
        };

        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                var status = NativeMethods.pv_page_render_bgra(
                    _handle.DangerousGetHandle(),
                    pageIndex,
                    ref opts,
                    ptr,
                    stride);
                PdfEngine.ThrowIfFailed(status);
            }
        }

        return buffer;
    }

    public byte[] RenderPageTileBgra(
        int pageIndex,
        int fullPageWidthPx,
        int fullPageHeightPx,
        int tileX,
        int tileY,
        int tileWidthPx,
        int tileHeightPx,
        int rotation = 0,
        uint backgroundArgb = 0xFFFFFFFF,
        bool renderAnnotations = true,
        bool lcdText = true)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (fullPageWidthPx <= 0 || fullPageHeightPx <= 0 || tileWidthPx <= 0 || tileHeightPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(tileWidthPx), "Tile and page dimensions must be positive.");

        int stride = tileWidthPx * 4;
        byte[] buffer = new byte[stride * tileHeightPx];
        unsafe
        {
            fixed (byte* ptr = buffer)
            {
                var status = NativeMethods.pv_page_render_tile_bgra(
                    _handle.DangerousGetHandle(),
                    pageIndex,
                    fullPageWidthPx,
                    fullPageHeightPx,
                    tileX,
                    tileY,
                    tileWidthPx,
                    tileHeightPx,
                    rotation & 3,
                    backgroundArgb,
                    renderAnnotations ? 1 : 0,
                    lcdText ? 1 : 0,
                    ptr,
                    stride);
                PdfEngine.ThrowIfFailed(status);
            }
        }
        return buffer;
    }

    public string ExtractPageText(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_page_extract_text(_handle.DangerousGetHandle(), pageIndex, out var ptr);
        PdfEngine.ThrowIfFailed(status);
        if (ptr == nint.Zero) return string.Empty;
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        }
        finally
        {
            NativeMethods.pv_free_string(ptr);
        }
    }

    public IReadOnlyList<PvRect> SearchPage(int pageIndex, string query, bool matchCase = false, bool wholeWord = false)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (string.IsNullOrEmpty(query)) return Array.Empty<PvRect>();

        var status = NativeMethods.pv_page_search_text(
            _handle.DangerousGetHandle(),
            pageIndex,
            query,
            matchCase ? 1 : 0,
            wholeWord ? 1 : 0,
            out var rectsPtr,
            out int count);
        PdfEngine.ThrowIfFailed(status);

        if (rectsPtr == nint.Zero || count <= 0) return Array.Empty<PvRect>();

        try
        {
            var results = new PvRect[count];
            int structSize = Marshal.SizeOf<PvRect>();
            for (int i = 0; i < count; i++)
            {
                results[i] = Marshal.PtrToStructure<PvRect>(rectsPtr + i * structSize);
            }
            return results;
        }
        finally
        {
            NativeMethods.pv_free_rects(rectsPtr);
        }
    }

    public IReadOnlyList<PdfBookmark> GetBookmarks()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_doc_get_bookmarks(_handle.DangerousGetHandle(), out var ptr, out int count);
        PdfEngine.ThrowIfFailed(status);

        if (ptr == nint.Zero || count <= 0) return Array.Empty<PdfBookmark>();

        try
        {
            var results = new List<PdfBookmark>(count);
            int structSize = Marshal.SizeOf<PvBookmarkNative>();
            for (int i = 0; i < count; i++)
            {
                var native = Marshal.PtrToStructure<PvBookmarkNative>(ptr + i * structSize);
                string title = native.TitleUtf8 != nint.Zero
                    ? Marshal.PtrToStringUTF8(native.TitleUtf8) ?? string.Empty
                    : string.Empty;
                results.Add(new PdfBookmark(native.Level, native.PageIndex, title));
            }
            return results;
        }
        finally
        {
            NativeMethods.pv_free_bookmarks(ptr, count);
        }
    }

    public IReadOnlyList<PdfSignatureRaw> GetSignatures()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_doc_get_signatures(_handle.DangerousGetHandle(), out var ptr, out int count);
        PdfEngine.ThrowIfFailed(status);

        if (ptr == nint.Zero || count <= 0) return Array.Empty<PdfSignatureRaw>();

        try
        {
            var list = new List<PdfSignatureRaw>(count);
            int structSize = Marshal.SizeOf<PvSignatureInfoNative>();
            for (int i = 0; i < count; i++)
            {
                var n = Marshal.PtrToStructure<PvSignatureInfoNative>(ptr + i * structSize);
                string subFilter = n.SubFilterUtf8 != nint.Zero ? Marshal.PtrToStringUTF8(n.SubFilterUtf8) ?? "" : "";
                string reason = n.ReasonUtf8 != nint.Zero ? Marshal.PtrToStringUTF8(n.ReasonUtf8) ?? "" : "";
                string time = n.TimeUtf8 != nint.Zero ? Marshal.PtrToStringUTF8(n.TimeUtf8) ?? "" : "";
                byte[] der = Array.Empty<byte>();
                if (n.ContentsDer != nint.Zero && n.ContentsLen > 0)
                {
                    der = new byte[n.ContentsLen];
                    Marshal.Copy(n.ContentsDer, der, 0, n.ContentsLen);
                }
                list.Add(new PdfSignatureRaw(
                    subFilter,
                    reason,
                    time,
                    [n.ByteRange0, n.ByteRange1, n.ByteRange2, n.ByteRange3],
                    der));
            }
            return list;
        }
        finally
        {
            NativeMethods.pv_free_signatures(ptr, count);
        }
    }

    public void ReplaceFileAndReload(string replacementPdfPath)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        _handle.Dispose();
        File.Copy(replacementPdfPath, FilePath, overwrite: true);
        var openStatus = NativeMethods.pv_doc_open(FilePath, _password, out var newRawHandle);
        PdfEngine.ThrowIfFailed(openStatus);
        _handle = new PdfDocumentSafeHandle(newRawHandle);
    }

    public string? Password => _password;

    public IReadOnlyList<PdfPageObjectInfo> GetPageObjects(int pageIndex)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_page_get_objects(_handle.DangerousGetHandle(), pageIndex, out var ptr, out int count);
        PdfEngine.ThrowIfFailed(status);

        if (ptr == nint.Zero || count <= 0) return Array.Empty<PdfPageObjectInfo>();

        try
        {
            var list = new List<PdfPageObjectInfo>(count);
            int structSize = Marshal.SizeOf<PvPageObjectInfoNative>();
            for (int i = 0; i < count; i++)
            {
                var n = Marshal.PtrToStructure<PvPageObjectInfoNative>(ptr + i * structSize);
                string text = n.TextUtf8 != nint.Zero ? Marshal.PtrToStringUTF8(n.TextUtf8) ?? "" : "";
                list.Add(new PdfPageObjectInfo(n.Index, (PdfPageObjectType)n.Type, n.Bounds, n.FontSize, text));
            }
            return list;
        }
        finally
        {
            NativeMethods.pv_free_page_objects(ptr, count);
        }
    }

    public void EditTextObject(int pageIndex, int objIndex, string newText)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        ArgumentException.ThrowIfNullOrEmpty(newText);
        var status = NativeMethods.pv_page_edit_text_object(_handle.DangerousGetHandle(), pageIndex, objIndex, newText);
        PdfEngine.ThrowIfFailed(status);
    }

    public void MovePageObject(int pageIndex, int objIndex, double dxPt, double dyPt)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_page_move_object(_handle.DangerousGetHandle(), pageIndex, objIndex, dxPt, dyPt);
        PdfEngine.ThrowIfFailed(status);
    }

    public void RemovePageObject(int pageIndex, int objIndex)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_page_remove_object(_handle.DangerousGetHandle(), pageIndex, objIndex);
        PdfEngine.ThrowIfFailed(status);
    }

    public int AddTextObject(int pageIndex, string text, string fontName, float fontSize, double xPt, double yPt, uint rgba = 0x111111FF)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        ArgumentException.ThrowIfNullOrEmpty(text);
        var status = NativeMethods.pv_page_add_text_object(
            _handle.DangerousGetHandle(), pageIndex, text, fontName, fontSize, xPt, yPt, rgba, out int idx);
        PdfEngine.ThrowIfFailed(status);
        return idx;
    }

    public int AddImageBgra(int pageIndex, byte[] bgraPixels, int widthPx, int heightPx, PvRect destRect)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (bgraPixels is null || bgraPixels.Length < widthPx * heightPx * 4)
            throw new ArgumentException("Invalid BGRA pixel buffer.", nameof(bgraPixels));

        unsafe
        {
            fixed (byte* ptr = bgraPixels)
            {
                var status = NativeMethods.pv_page_add_image_bgra(
                    _handle.DangerousGetHandle(), pageIndex, ptr, widthPx, heightPx, ref destRect, out int idx);
                PdfEngine.ThrowIfFailed(status);
                return idx;
            }
        }
    }

    public int ApplyRedaction(int pageIndex, PvRect rect, uint fillRgba = 0x000000FF)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_page_apply_redaction(
            _handle.DangerousGetHandle(), pageIndex, ref rect, fillRgba, out int removed);
        PdfEngine.ThrowIfFailed(status);
        return removed;
    }

    public int AddInvisibleOcrText(int pageIndex, string text, double xPt, double yPt, float fontSize = 12f)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        ArgumentException.ThrowIfNullOrEmpty(text);
        var status = NativeMethods.pv_page_add_invisible_text(
            _handle.DangerousGetHandle(), pageIndex, text, fontSize, xPt, yPt, out int idx);
        PdfEngine.ThrowIfFailed(status);
        return idx;
    }

    public int GetCharIndexAtPos(int pageIndex, double xPt, double yPt, double tolerancePt = 10.0)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        var status = NativeMethods.pv_page_get_char_index_at_pos(
            _handle.DangerousGetHandle(), pageIndex, xPt, yPt, tolerancePt, out int idx);
        PdfEngine.ThrowIfFailed(status);
        return idx;
    }

    public string GetTextRange(int pageIndex, int startChar, int charCount)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (startChar < 0 || charCount <= 0) return string.Empty;
        var status = NativeMethods.pv_page_get_text_range(
            _handle.DangerousGetHandle(), pageIndex, startChar, charCount, out var ptr);
        PdfEngine.ThrowIfFailed(status);
        if (ptr == nint.Zero) return string.Empty;
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
        }
        finally
        {
            NativeMethods.pv_free_string(ptr);
        }
    }

    public IReadOnlyList<PvRect> GetTextRangeRects(int pageIndex, int startChar, int charCount)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (startChar < 0 || charCount <= 0) return Array.Empty<PvRect>();
        var status = NativeMethods.pv_page_get_text_range_rects(
            _handle.DangerousGetHandle(), pageIndex, startChar, charCount, out var ptr, out int count);
        PdfEngine.ThrowIfFailed(status);
        if (ptr == nint.Zero || count <= 0) return Array.Empty<PvRect>();
        try
        {
            var results = new PvRect[count];
            int structSize = Marshal.SizeOf<PvRect>();
            for (int i = 0; i < count; i++)
            {
                results[i] = Marshal.PtrToStructure<PvRect>(ptr + i * structSize);
            }
            return results;
        }
        finally
        {
            NativeMethods.pv_free_rects(ptr);
        }
    }

    public int AddUnicodeTextObject(int pageIndex, string text, string ttfFontPath, float fontSize, double xPt, double yPt, uint rgba = 0x111111FF)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(ttfFontPath);
        var status = NativeMethods.pv_page_add_unicode_text_object(
            _handle.DangerousGetHandle(), pageIndex, text, ttfFontPath, fontSize, xPt, yPt, rgba, out int idx);
        PdfEngine.ThrowIfFailed(status);
        return idx;
    }

    public void Dispose()
    {
        _handle.Dispose();
    }
}
