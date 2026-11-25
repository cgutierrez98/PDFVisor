using System.Collections.ObjectModel;
using PDFVisor.Interop;

namespace PDFVisor.Core;

public sealed class PageItemViewModel : ObservableObject
{
    private object? _thumbnailImage;

    public int PageIndex { get; }
    public int DisplayNumber => PageIndex + 1;
    public double WidthPt { get; }
    public double HeightPt { get; }

    /// <summary>
    /// UI-layer BitmapSource populated lazily by the thumbnail renderer.
    /// </summary>
    public object? ThumbnailImage
    {
        get => _thumbnailImage;
        set => SetProperty(ref _thumbnailImage, value);
    }

    public PageItemViewModel(int pageIndex, double widthPt, double heightPt)
    {
        PageIndex = pageIndex;
        WidthPt = widthPt;
        HeightPt = heightPt;
    }
}

public sealed class BookmarkItemViewModel : ObservableObject
{
    private string _title;

    public int Level { get; set; }
    public int PageIndex { get; set; }
    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(DisplayIndent));
            }
        }
    }

    public ObservableCollection<BookmarkItemViewModel> Children { get; } = [];
    public string DisplayIndent => new string(' ', Math.Clamp(Level, 0, 8) * 3) + Title;

    public BookmarkItemViewModel(PdfBookmark bookmark)
    {
        Level = bookmark.Level;
        PageIndex = bookmark.PageIndex;
        _title = bookmark.Title;
    }
}

public sealed record SearchMatch(int PageIndex, PvRect Rect);

public enum AnnotationTool
{
    None,
    HandPan,
    Highlight,
    Underline,
    StrikeOut,
    Square,
    Note,
    Ink,
    AddText,
    Redact,
    SignStamp,
    AddFormField,
    AddCheckboxField,
    CropPage
}

public sealed class DocumentSession : ObservableObject, IDisposable
{
    private readonly PdfDocument _document;
    private int _currentPageIndex;
    private double _zoom = 1.0;
    private int _rotation;
    private bool _isModified;
    private int _documentRevision;
    private bool _darkReadingMode;
    private bool _twoPageMode;
    private AnnotationTool _activeTool = AnnotationTool.None;
    private uint _activeColorRgba = 0xE53935FF;
    private float _activeFontSize = 14f;
    private string _searchQuery = string.Empty;
    private bool _searchMatchCase;
    private bool _searchWholeWord;
    private List<SearchMatch> _searchMatches = [];
    private int _activeMatchIndex = -1;
    private readonly Stack<int> _backStack = new();
    private readonly Stack<int> _forwardStack = new();
    private bool _isNavigatingHistory;

    /// <summary>
    /// Limits concurrent background PDFium renders (tiles + thumbnails) to prevent ThreadPool starvation over g_pdfium_mutex.
    /// </summary>
    public SemaphoreSlim RenderGate { get; } = new(2, 2);

    public string FilePath => _document.FilePath;
    public string FileName => Path.GetFileName(_document.FilePath);
    public string DisplayTabTitle => IsModified ? $"{FileName} *" : FileName;
    public int PageCount => _document.PageCount;
    public ObservableCollection<PageItemViewModel> Pages { get; } = [];
    public ObservableCollection<BookmarkItemViewModel> Bookmarks { get; } = [];
    public ObservableCollection<BookmarkItemViewModel> BookmarkTree { get; } = [];
    public ObservableCollection<PdfAnnotationInfo> Annotations { get; } = [];
    public ObservableCollection<PdfAttachmentInfo> Attachments { get; } = [];
    public ObservableCollection<PdfFormField> FormFields { get; } = [];
    public ObservableCollection<PdfSignatureVerificationResult> Signatures { get; } = [];
    public ObservableCollection<PdfPageObjectInfo> CurrentPageObjects { get; } = [];
    public CommandHistory History { get; } = new();

    public bool CanNavigateBack => _backStack.Count > 0;
    public bool CanNavigateForward => _forwardStack.Count > 0;

    public bool IsModified
    {
        get => _isModified;
        private set
        {
            if (SetProperty(ref _isModified, value))
            {
                OnPropertyChanged(nameof(DisplayTabTitle));
            }
        }
    }

