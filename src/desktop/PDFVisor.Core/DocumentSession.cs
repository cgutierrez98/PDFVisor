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

public sealed class BookmarkItemViewModel
{
    public int Level { get; }
    public int PageIndex { get; }
    public string Title { get; }
    public string DisplayIndent => new string(' ', Math.Clamp(Level, 0, 8) * 3) + Title;

    public BookmarkItemViewModel(PdfBookmark bookmark)
    {
        Level = bookmark.Level;
        PageIndex = bookmark.PageIndex;
        Title = bookmark.Title;
    }
}

public sealed record SearchMatch(int PageIndex, PvRect Rect);

public enum AnnotationTool
{
    None,
    Highlight,
    Square,
    Note,
    Ink,
    AddText,
    Redact
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
    private AnnotationTool _activeTool = AnnotationTool.None;
    private string _searchQuery = string.Empty;
    private List<SearchMatch> _searchMatches = [];
    private int _activeMatchIndex = -1;

    public string FilePath => _document.FilePath;
    public string FileName => Path.GetFileName(_document.FilePath);
    public string DisplayTabTitle => IsModified ? $"{FileName} *" : FileName;
    public int PageCount => _document.PageCount;
    public ObservableCollection<PageItemViewModel> Pages { get; } = [];
    public ObservableCollection<BookmarkItemViewModel> Bookmarks { get; } = [];
    public ObservableCollection<PdfFormField> FormFields { get; } = [];
    public ObservableCollection<PdfSignatureVerificationResult> Signatures { get; } = [];
    public ObservableCollection<PdfPageObjectInfo> CurrentPageObjects { get; } = [];
    public CommandHistory History { get; } = new();

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

    public string SearchQuery
    {
        get => _searchQuery;
        private set => SetProperty(ref _searchQuery, value);
    }