    /// <summary>
    /// Incremented whenever the underlying PDF content or page structure changes permanently.
    /// </summary>
    public int DocumentRevision
    {
        get => _documentRevision;
        private set => SetProperty(ref _documentRevision, value);
    }

    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            if (PageCount == 0) return;
            var clamped = Math.Clamp(value, 0, PageCount - 1);
            if (SetProperty(ref _currentPageIndex, clamped))
            {
                RefreshCurrentPageObjects();
                OnPropertyChanged(nameof(CurrentPageNumber));
                OnPropertyChanged(nameof(CurrentPageSizeText));
                OnPropertyChanged(nameof(CurrentPageHighlights));
            }
        }
    }

    public int CurrentPageNumber => PageCount > 0 ? CurrentPageIndex + 1 : 0;

    public double Zoom
    {
        get => _zoom;
        set
        {
            var clamped = Math.Clamp(value, 0.25, 8.0);
            if (SetProperty(ref _zoom, clamped))
            {
                OnPropertyChanged(nameof(ZoomPercentText));
            }
        }
    }

    public string ZoomPercentText => $"{Math.Round(Zoom * 100)}%";

    /// <summary>
    /// View-only rotation override (0..3). Permanent page rotation uses RotateCurrentPagePermanent().
    /// </summary>
    public int Rotation
    {
        get => _rotation;
        set => SetProperty(ref _rotation, ((value % 4) + 4) % 4);
    }

    public AnnotationTool ActiveTool
    {
        get => _activeTool;
        set => SetProperty(ref _activeTool, value);
    }

    public bool DarkReadingMode
    {
        get => _darkReadingMode;
        set => SetProperty(ref _darkReadingMode, value);
    }

    public bool TwoPageMode
    {
        get => _twoPageMode;
        set => SetProperty(ref _twoPageMode, value);
    }

    /// <summary>
    /// Active RGBA color (0xRRGGBBAA) used by annotation and text insertion tools.
    /// </summary>
    public uint ActiveColorRgba
    {
        get => _activeColorRgba;
        set => SetProperty(ref _activeColorRgba, value);
    }

    /// <summary>
    /// Active font size in PDF points used when inserting new text objects.
    /// </summary>
    public float ActiveFontSize
    {
        get => _activeFontSize;
        set => SetProperty(ref _activeFontSize, Math.Clamp(value, 6f, 72f));
    }

    public string SearchQuery
    {
        get => _searchQuery;
        private set => SetProperty(ref _searchQuery, value);
    }

    public bool SearchMatchCase
    {
        get => _searchMatchCase;
        set => SetProperty(ref _searchMatchCase, value);
    }

    public bool SearchWholeWord
    {
        get => _searchWholeWord;
        set => SetProperty(ref _searchWholeWord, value);
    }

    public int TotalSearchMatches => _searchMatches.Count;

    public SearchMatch? ActiveSearchMatch =>
        ActiveMatchIndex >= 0 && ActiveMatchIndex < _searchMatches.Count
            ? _searchMatches[ActiveMatchIndex]
            : null;

    public IReadOnlyList<PvRect> GetHighlightsForPage(int pageIndex) =>
        _searchMatches
            .Where(m => m.PageIndex == pageIndex)
            .Select(m => m.Rect)
            .ToArray();

    public int ActiveMatchIndex
    {
        get => _activeMatchIndex;
        private set
        {
            if (SetProperty(ref _activeMatchIndex, value))
            {
                OnPropertyChanged(nameof(SearchStatusText));
                OnPropertyChanged(nameof(ActiveMatchOnCurrentPage));
            }
        }
    }

    public string SearchStatusText =>
        string.IsNullOrEmpty(SearchQuery)
            ? string.Empty
            : TotalSearchMatches == 0
                ? "0 resultados"
                : $"{ActiveMatchIndex + 1} / {TotalSearchMatches}";

    public IReadOnlyList<PvRect> CurrentPageHighlights =>
        _searchMatches
            .Where(m => m.PageIndex == CurrentPageIndex)
            .Select(m => m.Rect)
            .ToArray();

    public PvRect? ActiveMatchOnCurrentPage =>
        ActiveMatchIndex >= 0 && ActiveMatchIndex < _searchMatches.Count &&
        _searchMatches[ActiveMatchIndex].PageIndex == CurrentPageIndex
            ? _searchMatches[ActiveMatchIndex].Rect
            : null;

    public string CurrentPageSizeText
    {
        get
        {
            if (Pages.Count == 0 || CurrentPageIndex >= Pages.Count) return string.Empty;
            var p = Pages[CurrentPageIndex];
            var mmW = Math.Round(p.WidthPt * 25.4 / 72.0, 1);
            var mmH = Math.Round(p.HeightPt * 25.4 / 72.0, 1);
            return $"{mmW} × {mmH} mm";
        }
    }

    public DocumentSession(PdfDocument document)
    {
        _document = document;
        RebuildPageList();
        RebuildBookmarksFromDocument();
        RefreshAnnotationsAndAttachments();
        RefreshFormFieldsAndSignatures();
        RefreshCurrentPageObjects();
    }

    public void JumpToPageWithHistory(int targetPageIndex)
    {
        if (PageCount == 0) return;
        int clamped = Math.Clamp(targetPageIndex, 0, PageCount - 1);
        if (clamped == CurrentPageIndex) return;

        if (!_isNavigatingHistory)
        {
            _backStack.Push(CurrentPageIndex);
            _forwardStack.Clear();
            OnPropertyChanged(nameof(CanNavigateBack));
            OnPropertyChanged(nameof(CanNavigateForward));
        }
        CurrentPageIndex = clamped;
    }

    public void NavigateBack()
    {
        if (_backStack.Count == 0) return;
        _isNavigatingHistory = true;
        try
        {
            _forwardStack.Push(CurrentPageIndex);
            CurrentPageIndex = _backStack.Pop();
            OnPropertyChanged(nameof(CanNavigateBack));
            OnPropertyChanged(nameof(CanNavigateForward));
        }
        finally { _isNavigatingHistory = false; }
    }

    public void NavigateForward()
    {
        if (_forwardStack.Count == 0) return;
        _isNavigatingHistory = true;
        try
        {
            _backStack.Push(CurrentPageIndex);
            CurrentPageIndex = _forwardStack.Pop();
            OnPropertyChanged(nameof(CanNavigateBack));
            OnPropertyChanged(nameof(CanNavigateForward));
        }
        finally { _isNavigatingHistory = false; }
    }

    public void RebuildBookmarksFromDocument()
    {
        Bookmarks.Clear();
        BookmarkTree.Clear();
        var stack = new Stack<BookmarkItemViewModel>();

        foreach (var bm in _document.GetBookmarks())
        {
            var vm = new BookmarkItemViewModel(bm);
            Bookmarks.Add(vm);

            while (stack.Count > 0 && stack.Peek().Level >= vm.Level)
            {
                stack.Pop();
            }

            if (stack.Count == 0)
                BookmarkTree.Add(vm);
            else
                stack.Peek().Children.Add(vm);

            stack.Push(vm);
        }
    }

    public void RefreshAnnotationsAndAttachments()
    {
        Annotations.Clear();
        for (int p = 0; p < PageCount; p++)
        {
            foreach (var a in _document.GetPageAnnotations(p))
            {
                if (a.Subtype is not PdfAnnotSubtype.Widget and not PdfAnnotSubtype.Popup and not PdfAnnotSubtype.Link)
                {
                    Annotations.Add(a);
                }
            }
        }

        Attachments.Clear();
        foreach (var att in _document.GetAttachments())
        {
            Attachments.Add(att);
        }
    }

    public IReadOnlyList<PdfAnnotationInfo> GetInteractiveAnnotationsForPage(int pageIndex) =>
        Annotations.Where(a => a.PageIndex == pageIndex).ToArray();

    public IReadOnlyList<PdfPageObjectInfo> GetImageObjectsForPage(int pageIndex) =>
        pageIndex == CurrentPageIndex
            ? CurrentPageObjects.Where(o => o.Type == PdfPageObjectType.Image).ToArray()
            : _document.GetPageObjects(pageIndex).Where(o => o.Type == PdfPageObjectType.Image).ToArray();

    public void RefreshCurrentPageObjects()
    {
        CurrentPageObjects.Clear();
        if (PageCount == 0 || CurrentPageIndex >= PageCount) return;
        foreach (var obj in _document.GetPageObjects(CurrentPageIndex))
        {
            CurrentPageObjects.Add(obj);
        }
    }

    public void EditTextObject(int objIndex, string oldText, string newText)
    {
        if (PageCount == 0) return;
        History.Execute(new EditTextObjectCommand(_document, CurrentPageIndex, objIndex, oldText, newText));
        OnDocumentMutated(CurrentPageIndex);
    }

    public void MovePageObject(int objIndex, double dxPt, double dyPt)
    {
        if (PageCount == 0) return;
        History.Execute(new MovePageObjectCommand(_document, CurrentPageIndex, objIndex, dxPt, dyPt));
        OnDocumentMutated(CurrentPageIndex);
    }

    public void ResizeOrMoveImageObject(int pageIndex, int objIndex, PvRect oldBounds, PvRect newBounds)
    {
        if (PageCount == 0) return;
        History.Execute(new ResizeImageObjectCommand(_document, pageIndex, objIndex, oldBounds, newBounds));
        OnDocumentMutated(pageIndex);
    }

    public void RemovePageObjectUndoable(int pageIndex, int objIndex)
    {
        if (PageCount == 0) return;
        History.Execute(new SnapshotInMemoryMutationCommand(
            _document,
            $"Eliminar objeto #{objIndex} en página {pageIndex + 1}",
            doc => doc.RemovePageObject(pageIndex, objIndex)));
        OnDocumentMutated(pageIndex);
    }

    public void InsertBlankPageAtCurrent(double? widthPt = null, double? heightPt = null)
    {
        double w = widthPt ?? (PageCount > 0 ? Pages[CurrentPageIndex].WidthPt : 595.0);
        double h = heightPt ?? (PageCount > 0 ? Pages[CurrentPageIndex].HeightPt : 842.0);
        int targetIdx = PageCount > 0 ? CurrentPageIndex + 1 : 0;
        History.Execute(new InsertBlankPageCommand(_document, targetIdx, w, h));
        OnDocumentMutated(targetIdx);
    }

    public void CropPage(int pageIndex, PvRect cropBox)
    {
        if (PageCount == 0 || pageIndex < 0 || pageIndex >= PageCount) return;
        History.Execute(new CropPageCommand(_document, pageIndex, cropBox));
        OnDocumentMutated(pageIndex);
    }

    public void FlattenDocument()
    {
        if (PageCount == 0) return;
        int cur = CurrentPageIndex;
        History.Execute(new SnapshotInMemoryMutationCommand(
            _document,
            "Aplanar formularios y anotaciones",
            doc => doc.FlattenAll()));
        RefreshFormFieldsAndSignatures();
        OnDocumentMutated(cur);
    }

    public void AddTextMarkup(int pageIndex, PdfAnnotSubtype subtype, IReadOnlyList<PvRect> quads, uint rgba)
    {
        if (PageCount == 0 || quads.Count == 0) return;
        History.Execute(new AddTextMarkupCommand(_document, pageIndex, subtype, quads, rgba));
        OnDocumentMutated(pageIndex);
    }

    public void UpdateExistingAnnotation(PdfAnnotationInfo existing, PvRect? newRect = null, string? newContents = null, uint? newColor = null)
    {
        if (PageCount == 0) return;
        History.Execute(new UpdateAnnotationCommand(_document, existing, newRect, newContents, newColor));
        OnDocumentMutated(existing.PageIndex);
    }

    public void DeleteExistingAnnotation(PdfAnnotationInfo existing)
    {
        if (PageCount == 0) return;
        int p = existing.PageIndex;
        int idx = existing.Index;
        History.Execute(new SnapshotInMemoryMutationCommand(
            _document,
            $"Eliminar {existing.TypeDisplay} en página {p + 1}",
            doc => doc.RemoveAnnotation(p, idx)));
        OnDocumentMutated(p);
    }

    public void AddAttachmentFromFile(string filePath)
    {
        string name = Path.GetFileName(filePath);
        byte[] bytes = File.ReadAllBytes(filePath);
        int cur = CurrentPageIndex;
        History.Execute(new SnapshotInMemoryMutationCommand(
            _document,
            $"Adjuntar \"{name}\"",
            doc => doc.AddAttachment(name, bytes)));
        OnDocumentMutated(cur);
    }

    public void ExtractAttachmentToFile(int index, string destinationPath)
    {
        _document.ExtractAttachment(index, destinationPath);
    }

    public void DeleteAttachmentAt(int index, string name)
    {
        int cur = CurrentPageIndex;
        History.Execute(new SnapshotInMemoryMutationCommand(
            _document,
            $"Eliminar adjunto \"{name}\"",
            doc => doc.DeleteAttachment(index)));
        OnDocumentMutated(cur);
    }

    public void AddBookmark(string title, int pageIndex, int level = 0)
    {
        var updated = Bookmarks.Select(b => new PdfBookmark(b.Level, b.PageIndex, b.Title)).ToList();
        updated.Add(new PdfBookmark(Math.Max(0, level), Math.Clamp(pageIndex, 0, Math.Max(0, PageCount - 1)), title));
        ApplyBookmarksList(updated, $"Añadir marcador \"{title}\"");
    }

    public void RenameBookmark(BookmarkItemViewModel item, string newTitle)
    {
        int idx = Bookmarks.IndexOf(item);
        if (idx < 0) return;
        var updated = Bookmarks.Select((b, i) =>
            new PdfBookmark(b.Level, b.PageIndex, i == idx ? newTitle : b.Title)).ToList();
        ApplyBookmarksList(updated, $"Renombrar marcador a \"{newTitle}\"");
    }

    public void DeleteBookmark(BookmarkItemViewModel item)
    {
        int idx = Bookmarks.IndexOf(item);
        if (idx < 0) return;
        var updated = Bookmarks
            .Where((_, i) => i != idx)
            .Select(b => new PdfBookmark(b.Level, b.PageIndex, b.Title))
            .ToList();
        ApplyBookmarksList(updated, $"Eliminar marcador \"{item.Title}\"");
    }

    private void ApplyBookmarksList(IReadOnlyList<PdfBookmark> updated, string description)
    {
        int cur = CurrentPageIndex;
        History.Execute(new SnapshotPdfMutationCommand(
            _document,
            description,
            (src, dst, pwd) => QpdfTools.SetBookmarks(src, dst, updated, sourcePassword: pwd)));
        RebuildBookmarksFromDocument();
        OnExternalWorkingCopyMutated(cur);
    }

    public void AddTextObject(string text, double xPt, double yPt, string fontName = "Helvetica", float fontSize = 14f, uint rgba = 0x111111FF)
    {
        if (PageCount == 0) return;
        // Prefer TrueType Unicode font from Windows Fonts when text contains non-ASCII characters (á, é, í, ó, ú, ñ, €, etc.)
        bool hasNonAscii = text.Any(c => c > 127);
        string? windowsTtf = FindWindowsUnicodeFont();
        if (hasNonAscii && windowsTtf is not null)
        {
            History.Execute(new AddUnicodeTextObjectCommand(_document, CurrentPageIndex, text, windowsTtf, fontSize, xPt, yPt, rgba));
            OnDocumentMutated(CurrentPageIndex);
            return;
        }
        History.Execute(new AddTextObjectCommand(_document, CurrentPageIndex, text, fontName, fontSize, xPt, yPt, rgba));
        OnDocumentMutated(CurrentPageIndex);
    }

    public static string? FindWindowsUnicodeFont()
    {
        string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates = ["arial.ttf", "segoeui.ttf", "calibri.ttf", "tahoma.ttf"];
        foreach (string c in candidates)
        {
            string full = Path.Combine(fontsDir, c);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    public void AddImageBgra(byte[] bgraPixels, int widthPx, int heightPx, PvRect destRect)
    {
        if (PageCount == 0) return;
        History.Execute(new AddImageObjectCommand(_document, CurrentPageIndex, bgraPixels, widthPx, heightPx, destRect));
        OnDocumentMutated(CurrentPageIndex);
    }

    /// <summary>
    /// Permanently removes intersecting text/images under the redaction box and draws an opaque black bar.
    /// Intentionally clears Undo history to guarantee redacted data is not retained in memory.
    /// </summary>
    public int ApplyPermanentRedaction(PvRect pdfRect)
    {
        if (PageCount == 0) return 0;
        int removed = _document.ApplyRedaction(CurrentPageIndex, pdfRect, fillRgba: 0x000000FF);
        History.Clear();
        OnDocumentMutated(CurrentPageIndex);
        return removed;
    }

    public void AddInvisibleOcrText(string text, double xPt, double yPt, float fontSize = 12f)
    {
        if (PageCount == 0 || string.IsNullOrWhiteSpace(text)) return;
        AddInvisibleOcrWordsBatch(CurrentPageIndex, [new PdfOcrWord(text, xPt, yPt, fontSize)]);
    }

    public int AddInvisibleOcrWordsBatch(int pageIndex, IReadOnlyList<PdfOcrWord> words)
    {
        if (PageCount == 0 || pageIndex < 0 || pageIndex >= PageCount || words.Count == 0) return 0;
        string? ttf = FindWindowsUnicodeFont();
        int totalAdded = 0;
        History.Execute(new BatchGeneratedObjectsCommand(_document, $"OCR en página {pageIndex + 1}", doc =>
        {
            var (firstIdx, added) = doc.AddInvisibleOcrTextBatch(pageIndex, words, ttf);
            totalAdded = added;
            return added > 0 ? [(pageIndex, firstIdx, added)] : [];
        }));
        OnDocumentMutated(pageIndex);
        return totalAdded;
    }

    public void SanitizeAndExport(string destinationPath)
    {
        string tempSrc = Path.Combine(Path.GetTempPath(), $"pdfvisor_san_src_{Guid.NewGuid():N}.pdf");
        try
        {
            _document.SaveAs(tempSrc, incremental: false);
            QpdfTools.SanitizeMetadata(tempSrc, destinationPath, sourcePassword: _document.ActivePassword);
        }
        finally
        {
            if (File.Exists(tempSrc)) try { File.Delete(tempSrc); } catch { }
        }
    }

    public void RefreshFormFieldsAndSignatures()
    {
        FormFields.Clear();
        try
        {
            string srcPath = _document.ActiveSourcePath;
            if (!string.IsNullOrWhiteSpace(srcPath) && File.Exists(srcPath))
            {
                foreach (var field in QpdfTools.GetFormFields(srcPath, _document.ActivePassword))
                {
                    if (!string.Equals(field.FieldType, "/Sig", StringComparison.Ordinal))
                    {
                        FormFields.Add(field);
                    }
                }
            }
        }
        catch
        {
            // Ignore form inspection errors on non-standard PDFs
        }

        Signatures.Clear();
        try
        {
            foreach (var sig in PadesSignatureService.VerifySignatures(_document))
            {
                Signatures.Add(sig);
            }
        }
        catch
        {
            // Ignore signature inspection errors
        }
    }

    public IReadOnlyList<PdfFormField> GetFormFieldsForPage(int pageIndex) =>
        FormFields
            .Where(f => f.PageIndex == pageIndex && f.Rect.Right > f.Rect.Left && f.Rect.Top > f.Rect.Bottom)
            .ToArray();

    public void AddTextFormField(string fieldName, PvRect pdfRect, string defaultValue = "", int? targetPageIndex = null)
    {
        if (PageCount == 0) return;
        int pageIdx = targetPageIndex is int p ? Math.Clamp(p, 0, PageCount - 1) : CurrentPageIndex;
        History.Execute(new SnapshotPdfMutationCommand(
            _document,
            $"Añadir campo \"{fieldName}\"",
            (src, dst, pwd) => QpdfTools.AddTextField(src, dst, pageIdx, fieldName, pdfRect, defaultValue, sourcePassword: pwd)));
        OnExternalWorkingCopyMutated(pageIdx);
    }

    public void MoveOrResizeFormField(string fieldName, PvRect newPdfRect, int? targetPageIndex = null)
    {
        if (PageCount == 0) return;
        int pageIdx = targetPageIndex is int p ? Math.Clamp(p, 0, PageCount - 1) : CurrentPageIndex;
        History.Execute(new SnapshotPdfMutationCommand(
            _document,
            $"Mover/redimensionar campo \"{fieldName}\"",
            (src, dst, pwd) => QpdfTools.SetFormFieldRect(src, dst, fieldName, newPdfRect, sourcePassword: pwd)));
        OnExternalWorkingCopyMutated(pageIdx);
    }

    public void RemoveFormField(string fieldName)
    {
        if (PageCount == 0) return;
        int pageIdx = CurrentPageIndex;
        History.Execute(new SnapshotPdfMutationCommand(
            _document,
            $"Eliminar campo \"{fieldName}\"",
            (src, dst, pwd) => QpdfTools.RemoveFormField(src, dst, fieldName, sourcePassword: pwd)));
        OnExternalWorkingCopyMutated(pageIdx);
    }

    public void SetFormFieldValue(string fieldName, string newValue)
    {
        int pageIdx = CurrentPageIndex;
        History.Execute(new SnapshotPdfMutationCommand(
            _document,
            $"Rellenar campo \"{fieldName}\"",
            (src, dst, pwd) => QpdfTools.SetFormFieldValue(src, dst, fieldName, newValue, sourcePassword: pwd)));
        OnExternalWorkingCopyMutated(pageIdx);
    }

    public void AddCheckboxFormField(string fieldName, PvRect pdfRect, bool initiallyChecked = false, int? targetPageIndex = null)
    {
        if (PageCount == 0) return;
        int pageIdx = targetPageIndex is int p ? Math.Clamp(p, 0, PageCount - 1) : CurrentPageIndex;
        History.Execute(new SnapshotPdfMutationCommand(
            _document,
            $"Añadir casilla \"{fieldName}\"",
            (src, dst, pwd) => QpdfTools.AddCheckboxField(src, dst, pageIdx, fieldName, pdfRect, initiallyChecked, sourcePassword: pwd)));
        OnExternalWorkingCopyMutated(pageIdx);
    }

    public void DecryptAndExport(string destinationPath)
    {
        string tempSrc = Path.Combine(Path.GetTempPath(), $"pdfvisor_dec_src_{Guid.NewGuid():N}.pdf");
        try
        {
            _document.SaveAs(tempSrc, incremental: false);
            QpdfTools.Decrypt(tempSrc, destinationPath, sourcePassword: _document.ActivePassword);
        }
        finally
        {
            if (File.Exists(tempSrc)) try { File.Delete(tempSrc); } catch { }
        }
    }

    public void SignPadesAndExport(
        string destinationPath,
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
        string? reason = "Firmado digitalmente con PDFVisor",
        string? location = null,
        string? tsaUrl = null,
        PvRect? customWidgetRect = null,
        (byte[] BgraPixels, int WidthPx, int HeightPx)? stampImage = null)
    {
        bool hasPriorSignatures = Signatures.Count > 0 && !IsModified && stampImage is null && File.Exists(_document.ActiveSourcePath);
        string? tempSrc = null;
        try
        {
            string sourceForSigning;
            if (hasPriorSignatures)
            {
                // Preserve original signed bytes verbatim so incremental co-signing keeps prior signatures valid!
                sourceForSigning = _document.ActiveSourcePath;
            }
            else
            {
                tempSrc = Path.Combine(Path.GetTempPath(), $"pdfvisor_sig_src_{Guid.NewGuid():N}.pdf");
                _document.SaveAs(tempSrc, incremental: false);
                sourceForSigning = tempSrc;
            }

            // Offset subsequent default signature stamps horizontally so co-signatures do not overlap visually
            int offsetIdx = Signatures.Count % 3;
            double left = 36 + offsetIdx * 182;
            var sigRect = customWidgetRect ?? new PvRect { Left = left, Bottom = 36, Right = left + 174, Top = 86 };
            string? srcPwd = _document.ActivePassword;

            PadesSignatureService.SignPdfPades(
                sourceForSigning,
                destinationPath,
                certificate,
                CurrentPageIndex,
                sigRect,
                reason,
                location,
                sourcePassword: srcPwd,
                tsaUrl: tsaUrl,
                stampImage: stampImage);
        }
        finally
        {
            if (tempSrc is not null && File.Exists(tempSrc)) try { File.Delete(tempSrc); } catch { }
        }
    }

    private void OnExternalWorkingCopyMutated(int pageIdx)
    {
        IsModified = true;
        RebuildPageList();
        RebuildBookmarksFromDocument();
        _currentPageIndex = Math.Clamp(pageIdx, 0, Math.Max(0, PageCount - 1));
        RefreshAnnotationsAndAttachments();
        RefreshFormFieldsAndSignatures();
        RefreshCurrentPageObjects();
        DocumentRevision++;
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(CurrentPageIndex));
        OnPropertyChanged(nameof(CurrentPageNumber));
        OnPropertyChanged(nameof(CurrentPageSizeText));
    }

    public PdfDocument Document => _document;

    public void RotateCurrentPagePermanent(int deltaQuarterTurns = 1)
    {
        if (PageCount == 0) return;
        History.Execute(new RotatePageCommand(_document, CurrentPageIndex, deltaQuarterTurns));
        OnDocumentMutated(CurrentPageIndex);
    }

    public void MoveCurrentPage(int targetIndex)
    {
        MovePage(CurrentPageIndex, targetIndex);
    }

    public void MovePage(int fromIndex, int toIndex)
    {
        if (PageCount <= 1) return;
        int src = Math.Clamp(fromIndex, 0, PageCount - 1);
        int dst = Math.Clamp(toIndex, 0, PageCount - 1);
        if (src == dst) return;
        History.Execute(new MovePageCommand(_document, src, dst));
        OnDocumentMutated(dst);
    }

    public IReadOnlyList<string> SplitIntoFiles(IReadOnlyList<int[]> pageGroups, string outputDirectory, string baseName)
    {
        Directory.CreateDirectory(outputDirectory);
        var created = new List<string>(pageGroups.Count);
        for (int i = 0; i < pageGroups.Count; i++)
        {
            string outPath = Path.Combine(outputDirectory, $"{baseName}_parte_{i + 1:D2}.pdf");
            _document.ExtractPages(pageGroups[i], outPath);
            created.Add(outPath);
        }
        return created;
    }

    public void ApplyWatermarkToAllPages(string text, byte opacity = 55, float fontSize = 54f, double angleDegrees = 45.0)
    {
        if (PageCount == 0 || string.IsNullOrWhiteSpace(text)) return;
        uint rgba = (0x80808000u) | opacity;
        string? ttf = text.Any(c => c > 127) ? FindWindowsUnicodeFont() : null;
        History.Execute(new BatchGeneratedObjectsCommand(_document, $"Marca de agua \"{text}\"", doc =>
        {
            var inserted = new List<(int PageIndex, int FirstObjIndex, int Count)>(doc.PageCount);
            for (int i = 0; i < doc.PageCount; i++)
            {
                var size = doc.GetPageSize(i);
                double x = size.WidthPt * 0.18;
                double y = size.HeightPt * 0.30;
                int objIdx = doc.AddRotatedUnicodeText(i, text, ttf, fontSize, x, y, angleDegrees, rgba);
                inserted.Add((i, objIdx, 1));
            }
            return inserted;
        }));
        OnDocumentMutated(CurrentPageIndex);
    }

    public async Task ApplyWatermarkToAllPagesAsync(
        string text,
        byte opacity = 55,
        float fontSize = 54f,
        double angleDegrees = 45.0,
        IProgress<(int Current, int Total)>? progress = null)
    {
        if (PageCount == 0 || string.IsNullOrWhiteSpace(text)) return;
        int total = PageCount;
        uint rgba = (0x80808000u) | opacity;
        string? ttf = text.Any(c => c > 127) ? FindWindowsUnicodeFont() : null;
        await Task.Run(() =>
        {
            History.Execute(new BatchGeneratedObjectsCommand(_document, $"Marca de agua \"{text}\"", doc =>
            {
                var inserted = new List<(int PageIndex, int FirstObjIndex, int Count)>(total);
                for (int i = 0; i < total; i++)
                {
                    var size = doc.GetPageSize(i);
                    double x = size.WidthPt * 0.18;
                    double y = size.HeightPt * 0.30;
                    int objIdx = doc.AddRotatedUnicodeText(i, text, ttf, fontSize, x, y, angleDegrees, rgba);
                    inserted.Add((i, objIdx, 1));
                    progress?.Report((i + 1, total));
                }
                return inserted;
            }));
        });
        OnDocumentMutated(CurrentPageIndex);
    }

    public void ApplyPageNumbersToAllPages(string format = "Página {page} de {total}", float fontSize = 10f)
    {
        if (PageCount == 0) return;
        int total = PageCount;
        string? ttf = FindWindowsUnicodeFont();
        History.Execute(new BatchGeneratedObjectsCommand(_document, "Numeración de páginas", doc =>
        {
            var inserted = new List<(int PageIndex, int FirstObjIndex, int Count)>(total);
            for (int i = 0; i < total; i++)
            {
                var size = doc.GetPageSize(i);
                string label = format
                    .Replace("{page}", (i + 1).ToString())
                    .Replace("{total}", total.ToString());
                double x = Math.Max(36, (size.WidthPt - label.Length * fontSize * 0.5) * 0.5);
                double y = 24.0;
                bool nonAscii = label.Any(c => c > 127);
                int objIdx = (nonAscii && ttf is not null)
                    ? doc.AddUnicodeTextObject(i, label, ttf, fontSize, x, y, 0x444444FF)
                    : doc.AddTextObject(i, label, "Helvetica", fontSize, x, y, 0x444444FF);
                inserted.Add((i, objIdx, 1));
            }
            return inserted;
        }));
        OnDocumentMutated(CurrentPageIndex);
    }

    public async Task ApplyPageNumbersToAllPagesAsync(
        string format = "Página {page} de {total}",
        float fontSize = 10f,
        IProgress<(int Current, int Total)>? progress = null)
    {
        if (PageCount == 0) return;
        int total = PageCount;
        string? ttf = FindWindowsUnicodeFont();
        await Task.Run(() =>
        {
            History.Execute(new BatchGeneratedObjectsCommand(_document, "Numeración de páginas", doc =>
            {
                var inserted = new List<(int PageIndex, int FirstObjIndex, int Count)>(total);
                for (int i = 0; i < total; i++)
                {
                    var size = doc.GetPageSize(i);
                    string label = format
                        .Replace("{page}", (i + 1).ToString())
                        .Replace("{total}", total.ToString());
                    double x = Math.Max(36, (size.WidthPt - label.Length * fontSize * 0.5) * 0.5);
                    double y = 24.0;
                    bool nonAscii = label.Any(c => c > 127);
                    int objIdx = (nonAscii && ttf is not null)
                        ? doc.AddUnicodeTextObject(i, label, ttf, fontSize, x, y, 0x444444FF)
                        : doc.AddTextObject(i, label, "Helvetica", fontSize, x, y, 0x444444FF);
                    inserted.Add((i, objIdx, 1));
                    progress?.Report((i + 1, total));
                }
                return inserted;
            }));
        });
        OnDocumentMutated(CurrentPageIndex);
    }

    public PdfMetadata GetMetadata()
    {
        return QpdfTools.GetMetadata(_document.ActiveSourcePath, _document.ActivePassword);
    }

    public void UpdateMetadata(string title, string author, string subject, string keywords)
    {
        int pageIdx = CurrentPageIndex;
        History.Execute(new SnapshotPdfMutationCommand(
            _document,
            "Editar metadatos",
            (src, dst, pwd) => QpdfTools.SetMetadata(src, dst, title, author, subject, keywords, sourcePassword: pwd)));
        OnExternalWorkingCopyMutated(pageIdx);
    }

    public async Task UpdateMetadataAsync(string title, string author, string subject, string keywords)
    {
        int pageIdx = CurrentPageIndex;
        await Task.Run(() =>
        {
            History.Execute(new SnapshotPdfMutationCommand(
                _document,
                "Editar metadatos",
                (src, dst, pwd) => QpdfTools.SetMetadata(src, dst, title, author, subject, keywords, sourcePassword: pwd)));
        });
        OnExternalWorkingCopyMutated(pageIdx);
    }

    public void DeleteCurrentPage()
    {
        if (PageCount <= 1) return;
        int idx = CurrentPageIndex;
        History.Execute(new DeletePageCommand(_document, idx));
        OnDocumentMutated(Math.Min(idx, PageCount - 1));
    }

    public void ImportPagesFromFile(string sourcePdfPath, int? insertAtIndex = null)
    {
        int target = insertAtIndex ?? PageCount;
        History.Execute(new ImportPagesCommand(_document, sourcePdfPath, target));
        OnDocumentMutated(target);
    }

    public void ExtractCurrentPage(string destinationPdfPath)
    {
        if (PageCount == 0) return;
        _document.ExtractPages([CurrentPageIndex], destinationPdfPath);
    }

    public void AddHighlight(PvRect pdfRect, uint rgba = 0xFFEA0080)
    {
        if (PageCount == 0) return;
        History.Execute(new AddHighlightCommand(_document, CurrentPageIndex, pdfRect, rgba));
        OnDocumentMutated(CurrentPageIndex);
    }

    public void AddSquare(PvRect pdfRect, uint strokeRgba = 0xE53935FF, uint fillRgba = 0x00000000)
    {
        if (PageCount == 0) return;
        History.Execute(new AddSquareCommand(_document, CurrentPageIndex, pdfRect, strokeRgba, fillRgba));
        OnDocumentMutated(CurrentPageIndex);
    }

    public void AddNote(PvRect pdfRect, string contents, uint rgba = 0xFFC107FF)
    {
        if (PageCount == 0) return;
        History.Execute(new AddNoteCommand(_document, CurrentPageIndex, pdfRect, contents, rgba));
        OnDocumentMutated(CurrentPageIndex);
    }

    public void AddInk(PvPoint[] pdfPoints, uint rgba = 0x1E88E5FF)
    {
        if (PageCount == 0 || pdfPoints.Length < 2) return;
        History.Execute(new AddInkCommand(_document, CurrentPageIndex, pdfPoints, rgba));
        OnDocumentMutated(CurrentPageIndex);
    }

    public void OptimizeAndExport(string destinationPath, bool linearize = true, bool recompress = true, bool objectStreams = true)
    {
        string tempSrc = Path.Combine(Path.GetTempPath(), $"pdfvisor_opt_src_{Guid.NewGuid():N}.pdf");
        try
        {
            _document.SaveAs(tempSrc, incremental: false);
            QpdfTools.Optimize(tempSrc, destinationPath, linearize, recompress, objectStreams, sourcePassword: _document.ActivePassword);
        }
        finally
        {
            if (File.Exists(tempSrc)) try { File.Delete(tempSrc); } catch { }
        }
    }

    public void EncryptAes256AndExport(string destinationPath, string userPassword, string ownerPassword, bool allowPrint = true, bool allowModify = false, bool allowExtract = false)
    {
        string tempSrc = Path.Combine(Path.GetTempPath(), $"pdfvisor_enc_src_{Guid.NewGuid():N}.pdf");
        try
        {
            _document.SaveAs(tempSrc, incremental: false);
            QpdfTools.EncryptAes256(tempSrc, destinationPath, userPassword, ownerPassword, allowPrint, allowModify, allowExtract, sourcePassword: _document.ActivePassword);
        }
        finally
        {
            if (File.Exists(tempSrc)) try { File.Delete(tempSrc); } catch { }
        }
    }

    public void Undo()
    {
        if (!History.CanUndo) return;
        History.Undo();
        RefreshFormFieldsAndSignatures();
        OnDocumentMutated(Math.Clamp(CurrentPageIndex, 0, Math.Max(0, PageCount - 1)));
    }

    public void Redo()
    {
        if (!History.CanRedo) return;
        History.Redo();
        RefreshFormFieldsAndSignatures();
        OnDocumentMutated(Math.Clamp(CurrentPageIndex, 0, Math.Max(0, PageCount - 1)));
    }

    public void Save()
    {
        _document.SaveInPlace(incremental: false);
        IsModified = false;
        RefreshFormFieldsAndSignatures();
    }

    public void SaveAs(string destinationPath)
    {
        _document.SaveAs(destinationPath, incremental: false);
        if (string.Equals(Path.GetFullPath(destinationPath), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
        {
            IsModified = false;
        }
    }

    public void NotifyExternalBatchMutated(int pageIndex)
    {
        OnDocumentMutated(pageIndex);
    }

    private void OnDocumentMutated(int newCurrentPage)
    {
        IsModified = true;
        RebuildPageList();
        RebuildBookmarksFromDocument();
        _currentPageIndex = Math.Clamp(newCurrentPage, 0, Math.Max(0, PageCount - 1));
        RefreshAnnotationsAndAttachments();
        RefreshCurrentPageObjects();
        _searchMatches = [];
        _activeMatchIndex = -1;
        DocumentRevision++;
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(CurrentPageIndex));
        OnPropertyChanged(nameof(CurrentPageNumber));
        OnPropertyChanged(nameof(CurrentPageSizeText));
        NotifySearchChanged();
    }

    private void RebuildPageList()
    {
        Pages.Clear();
        int count = _document.PageCount;
        for (int i = 0; i < count; i++)
        {
            var size = _document.GetPageSize(i);
            Pages.Add(new PageItemViewModel(i, size.WidthPt, size.HeightPt));
        }
    }

    public async Task<int> SearchAsync(string query, bool? matchCase = null, bool? wholeWord = null, CancellationToken ct = default)
    {
        SearchQuery = query;
        bool mc = matchCase ?? SearchMatchCase;
        bool ww = wholeWord ?? SearchWholeWord;
        SearchMatchCase = mc;
        SearchWholeWord = ww;

        if (string.IsNullOrWhiteSpace(query))
        {
            _searchMatches = [];
            ActiveMatchIndex = -1;
            NotifySearchChanged();
            return 0;
        }

        var found = await Task.Run(() =>
        {
            var list = new List<SearchMatch>();
            for (int p = 0; p < PageCount; p++)
            {
                ct.ThrowIfCancellationRequested();
                var rects = _document.SearchPage(p, query, mc, ww);
                foreach (var r in rects)
                {
                    list.Add(new SearchMatch(p, r));
                }
            }
            return list;
        }, ct);

        _searchMatches = found;
        if (_searchMatches.Count > 0)
        {
            int idx = _searchMatches.FindIndex(m => m.PageIndex >= CurrentPageIndex);
            if (idx < 0) idx = 0;
            ActiveMatchIndex = idx;
            CurrentPageIndex = _searchMatches[idx].PageIndex;
        }
        else
        {
            ActiveMatchIndex = -1;
        }

        NotifySearchChanged();
        return _searchMatches.Count;
    }

    public void NextSearchMatch()
    {
        if (_searchMatches.Count == 0) return;
        ActiveMatchIndex = (ActiveMatchIndex + 1) % _searchMatches.Count;
        CurrentPageIndex = _searchMatches[ActiveMatchIndex].PageIndex;
        OnPropertyChanged(nameof(CurrentPageHighlights));
        OnPropertyChanged(nameof(ActiveMatchOnCurrentPage));
    }

    public void PreviousSearchMatch()
    {
        if (_searchMatches.Count == 0) return;
        ActiveMatchIndex = (ActiveMatchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
        CurrentPageIndex = _searchMatches[ActiveMatchIndex].PageIndex;
        OnPropertyChanged(nameof(CurrentPageHighlights));
        OnPropertyChanged(nameof(ActiveMatchOnCurrentPage));
    }

    private void NotifySearchChanged()
    {
        OnPropertyChanged(nameof(TotalSearchMatches));
        OnPropertyChanged(nameof(SearchStatusText));
        OnPropertyChanged(nameof(CurrentPageHighlights));
        OnPropertyChanged(nameof(ActiveMatchOnCurrentPage));
    }

    public void Dispose()
    {
        History.Dispose();
        _document.Dispose();
        RenderGate.Dispose();
    }
}