    public int TotalSearchMatches => _searchMatches.Count;

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
        foreach (var bm in document.GetBookmarks())
        {
            Bookmarks.Add(new BookmarkItemViewModel(bm));
        }
        RefreshFormFieldsAndSignatures();
        RefreshCurrentPageObjects();
    }

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

    public void AddTextObject(string text, double xPt, double yPt, string fontName = "Helvetica", float fontSize = 14f, uint rgba = 0x111111FF)
    {
        if (PageCount == 0) return;
        // Prefer TrueType Unicode font from Windows Fonts when text contains non-ASCII characters (á, é, í, ó, ú, ñ, €, etc.)
        bool hasNonAscii = text.Any(c => c > 127);
        string? windowsTtf = FindWindowsUnicodeFont();
        if (hasNonAscii && windowsTtf is not null)
        {
            _document.AddUnicodeTextObject(CurrentPageIndex, text, windowsTtf, fontSize, xPt, yPt, rgba);
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
        _document.AddInvisibleOcrText(CurrentPageIndex, text, xPt, yPt, fontSize);
        OnDocumentMutated(CurrentPageIndex);
    }

    public void SanitizeAndExport(string destinationPath)
    {
        string tempSrc = Path.Combine(Path.GetTempPath(), $"pdfvisor_san_src_{Guid.NewGuid():N}.pdf");
        try
        {
            _document.SaveAs(tempSrc, incremental: false);
            QpdfTools.SanitizeMetadata(tempSrc, destinationPath);
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
            foreach (var field in QpdfTools.GetFormFields(_document.FilePath, _document.Password))
            {
                if (!string.Equals(field.FieldType, "/Sig", StringComparison.Ordinal))
                {
                    FormFields.Add(field);
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

    public void AddTextFormField(string fieldName, PvRect pdfRect, string defaultValue = "")
    {
        if (PageCount == 0) return;
        string tempSrc = Path.Combine(Path.GetTempPath(), $"pdfvisor_form_src_{Guid.NewGuid():N}.pdf");
        string tempOut = Path.Combine(Path.GetTempPath(), $"pdfvisor_form_out_{Guid.NewGuid():N}.pdf");
        try
        {
            _document.SaveAs(tempSrc, incremental: false);
            QpdfTools.AddTextField(tempSrc, tempOut, CurrentPageIndex, fieldName, pdfRect, defaultValue);
            ApplyExternalPdfAndReload(tempOut);
        }
        finally
        {
            if (File.Exists(tempSrc)) try { File.Delete(tempSrc); } catch { }
            if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { }
        }
    }

    public void SetFormFieldValue(string fieldName, string newValue)
    {
        string tempSrc = Path.Combine(Path.GetTempPath(), $"pdfvisor_form_src_{Guid.NewGuid():N}.pdf");
        string tempOut = Path.Combine(Path.GetTempPath(), $"pdfvisor_form_out_{Guid.NewGuid():N}.pdf");
        try
        {
            _document.SaveAs(tempSrc, incremental: false);
            QpdfTools.SetFormFieldValue(tempSrc, tempOut, fieldName, newValue);
            ApplyExternalPdfAndReload(tempOut);
        }
        finally
        {
            if (File.Exists(tempSrc)) try { File.Delete(tempSrc); } catch { }
            if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { }
        }
    }

    public void SignPadesAndExport(
        string destinationPath,
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
        string? reason = "Firmado digitalmente con PDFVisor",
        string? location = null,
        string? tsaUrl = null)
    {
        string tempSrc = Path.Combine(Path.GetTempPath(), $"pdfvisor_sig_src_{Guid.NewGuid():N}.pdf");
        try
        {
            _document.SaveAs(tempSrc, incremental: false);
            var sigRect = new PvRect { Left = 36, Bottom = 36, Right = 240, Top = 86 };
            PadesSignatureService.SignPdfPades(tempSrc, destinationPath, certificate, CurrentPageIndex, sigRect, reason, location, tsaUrl: tsaUrl);
        }
        finally
        {
            if (File.Exists(tempSrc)) try { File.Delete(tempSrc); } catch { }
        }
    }

    private void ApplyExternalPdfAndReload(string updatedPdfPath)
    {
        _document.ReplaceFileAndReload(updatedPdfPath);
        IsModified = false;
        RebuildPageList();
        RefreshFormFieldsAndSignatures();
        DocumentRevision++;
        OnPropertyChanged(nameof(PageCount));
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
        if (PageCount <= 1) return;
        int clamped = Math.Clamp(targetIndex, 0, PageCount - 1);
        if (clamped == CurrentPageIndex) return;
        History.Execute(new MovePageCommand(_document, CurrentPageIndex, clamped));
        OnDocumentMutated(clamped);
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
            QpdfTools.Optimize(tempSrc, destinationPath, linearize, recompress, objectStreams);
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
            QpdfTools.EncryptAes256(tempSrc, destinationPath, userPassword, ownerPassword, allowPrint, allowModify, allowExtract);
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
        OnDocumentMutated(Math.Clamp(CurrentPageIndex, 0, Math.Max(0, PageCount - 1)));
    }

    public void Redo()
    {
        if (!History.CanRedo) return;
        History.Redo();
        OnDocumentMutated(Math.Clamp(CurrentPageIndex, 0, Math.Max(0, PageCount - 1)));
    }

    public void Save()
    {
        _document.SaveInPlace(incremental: false);
        IsModified = false;
    }

    public void SaveAs(string destinationPath)
    {
        _document.SaveAs(destinationPath, incremental: false);
        if (string.Equals(Path.GetFullPath(destinationPath), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
        {
            IsModified = false;
        }
    }

    private void OnDocumentMutated(int newCurrentPage)
    {
        IsModified = true;
        RebuildPageList();
        _currentPageIndex = Math.Clamp(newCurrentPage, 0, Math.Max(0, PageCount - 1));
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

    public async Task<int> SearchAsync(string query, bool matchCase = false, bool wholeWord = false, CancellationToken ct = default)
    {
        SearchQuery = query;
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
                var rects = _document.SearchPage(p, query, matchCase, wholeWord);
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
    }
}
