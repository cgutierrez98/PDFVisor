using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PDFVisor.Core;
using PDFVisor.Interop;

namespace PDFVisor.Viewport;

/// <summary>
/// Virtualized, continuous multi-page High-DPI WPF viewport with 512x512 px tile-based asynchronous rendering,
/// an O(1) memory-budgeted LRU tile cache (192 MB), binary-search page visibility for thousands of pages,
/// inline AcroForm editing, text-snapped QuadPoints markup, interactive annotation/image manipulation,
/// and per-page 60-120Hz vector overlays.
/// </summary>
public sealed class PdfPageViewport : ContentControl
{
    private const double PageGapDip = 18.0;
    private const double OuterPaddingDip = 24.0;
    private const double PreloadMarginDip = 450.0;
    private const int TileSizePx = 512;

    private readonly struct PageLayoutSlot
    {
        public int PageIndex { get; init; }
        public double LeftDip { get; init; }
        public double TopDip { get; init; }
        public double WidthDip { get; init; }
        public double HeightDip { get; init; }
        public double BottomDip => TopDip + HeightDip;
    }

    private sealed class ActivePageVisual
    {
        public int PageIndex { get; init; }
        public Border Border { get; init; } = null!;
        public Grid SurfaceGrid { get; init; } = null!;
        public Canvas TileCanvas { get; init; } = null!;
        public Canvas OverlayCanvas { get; init; } = null!;
        public Canvas InteractiveCanvas { get; init; } = null!;
        public Dictionary<TileKey, Image> MountedTiles { get; } = new();
    }

    private enum DragTargetKind
    {
        None,
        FormField,
        Annotation,
        ImageObject
    }

    private readonly ScrollViewer _scrollViewer;
    private readonly Canvas _documentCanvas;
    private readonly LruTileCache<TileKey, BitmapSource> _tileCache = new(192L * 1024 * 1024);
    private readonly Dictionary<int, ActivePageVisual> _activePages = new();
    private readonly HashSet<TileKey> _inFlightTiles = new();

    private PageLayoutSlot[] _layoutSlots = [];
    private double _maxPageWidthDip;
    private double _totalDocumentHeightDip;
    private CancellationTokenSource? _renderCts;
    private bool _isSyncingScrollToSession;

    // Panning state (Hand tool, Middle-Mouse drag, or Spacebar hold)
    private bool _isPanning;
    private bool _isSpaceHeld;
    private Point _panStartMouse;
    private double _panStartOffsetH;
    private double _panStartOffsetV;

    // Interactive drawing & text selection state
    private bool _isDrawing;
    private ActivePageVisual? _drawingPage;
    private Point _dragStartDip;
    private Rectangle? _previewRect;
    private Polyline? _previewInk;
    private readonly List<Point> _inkPointsDip = [];

    private bool _isSelectingText;
    private int _selPageIndex = -1;
    private int _selAnchorChar = -1;
    private int _selStartChar = -1;
    private int _selCharCount;
    private IReadOnlyList<PvRect> _selRects = Array.Empty<PvRect>();

    // Unified object dragging / resizing (AcroForm fields, Annotations, Image objects)
    private DragTargetKind _dragKind = DragTargetKind.None;
    private bool _isResizingObject;
    private PdfFormField? _draggedFormField;
    private PdfAnnotationInfo? _draggedAnnotation;
    private PdfPageObjectInfo? _draggedImage;
    private Rect _draggedObjectStartDip;
    private Rectangle? _objectDragPreview;

    // Selected canvas object (for visual highlight + Delete key)
    private PdfFormField? _selectedFormField;
    private PdfAnnotationInfo? _selectedAnnotation;
    private (int PageIndex, PdfPageObjectInfo Obj)? _selectedImage;

    // Inline floating TextBox for direct AcroForm /Tx editing
    private TextBox? _inlineFieldEditor;
    private PdfFormField? _editingFormField;
    private int _editingFieldPageIndex = -1;
    private bool _isCommittingInlineEditor;

    private static readonly Brush TextSelectionBrush = new SolidColorBrush(Color.FromArgb(95, 0, 120, 215));
    private static readonly Brush MatchFillBrush = new SolidColorBrush(Color.FromArgb(95, 255, 215, 0));
    private static readonly Brush ActiveMatchFillBrush = new SolidColorBrush(Color.FromArgb(145, 255, 140, 0));
    private static readonly Brush ActiveMatchStrokeBrush = new SolidColorBrush(Color.FromArgb(220, 210, 90, 0));
    private static readonly Brush HighlightPreviewBrush = new SolidColorBrush(Color.FromArgb(110, 255, 234, 0));
    private static readonly Brush UnderlinePreviewBrush = new SolidColorBrush(Color.FromArgb(90, 30, 136, 229));
    private static readonly Brush StrikeOutPreviewBrush = new SolidColorBrush(Color.FromArgb(90, 229, 57, 53));
    private static readonly Brush SquarePreviewStroke = new SolidColorBrush(Color.FromRgb(229, 57, 53));
    private static readonly Brush InkPreviewStroke = new SolidColorBrush(Color.FromRgb(30, 136, 229));
    private static readonly Brush RedactPreviewFill = new SolidColorBrush(Color.FromArgb(140, 20, 20, 20));
    private static readonly Brush RedactPreviewStroke = new SolidColorBrush(Color.FromRgb(220, 38, 38));
    private static readonly Brush SignPreviewFill = new SolidColorBrush(Color.FromArgb(65, 25, 118, 210));
    private static readonly Brush SignPreviewStroke = new SolidColorBrush(Color.FromRgb(21, 101, 192));
    private static readonly Brush CropPreviewFill = new SolidColorBrush(Color.FromArgb(45, 46, 125, 50));
    private static readonly Brush CropPreviewStroke = new SolidColorBrush(Color.FromRgb(46, 125, 50));
    private static readonly Brush FormFieldOutlineBrush = new SolidColorBrush(Color.FromArgb(145, 25, 118, 210));
    private static readonly Brush FormFieldHandleBrush = new SolidColorBrush(Color.FromRgb(25, 118, 210));
    private static readonly Brush NoteBadgeFillBrush = new SolidColorBrush(Color.FromArgb(75, 255, 193, 7));
    private static readonly Brush NoteBadgeStrokeBrush = new SolidColorBrush(Color.FromArgb(210, 245, 127, 23));
    private static readonly Brush SelectedObjectStrokeBrush = new SolidColorBrush(Color.FromRgb(0, 150, 136));
    private static readonly Brush PageBorderBrush = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));

    private Point _lastHoverProbeDip = new(-999, -999);

    static PdfPageViewport()
    {
        TextSelectionBrush.Freeze();
        MatchFillBrush.Freeze();
        ActiveMatchFillBrush.Freeze();
        ActiveMatchStrokeBrush.Freeze();
        HighlightPreviewBrush.Freeze();
        UnderlinePreviewBrush.Freeze();
        StrikeOutPreviewBrush.Freeze();
        SquarePreviewStroke.Freeze();
        InkPreviewStroke.Freeze();
        RedactPreviewFill.Freeze();
        RedactPreviewStroke.Freeze();
        SignPreviewFill.Freeze();
        SignPreviewStroke.Freeze();
        CropPreviewFill.Freeze();
        CropPreviewStroke.Freeze();
        FormFieldOutlineBrush.Freeze();
        FormFieldHandleBrush.Freeze();
        NoteBadgeFillBrush.Freeze();
        NoteBadgeStrokeBrush.Freeze();
        SelectedObjectStrokeBrush.Freeze();
        PageBorderBrush.Freeze();
    }

    public static readonly DependencyProperty SessionProperty =
        DependencyProperty.Register(
            nameof(Session),
            typeof(DocumentSession),
            typeof(PdfPageViewport),
            new PropertyMetadata(null, OnSessionChanged));

    public DocumentSession? Session
    {
        get => (DocumentSession?)GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public Func<string?>? PromptNoteText { get; set; }
    public Func<PdfAnnotationInfo, string?>? PromptEditAnnotation { get; set; }
    public Func<string?>? PromptAddText { get; set; }
    public Func<string?>? PromptFormFieldName { get; set; }
    public Action<PdfFormField>? FormFieldDoubleClicked { get; set; }
    public Action<string>? ExternalUriRequested { get; set; }
    public Action<int, PvRect>? SignatureRegionSelected { get; set; }
    public Action? ToolAutoReset { get; set; }

    public bool HasTextSelection => _selPageIndex >= 0 && _selRects.Count > 0;
    public bool IsEditingInlineField => _inlineFieldEditor is not null && _inlineFieldEditor.IsKeyboardFocusWithin;

    public PdfPageViewport()
    {
        _documentCanvas = new Canvas
        {
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(42, 44, 48)),
            Content = _documentCanvas
        };
        _scrollViewer.PreviewMouseWheel += ScrollViewer_PreviewMouseWheel;
        _scrollViewer.PreviewMouseDown += ScrollViewer_PreviewMouseDown;
        _scrollViewer.PreviewMouseMove += ScrollViewer_PreviewMouseMove;
        _scrollViewer.PreviewMouseUp += ScrollViewer_PreviewMouseUp;
        _scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
        _scrollViewer.SizeChanged += (_, _) => UpdateVisiblePages();

        Content = _scrollViewer;
    }

    public void SetSpacePanning(bool spaceHeld)
    {
        if (_isSpaceHeld == spaceHeld) return;
        _isSpaceHeld = spaceHeld;
        UpdateAllPageCursors();
    }

    private void ScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Session is null) return;
        if (_inlineFieldEditor is not null && _inlineFieldEditor.IsMouseOver) return;

        bool middlePan = e.ChangedButton == MouseButton.Middle;
        bool handPan = e.ChangedButton == MouseButton.Left && (_isSpaceHeld || Session.ActiveTool == AnnotationTool.HandPan);
        if (middlePan || handPan)
        {
            CommitAndCloseInlineEditor(save: true);
            _isPanning = true;
            _panStartMouse = e.GetPosition(_scrollViewer);
            _panStartOffsetH = _scrollViewer.HorizontalOffset;
            _panStartOffsetV = _scrollViewer.VerticalOffset;
            _scrollViewer.CaptureMouse();
            _scrollViewer.Cursor = Cursors.ScrollAll;
            e.Handled = true;
        }
    }

    private void ScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        Point cur = e.GetPosition(_scrollViewer);
        double dx = cur.X - _panStartMouse.X;
        double dy = cur.Y - _panStartMouse.Y;
        _scrollViewer.ScrollToHorizontalOffset(Math.Max(0, _panStartOffsetH - dx));
        _scrollViewer.ScrollToVerticalOffset(Math.Max(0, _panStartOffsetV - dy));
        e.Handled = true;
    }

    private void ScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        _scrollViewer.ReleaseMouseCapture();
        _scrollViewer.Cursor = null;
        UpdateAllPageCursors();
        e.Handled = true;
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Session is null) return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            CommitAndCloseInlineEditor(save: true);
            Session.Zoom *= e.Delta > 0 ? 1.15 : (1.0 / 1.15);
            e.Handled = true;
        }
    }

    private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Session is null || _layoutSlots.Length == 0) return;

        UpdateVisiblePages();

        if (!_isSyncingScrollToSession && Math.Abs(e.VerticalChange) > 0.1)
        {
            double midY = _scrollViewer.VerticalOffset + (_scrollViewer.ViewportHeight * 0.4);
            int midPage = FindFirstPageAtOrBelow(midY);
            if (midPage >= 0 && midPage < Session.PageCount && Session.CurrentPageIndex != midPage)
            {
                _isSyncingScrollToSession = true;
                try
                {
                    Session.CurrentPageIndex = midPage;
                }
                finally
                {
                    _isSyncingScrollToSession = false;
                }
            }
        }
    }

    private static void OnSessionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewport = (PdfPageViewport)d;
        viewport.CommitAndCloseInlineEditor(save: false);
        viewport.ClearSelectedCanvasObject();

        if (e.OldValue is DocumentSession oldSession)
        {
            oldSession.PropertyChanged -= viewport.OnSessionPropertyChanged;
        }

        viewport.ResetViewportVisuals(clearCache: true);

        if (e.NewValue is DocumentSession newSession)
        {
            newSession.PropertyChanged += viewport.OnSessionPropertyChanged;
            viewport.RebuildLayout();
            viewport.ScrollToPage(newSession.CurrentPageIndex);
            viewport.UpdateVisiblePages();
        }
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Session is null) return;

        if (e.PropertyName is nameof(DocumentSession.Zoom)
            or nameof(DocumentSession.Rotation)
            or nameof(DocumentSession.DocumentRevision)
            or nameof(DocumentSession.DarkReadingMode)
            or nameof(DocumentSession.TwoPageMode))
        {
            CommitAndCloseInlineEditor(save: false);
            int anchorPage = Session.CurrentPageIndex;
            if (e.PropertyName is nameof(DocumentSession.DocumentRevision) or nameof(DocumentSession.DarkReadingMode))
            {
                _tileCache.Clear();
                ClearSelectedCanvasObject();
            }
            ClearTextSelection();
            ResetViewportVisuals(clearCache: false);
            RebuildLayout();
            ScrollToPage(anchorPage);
            UpdateVisiblePages();
        }
        else if (e.PropertyName == nameof(DocumentSession.CurrentPageIndex))
        {
            if (!_isSyncingScrollToSession)
            {
                ScrollToPage(Session.CurrentPageIndex);
            }
            RenderAllVisibleHighlights();
        }
        else if (e.PropertyName is nameof(DocumentSession.CurrentPageHighlights)
            or nameof(DocumentSession.ActiveMatchOnCurrentPage))
        {
            RenderAllVisibleHighlights();
        }
        else if (e.PropertyName == nameof(DocumentSession.ActiveTool))
        {
            CommitAndCloseInlineEditor(save: true);
            UpdateAllPageCursors();
        }
    }

    public void FitToWidth()
    {
        if (Session is null || Session.Pages.Count == 0) return;
        var page = Session.Pages[Session.CurrentPageIndex];
        bool swapped = Session.Rotation % 2 == 1;
        double baseWidthDip = (swapped ? page.HeightPt : page.WidthPt) * (96.0 / 72.0);
        double available = Math.Max(200, _scrollViewer.ViewportWidth - (OuterPaddingDip * 2 + 16));
        Session.Zoom = available / baseWidthDip;
    }

    public void FitToPage()
    {
        if (Session is null || Session.Pages.Count == 0) return;
        var page = Session.Pages[Session.CurrentPageIndex];
        bool swapped = Session.Rotation % 2 == 1;
        double baseWidthDip = (swapped ? page.HeightPt : page.WidthPt) * (96.0 / 72.0);
        double baseHeightDip = (swapped ? page.WidthPt : page.HeightPt) * (96.0 / 72.0);
        double availW = Math.Max(200, _scrollViewer.ViewportWidth - (OuterPaddingDip * 2 + 16));
        double availH = Math.Max(200, _scrollViewer.ViewportHeight - (OuterPaddingDip * 2 + 16));
        Session.Zoom = Math.Min(availW / baseWidthDip, availH / baseHeightDip);
    }

    public void ScrollToPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _layoutSlots.Length) return;
        double targetY = Math.Max(0, _layoutSlots[pageIndex].TopDip - OuterPaddingDip);
        _isSyncingScrollToSession = true;
        try
        {
            _scrollViewer.ScrollToVerticalOffset(targetY);
        }
        finally
        {
            _isSyncingScrollToSession = false;
        }
    }

    public bool ApplyMarkupToSelection(PdfAnnotSubtype subtype)
    {
        if (!HasTextSelection || Session is null) return false;
        uint color = subtype == PdfAnnotSubtype.Highlight
            ? ((Session.ActiveColorRgba & 0xFFFFFF00u) | 0x80u)
            : ((Session.ActiveColorRgba & 0xFFFFFF00u) | 0xFFu);
        Session.AddTextMarkup(_selPageIndex, subtype, _selRects.ToArray(), color);
        ClearTextSelection();
        RenderAllVisibleHighlights();
        return true;
    }

    public bool DeleteSelectedCanvasItem()
    {
        if (Session is null) return false;

        if (_selectedAnnotation is { } annot)
        {
            _selectedAnnotation = null;
            Session.DeleteExistingAnnotation(annot);
            return true;
        }

        if (_selectedImage is { } img)
        {
            _selectedImage = null;
            Session.RemovePageObjectUndoable(img.PageIndex, img.Obj.Index);
            return true;
        }

        if (_selectedFormField is { } field)
        {
            _selectedFormField = null;
            Session.RemoveFormField(field.Name);
            return true;
        }

        return false;
    }

    private void ClearSelectedCanvasObject()
    {
        _selectedFormField = null;
        _selectedAnnotation = null;
        _selectedImage = null;
    }

    private void ResetViewportVisuals(bool clearCache)
    {
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        _inFlightTiles.Clear();
        _activePages.Clear();
        _documentCanvas.Children.Clear();
        if (clearCache)
        {
            _tileCache.Clear();
        }
    }

    private void RebuildLayout()
    {
        var session = Session;
        if (session is null || session.Pages.Count == 0)
        {
            _layoutSlots = [];
            _documentCanvas.Width = 0;
            _documentCanvas.Height = 0;
            return;
        }

        int count = session.Pages.Count;
        _layoutSlots = new PageLayoutSlot[count];
        bool swapped = session.Rotation % 2 == 1;
        double scale = (96.0 / 72.0) * session.Zoom;
        double y = OuterPaddingDip;
        double maxW = 0;

        if (session.TwoPageMode && count > 1)
        {
            const double spreadGap = 16.0;
            for (int i = 0; i < count; i += 2)
            {
                var p1 = session.Pages[i];
                double w1 = (swapped ? p1.HeightPt : p1.WidthPt) * scale;
                double rowW = w1;
                if (i + 1 < count)
                {
                    var p2 = session.Pages[i + 1];
                    rowW += spreadGap + (swapped ? p2.HeightPt : p2.WidthPt) * scale;
                }
                if (rowW > maxW) maxW = rowW;
            }

            double canvasW = maxW + OuterPaddingDip * 2;
            for (int i = 0; i < count; i += 2)
            {
                var p1 = session.Pages[i];
                double w1 = (swapped ? p1.HeightPt : p1.WidthPt) * scale;
                double h1 = (swapped ? p1.WidthPt : p1.HeightPt) * scale;

                if (i + 1 < count)
                {
                    var p2 = session.Pages[i + 1];
                    double w2 = (swapped ? p2.HeightPt : p2.WidthPt) * scale;
                    double h2 = (swapped ? p2.WidthPt : p2.HeightPt) * scale;
                    double rowW = w1 + spreadGap + w2;
                    double startX = Math.Max(OuterPaddingDip, (canvasW - rowW) * 0.5);
                    double rowH = Math.Max(h1, h2);

                    _layoutSlots[i] = new PageLayoutSlot { PageIndex = i, LeftDip = startX, TopDip = y, WidthDip = w1, HeightDip = h1 };
                    _layoutSlots[i + 1] = new PageLayoutSlot { PageIndex = i + 1, LeftDip = startX + w1 + spreadGap, TopDip = y, WidthDip = w2, HeightDip = h2 };
                    y += rowH + PageGapDip;
                }
                else
                {
                    double startX = Math.Max(OuterPaddingDip, (canvasW - w1) * 0.5);
                    _layoutSlots[i] = new PageLayoutSlot { PageIndex = i, LeftDip = startX, TopDip = y, WidthDip = w1, HeightDip = h1 };
                    y += h1 + PageGapDip;
                }
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                var p = session.Pages[i];
                double wDip = (swapped ? p.HeightPt : p.WidthPt) * scale;
                double hDip = (swapped ? p.WidthPt : p.HeightPt) * scale;
                if (wDip > maxW) maxW = wDip;
                _layoutSlots[i] = new PageLayoutSlot
                {
                    PageIndex = i,
                    LeftDip = -1,
                    TopDip = y,
                    WidthDip = wDip,
                    HeightDip = hDip
                };
                y += hDip + PageGapDip;
            }
            double canvasW = maxW + OuterPaddingDip * 2;
            for (int i = 0; i < count; i++)
            {
                var s = _layoutSlots[i];
                _layoutSlots[i] = new PageLayoutSlot
                {
                    PageIndex = s.PageIndex,
                    LeftDip = Math.Max(OuterPaddingDip, (canvasW - s.WidthDip) * 0.5),
                    TopDip = s.TopDip,
                    WidthDip = s.WidthDip,
                    HeightDip = s.HeightDip
                };
            }
        }

        _maxPageWidthDip = maxW;
        _totalDocumentHeightDip = y + OuterPaddingDip;
        _documentCanvas.Width = Math.Max(1, _maxPageWidthDip + OuterPaddingDip * 2);
        _documentCanvas.Height = Math.Max(1, _totalDocumentHeightDip);
    }

    private int FindFirstPageAtOrBelow(double yDip)
    {
        if (_layoutSlots.Length == 0) return -1;
        int lo = 0, hi = _layoutSlots.Length - 1, ans = _layoutSlots.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_layoutSlots[mid].BottomDip >= yDip)
            {
                ans = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return ans;
    }

    private void UpdateVisiblePages()
    {
        var session = Session;
        if (session is null || _layoutSlots.Length == 0) return;

        double viewTop = _scrollViewer.VerticalOffset;
        double viewHeight = _scrollViewer.ViewportHeight > 0 ? _scrollViewer.ViewportHeight : 800;
        double viewBottom = viewTop + viewHeight;

        double minY = Math.Max(0, viewTop - PreloadMarginDip);
        double maxY = viewBottom + PreloadMarginDip;

        int firstIdx = FindFirstPageAtOrBelow(minY);
        if (firstIdx < 0) return;

        var neededPages = new HashSet<int>();
        for (int i = firstIdx; i < _layoutSlots.Length; i++)
        {
            if (_layoutSlots[i].TopDip > maxY) break;
            neededPages.Add(i);
        }

        var toRemove = _activePages.Keys.Where(idx => !neededPages.Contains(idx)).ToList();
        foreach (int idx in toRemove)
        {
            if (idx == _editingFieldPageIndex)
            {
                CommitAndCloseInlineEditor(save: true);
            }
            _documentCanvas.Children.Remove(_activePages[idx].Border);
            _activePages.Remove(idx);
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        double canvasWidth = _documentCanvas.Width;
        double viewLeft = _scrollViewer.HorizontalOffset;
        double viewWidth = _scrollViewer.ViewportWidth > 0 ? _scrollViewer.ViewportWidth : 1200;
        double viewRight = viewLeft + viewWidth;

        foreach (int pageIdx in neededPages)
        {
            var slot = _layoutSlots[pageIdx];
            if (!_activePages.TryGetValue(pageIdx, out var visual))
            {
                visual = CreatePageVisual(slot, canvasWidth);
                _activePages[pageIdx] = visual;
                _documentCanvas.Children.Add(visual.Border);
                RenderHighlightsForPage(visual);
            }
            UpdateTilesForPage(session, visual, slot, dpi, viewLeft, viewRight, viewTop, viewBottom);
        }
    }

    private ActivePageVisual CreatePageVisual(PageLayoutSlot slot, double canvasWidth)
    {
        var tileCanvas = new Canvas
        {
            Width = slot.WidthDip,
            Height = slot.HeightDip,
            ClipToBounds = true,
            Background = Brushes.White
        };

        var overlayCanvas = new Canvas
        {
            Width = slot.WidthDip,
            Height = slot.HeightDip,
            IsHitTestVisible = false,
            Background = Brushes.Transparent
        };

        var interactiveCanvas = new Canvas
        {
            Width = slot.WidthDip,
            Height = slot.HeightDip,
            Background = null
        };

        var surfaceGrid = new Grid
        {
            Width = slot.WidthDip,
            Height = slot.HeightDip,
            Background = Brushes.Transparent,
            Cursor = GetDefaultCursorForActiveTool()
        };
        surfaceGrid.Children.Add(tileCanvas);
        surfaceGrid.Children.Add(overlayCanvas);
        surfaceGrid.Children.Add(interactiveCanvas);

        var border = new Border
        {
            Background = Brushes.White,
            BorderBrush = PageBorderBrush,
            BorderThickness = new Thickness(1, 1, 2, 2),
            Child = surfaceGrid,
            SnapsToDevicePixels = true
        };

        double leftDip = slot.LeftDip >= 0 ? slot.LeftDip : Math.Max(OuterPaddingDip, (canvasWidth - slot.WidthDip) * 0.5);
        Canvas.SetLeft(border, leftDip);
        Canvas.SetTop(border, slot.TopDip);

        var visual = new ActivePageVisual
        {
            PageIndex = slot.PageIndex,
            Border = border,
            SurfaceGrid = surfaceGrid,
            TileCanvas = tileCanvas,
            OverlayCanvas = overlayCanvas,
            InteractiveCanvas = interactiveCanvas
        };

        surfaceGrid.MouseLeftButtonDown += (_, e) => OnPageMouseLeftButtonDown(visual, e);
        surfaceGrid.MouseMove += (_, e) => OnPageMouseMove(visual, e);
        surfaceGrid.MouseLeftButtonUp += (_, e) => OnPageMouseLeftButtonUp(visual, e);

        return visual;
    }

    private Cursor GetDefaultCursorForActiveTool()
    {
        if (_isSpaceHeld || Session?.ActiveTool == AnnotationTool.HandPan)
            return Cursors.Hand;
        return Session?.ActiveTool is AnnotationTool.None or null ? Cursors.Arrow : Cursors.Cross;
    }

    private void UpdateTilesForPage(
        DocumentSession session,
        ActivePageVisual visual,
        PageLayoutSlot slot,
        DpiScale dpi,
        double viewLeft,
        double viewRight,
        double viewTop,
        double viewBottom)
    {
        int fullW = Math.Clamp((int)Math.Round(slot.WidthDip * dpi.DpiScaleX), 1, 16384);
        int fullH = Math.Clamp((int)Math.Round(slot.HeightDip * dpi.DpiScaleY), 1, 16384);

        double pageLeft = Canvas.GetLeft(visual.Border);
        double pageTop = slot.TopDip;

        double localLeftDip = Math.Max(0, viewLeft - pageLeft - 128);
        double localTopDip = Math.Max(0, viewTop - pageTop - 256);
        double localRightDip = Math.Min(slot.WidthDip, viewRight - pageLeft + 128);
        double localBottomDip = Math.Min(slot.HeightDip, viewBottom - pageTop + 256);

        if (localRightDip <= localLeftDip || localBottomDip <= localTopDip)
            return;

        int startCol = Math.Max(0, (int)Math.Floor((localLeftDip * dpi.DpiScaleX) / TileSizePx));
        int endCol = Math.Min((fullW - 1) / TileSizePx, (int)Math.Floor((localRightDip * dpi.DpiScaleX) / TileSizePx));
        int startRow = Math.Max(0, (int)Math.Floor((localTopDip * dpi.DpiScaleY) / TileSizePx));
        int endRow = Math.Min((fullH - 1) / TileSizePx, (int)Math.Floor((localBottomDip * dpi.DpiScaleY) / TileSizePx));

        var neededKeys = new HashSet<TileKey>();
        int revision = session.DocumentRevision;
        int rotation = session.Rotation;

        for (int row = startRow; row <= endRow; row++)
        {
            int ty = row * TileSizePx;
            int th = Math.Min(TileSizePx, fullH - ty);
            for (int col = startCol; col <= endCol; col++)
            {
                int tx = col * TileSizePx;
                int tw = Math.Min(TileSizePx, fullW - tx);

                var key = new TileKey(revision, slot.PageIndex, rotation, fullW, fullH, tx, ty, tw, th);
                neededKeys.Add(key);

                if (visual.MountedTiles.ContainsKey(key))
                    continue;

                if (_tileCache.TryGet(key, out var cachedBmp))
                {
                    MountTileImage(visual, key, cachedBmp, dpi);
                }
                else if (_inFlightTiles.Add(key))
                {
                    _ = RenderTileAsync(session, visual, key, dpi, _renderCts?.Token ?? CancellationToken.None);
                }
            }
        }

        var stale = visual.MountedTiles.Keys.Where(k => !neededKeys.Contains(k)).ToList();
        foreach (var k in stale)
        {
            visual.TileCanvas.Children.Remove(visual.MountedTiles[k]);
            visual.MountedTiles.Remove(k);
        }
    }

    private async Task RenderTileAsync(
        DocumentSession session,
        ActivePageVisual visual,
        TileKey key,
        DpiScale dpi,
        CancellationToken ct)
    {
        try
        {
            bool darkMode = session.DarkReadingMode;
            await session.RenderGate.WaitAsync(ct);
            byte[] bgra;
            try
            {
                if (ct.IsCancellationRequested) return;
                bgra = await Task.Run(() =>
                {
                    byte[] pixels = session.Document.RenderPageTileBgra(
                        key.PageIndex,
                        key.FullWidthPx,
                        key.FullHeightPx,
                        key.TileX,
                        key.TileY,
                        key.TileWidthPx,
                        key.TileHeightPx,
                        key.Rotation);

                    if (darkMode)
                    {
                        for (int i = 0; i < pixels.Length; i += 4)
                        {
                            pixels[i] = (byte)(255 - pixels[i] * 221 / 255);
                            pixels[i + 1] = (byte)(255 - pixels[i + 1] * 224 / 255);
                            pixels[i + 2] = (byte)(255 - pixels[i + 2] * 225 / 255);
                        }
                    }
                    return pixels;
                }, ct);
            }
            finally
            {
                try { session.RenderGate.Release(); } catch (ObjectDisposedException) { }
            }

            if (ct.IsCancellationRequested) return;

            var bmp = BitmapSource.Create(
                key.TileWidthPx,
                key.TileHeightPx,
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Bgra32,
                null,
                bgra,
                key.TileWidthPx * 4);
            bmp.Freeze();

            _tileCache.Put(key, bmp, bgra.Length);

            if (!ct.IsCancellationRequested &&
                _activePages.TryGetValue(key.PageIndex, out var currentVisual) &&
                ReferenceEquals(currentVisual, visual) &&
                !visual.MountedTiles.ContainsKey(key))
            {
                MountTileImage(visual, key, bmp, dpi);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            _inFlightTiles.Remove(key);
        }
    }

    private static void MountTileImage(ActivePageVisual visual, TileKey key, BitmapSource bmp, DpiScale dpi)
    {
        var img = new Image
        {
            Source = bmp,
            Width = key.TileWidthPx / dpi.DpiScaleX,
            Height = key.TileHeightPx / dpi.DpiScaleY,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(img, key.TileX / dpi.DpiScaleX);
        Canvas.SetTop(img, key.TileY / dpi.DpiScaleY);
        visual.TileCanvas.Children.Add(img);
        visual.MountedTiles[key] = img;
    }

    private void UpdateAllPageCursors()
    {
        var cursor = GetDefaultCursorForActiveTool();
        foreach (var v in _activePages.Values)
        {
            v.SurfaceGrid.Cursor = cursor;
        }
    }

    private void RenderAllVisibleHighlights()
    {
        foreach (var v in _activePages.Values)
        {
            RenderHighlightsForPage(v);
        }
    }

    public string GetSelectedText()
    {
        if (Session is null || _selPageIndex < 0 || _selStartChar < 0 || _selCharCount <= 0)
            return string.Empty;
        return Session.Document.GetTextRange(_selPageIndex, _selStartChar, _selCharCount);
    }

    public void ClearTextSelection()
    {
        _isSelectingText = false;
        _selPageIndex = -1;
        _selAnchorChar = -1;
        _selStartChar = -1;
        _selCharCount = 0;
        _selRects = Array.Empty<PvRect>();
    }

    private void OpenInlineFieldEditor(ActivePageVisual visual, PdfFormField field, Rect fieldDipRect)
    {
        CommitAndCloseInlineEditor(save: true);

        double w = Math.Max(48, fieldDipRect.Width);
        double h = Math.Max(22, fieldDipRect.Height);

        var tb = new TextBox
        {
            Text = field.Value ?? string.Empty,
            Width = w,
            Height = h,
            FontSize = Math.Clamp(h * 0.58, 10.5, 24.0),
            Padding = new Thickness(4, 1, 4, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.White,
            Foreground = Brushes.Black,
            BorderBrush = FormFieldHandleBrush,
            BorderThickness = new Thickness(1.5)
        };

        Canvas.SetLeft(tb, fieldDipRect.X);
        Canvas.SetTop(tb, fieldDipRect.Y);

        tb.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Escape)
            {
                ke.Handled = true;
                CommitAndCloseInlineEditor(save: false);
            }
            else if (ke.Key == Key.Enter)
            {
                ke.Handled = true;
                CommitAndCloseInlineEditor(save: true);
            }
            else if (ke.Key == Key.Tab && Session is not null)
            {
                ke.Handled = true;
                int pageIdx = visual.PageIndex;
                string curName = field.Name;
                CommitAndCloseInlineEditor(save: true);

                var txFields = Session.GetFormFieldsForPage(pageIdx)
                    .Where(f => string.Equals(f.FieldType, "/Tx", StringComparison.Ordinal))
                    .ToArray();
                if (txFields.Length > 1 && _activePages.TryGetValue(pageIdx, out var targetVisual))
                {
                    int curIdx = Array.FindIndex(txFields, f => f.Name == curName);
                    int nextIdx = (curIdx + 1 + txFields.Length) % txFields.Length;
                    var nextField = txFields[nextIdx];
                    var pInfo = Session.Pages[pageIdx];
                    double scale = (96.0 / 72.0) * Session.Zoom;
                    var nextDip = PdfRectToDipRect(nextField.Rect, pInfo.WidthPt, pInfo.HeightPt, Session.Rotation, scale);
                    OpenInlineFieldEditor(targetVisual, nextField, nextDip);
                }
            }
        };

        tb.LostFocus += (_, _) => CommitAndCloseInlineEditor(save: true);

        _inlineFieldEditor = tb;
        _editingFormField = field;
        _editingFieldPageIndex = visual.PageIndex;
        visual.InteractiveCanvas.Children.Add(tb);
        tb.Focus();
        tb.SelectAll();
    }

    public void CommitAndCloseInlineEditor(bool save)
    {
        if (_isCommittingInlineEditor || _inlineFieldEditor is null) return;
        _isCommittingInlineEditor = true;
        try
        {
            var tb = _inlineFieldEditor;
            var field = _editingFormField;
            int pageIdx = _editingFieldPageIndex;

            _inlineFieldEditor = null;
            _editingFormField = null;
            _editingFieldPageIndex = -1;

            if (pageIdx >= 0 && _activePages.TryGetValue(pageIdx, out var visual))
            {
                visual.InteractiveCanvas.Children.Remove(tb);
            }
            else if (tb.Parent is Panel parentPanel)
            {
                parentPanel.Children.Remove(tb);
            }

            if (save && Session is not null && field is not null &&
                !string.Equals(tb.Text, field.Value, StringComparison.Ordinal))
            {
                Session.SetFormFieldValue(field.Name, tb.Text);
            }
        }
        finally
        {
            _isCommittingInlineEditor = false;
        }
    }

    private void RenderHighlightsForPage(ActivePageVisual visual)
    {
        visual.OverlayCanvas.Children.Clear();
        var session = Session;
        if (session is null || session.PageCount == 0 || visual.PageIndex >= session.Pages.Count) return;

        var pageInfo = session.Pages[visual.PageIndex];
        double origW = pageInfo.WidthPt;
        double origH = pageInfo.HeightPt;
        double scale = (96.0 / 72.0) * session.Zoom;
        int rot = session.Rotation;

        // 1. Render interactive text selection rects
        if (visual.PageIndex == _selPageIndex && _selRects.Count > 0)
        {
            foreach (var r in _selRects)
            {
                var dipRect = PdfRectToDipRect(r, origW, origH, rot, scale);
                var selShape = new Rectangle
                {
                    Width = Math.Max(2, dipRect.Width),
                    Height = Math.Max(2, dipRect.Height),
                    Fill = TextSelectionBrush
                };
                Canvas.SetLeft(selShape, dipRect.X);
                Canvas.SetTop(selShape, dipRect.Y);
                visual.OverlayCanvas.Children.Add(selShape);
            }
        }

        // 1b. Render subtle interactive badges for Sticky Notes (/Text) + selected annotation outline
        foreach (var annot in session.GetInteractiveAnnotationsForPage(visual.PageIndex))
        {
            var dipRect = PdfRectToDipRect(annot.Rect, origW, origH, rot, scale);
            bool isSelected = _selectedAnnotation is not null &&
                              _selectedAnnotation.PageIndex == annot.PageIndex &&
                              _selectedAnnotation.Index == annot.Index;

            if (annot.Subtype == PdfAnnotSubtype.Text || isSelected)
            {
                var badge = new Rectangle
                {
                    Width = Math.Max(14, dipRect.Width),
                    Height = Math.Max(14, dipRect.Height),
                    Fill = annot.Subtype == PdfAnnotSubtype.Text ? NoteBadgeFillBrush : Brushes.Transparent,
                    Stroke = isSelected ? SelectedObjectStrokeBrush : NoteBadgeStrokeBrush,
                    StrokeThickness = isSelected ? 1.8 : 1.1,
                    StrokeDashArray = isSelected ? [3, 2] : null,
                    RadiusX = 3,
                    RadiusY = 3
                };
                Canvas.SetLeft(badge, dipRect.X);
                Canvas.SetTop(badge, dipRect.Y);
                visual.OverlayCanvas.Children.Add(badge);

                if (isSelected && annot.Subtype == PdfAnnotSubtype.Square)
                {
                    var handle = new Rectangle { Width = 7, Height = 7, Fill = SelectedObjectStrokeBrush };
                    Canvas.SetLeft(handle, dipRect.Right - 7);
                    Canvas.SetTop(handle, dipRect.Bottom - 7);
                    visual.OverlayCanvas.Children.Add(handle);
                }
            }
        }

        // 1c. Render selected Image object frame + resize handle
        if (_selectedImage is { } selImg && selImg.PageIndex == visual.PageIndex)
        {
            var dipRect = PdfRectToDipRect(selImg.Obj.Bounds, origW, origH, rot, scale);
            var outline = new Rectangle
            {
                Width = Math.Max(10, dipRect.Width),
                Height = Math.Max(10, dipRect.Height),
                Stroke = SelectedObjectStrokeBrush,
                StrokeThickness = 1.6,
                StrokeDashArray = [4, 2],
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(outline, dipRect.X);
            Canvas.SetTop(outline, dipRect.Y);
            visual.OverlayCanvas.Children.Add(outline);

            var handle = new Rectangle { Width = 8, Height = 8, Fill = SelectedObjectStrokeBrush };
            Canvas.SetLeft(handle, dipRect.Right - 8);
            Canvas.SetTop(handle, dipRect.Bottom - 8);
            visual.OverlayCanvas.Children.Add(handle);
        }

        // 1d. Render subtle interactive frames + resize handles for AcroForm fields on this page
        var pageFields = session.GetFormFieldsForPage(visual.PageIndex);
        foreach (var field in pageFields)
        {
            var dipRect = PdfRectToDipRect(field.Rect, origW, origH, rot, scale);
            bool isSelected = _selectedFormField is not null && _selectedFormField.Name == field.Name;
            var outline = new Rectangle
            {
                Width = Math.Max(8, dipRect.Width),
                Height = Math.Max(8, dipRect.Height),
                Stroke = isSelected ? SelectedObjectStrokeBrush : FormFieldOutlineBrush,
                StrokeThickness = isSelected ? 1.6 : 1.0,
                StrokeDashArray = [3, 2],
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(outline, dipRect.X);
            Canvas.SetTop(outline, dipRect.Y);
            visual.OverlayCanvas.Children.Add(outline);

            var handle = new Rectangle
            {
                Width = 6,
                Height = 6,
                Fill = isSelected ? SelectedObjectStrokeBrush : FormFieldHandleBrush
            };
            Canvas.SetLeft(handle, dipRect.Right - 6);
            Canvas.SetTop(handle, dipRect.Bottom - 6);
            visual.OverlayCanvas.Children.Add(handle);
        }

        // 2. Render search match highlights across ALL visible pages
        var highlights = session.GetHighlightsForPage(visual.PageIndex);
        if (highlights.Count == 0) return;

        var activeMatch = session.ActiveSearchMatch;

        foreach (var r in highlights)
        {
            var dipRect = PdfRectToDipRect(r, origW, origH, rot, scale);
            bool isActive = activeMatch is not null &&
                activeMatch.PageIndex == visual.PageIndex &&
                Math.Abs(activeMatch.Rect.Left - r.Left) < 0.01 &&
                Math.Abs(activeMatch.Rect.Top - r.Top) < 0.01 &&
                Math.Abs(activeMatch.Rect.Right - r.Right) < 0.01 &&
                Math.Abs(activeMatch.Rect.Bottom - r.Bottom) < 0.01;

            var rectShape = new Rectangle
            {
                Width = Math.Max(2, dipRect.Width),
                Height = Math.Max(2, dipRect.Height),
                Fill = isActive ? ActiveMatchFillBrush : MatchFillBrush,
                Stroke = isActive ? ActiveMatchStrokeBrush : null,
                StrokeThickness = isActive ? 1.5 : 0,
                RadiusX = 2,
                RadiusY = 2
            };
            Canvas.SetLeft(rectShape, dipRect.X);
            Canvas.SetTop(rectShape, dipRect.Y);
            visual.OverlayCanvas.Children.Add(rectShape);
        }
    }

    private void OnPageMouseLeftButtonDown(ActivePageVisual visual, MouseButtonEventArgs e)
    {
        var session = Session;
        if (session is null || session.PageCount == 0)
            return;

        if (_inlineFieldEditor is not null && !_inlineFieldEditor.IsMouseOver)
        {
            CommitAndCloseInlineEditor(save: true);
        }

        Point pos = e.GetPosition(visual.SurfaceGrid);

        // Cursor mode -> Form Field inline edit/move/resize, Annotation edit/move, Image move/resize, Hyperlink, or Text Selection
        if (session.ActiveTool == AnnotationTool.None)
        {
            _dragStartDip = pos;
            ClearTextSelection();

            // 1. AcroForm Fields
            if (HitTestFormField(visual.PageIndex, pos, session, out var hitField, out var fieldDipRect, out bool onFieldResize))
            {
                ClearSelectedCanvasObject();
                _selectedFormField = hitField;
                RenderAllVisibleHighlights();

                if (e.ClickCount >= 2 && !onFieldResize)
                {
                    if (string.Equals(hitField.FieldType, "/Tx", StringComparison.Ordinal))
                        OpenInlineFieldEditor(visual, hitField, fieldDipRect);
                    else
                        FormFieldDoubleClicked?.Invoke(hitField);
                    e.Handled = true;
                    return;
                }

                StartObjectDrag(visual, DragTargetKind.FormField, fieldDipRect, onFieldResize);
                _draggedFormField = hitField;
                e.Handled = true;
                return;
            }

            // 2. Interactive Annotations (Notes, Rectangles, Markups)
            if (HitTestAnnotation(visual.PageIndex, pos, session, out var hitAnnot, out var annotDipRect, out bool onAnnotResize))
            {
                ClearSelectedCanvasObject();
                _selectedAnnotation = hitAnnot;
                RenderAllVisibleHighlights();

                if (e.ClickCount >= 2 && PromptEditAnnotation is not null)
                {
                    string? edited = PromptEditAnnotation(hitAnnot);
                    if (edited is not null && edited != hitAnnot.Contents)
                    {
                        session.UpdateExistingAnnotation(hitAnnot, newContents: edited);
                    }
                    e.Handled = true;
                    return;
                }

                if (hitAnnot.Subtype is PdfAnnotSubtype.Text or PdfAnnotSubtype.Square)
                {
                    StartObjectDrag(visual, DragTargetKind.Annotation, annotDipRect, onAnnotResize && hitAnnot.Subtype == PdfAnnotSubtype.Square);
                    _draggedAnnotation = hitAnnot;
                }
                e.Handled = true;
                return;
            }

            // 3. Internal/External Hyperlinks (check before images so linked banners stay clickable)
            var pt = DipPointToPdfPoint(pos, visual.PageIndex, session);
            var link = session.Document.GetLinkAtPoint(visual.PageIndex, pt.X, pt.Y);
            if (link.Found)
            {
                ClearSelectedCanvasObject();
                if (link.DestPageIndex is int dstPage && dstPage >= 0 && dstPage < session.PageCount)
                {
                    session.JumpToPageWithHistory(dstPage);
                }
                else if (!string.IsNullOrWhiteSpace(link.Uri))
                {
                    ExternalUriRequested?.Invoke(link.Uri);
                }
                e.Handled = true;
                return;
            }

            // 4. Text Selection if pointer is directly on text characters
            int charIdx = session.Document.GetCharIndexAtPos(visual.PageIndex, pt.X, pt.Y, tolerancePt: 10.0);
            if (charIdx >= 0)
            {
                ClearSelectedCanvasObject();
                _isSelectingText = true;
                _drawingPage = visual;
                _selPageIndex = visual.PageIndex;
                _selAnchorChar = charIdx;
                _selStartChar = charIdx;
                _selCharCount = 1;
                _selRects = session.Document.GetTextRangeRects(visual.PageIndex, charIdx, 1);
                visual.SurfaceGrid.CaptureMouse();
                RenderAllVisibleHighlights();
                e.Handled = true;
                return;
            }

            // 5. Image Objects (move / corner-resize directly on page!)
            if (HitTestImageObject(visual.PageIndex, pos, session, out var hitImg, out var imgDipRect, out bool onImgResize))
            {
                ClearSelectedCanvasObject();
                _selectedImage = (visual.PageIndex, hitImg);
                RenderAllVisibleHighlights();
                StartObjectDrag(visual, DragTargetKind.ImageObject, imgDipRect, onImgResize);
                _draggedImage = hitImg;
                e.Handled = true;
                return;
            }

            ClearSelectedCanvasObject();
            RenderAllVisibleHighlights();
            e.Handled = true;
            return;
        }

        if (session.ActiveTool == AnnotationTool.Note)
        {
            string? noteText = PromptNoteText is not null ? PromptNoteText() : "Nota";
            if (!string.IsNullOrWhiteSpace(noteText))
            {
                var pt = DipPointToPdfPoint(pos, visual.PageIndex, session);
                session.CurrentPageIndex = visual.PageIndex;
                session.AddNote(new PvRect { Left = pt.X, Bottom = pt.Y - 24, Right = pt.X + 24, Top = pt.Y }, noteText, session.ActiveColorRgba);
            }
            e.Handled = true;
            return;
        }

        if (session.ActiveTool == AnnotationTool.AddText)
        {
            string? newText = PromptAddText is not null ? PromptAddText() : "Texto";
            if (!string.IsNullOrWhiteSpace(newText))
            {
                var pt = DipPointToPdfPoint(pos, visual.PageIndex, session);
                session.CurrentPageIndex = visual.PageIndex;
                session.AddTextObject(newText, pt.X, pt.Y, "Helvetica", session.ActiveFontSize, session.ActiveColorRgba);
            }
            e.Handled = true;
            return;
        }

        _isDrawing = true;
        _drawingPage = visual;
        _dragStartDip = pos;
        visual.SurfaceGrid.CaptureMouse();

        if (session.ActiveTool is AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.StrikeOut
            or AnnotationTool.Square or AnnotationTool.Redact or AnnotationTool.SignStamp
            or AnnotationTool.AddFormField or AnnotationTool.AddCheckboxField or AnnotationTool.CropPage)
        {
            _previewRect = new Rectangle
            {
                Fill = session.ActiveTool switch
                {
                    AnnotationTool.Highlight => HighlightPreviewBrush,
                    AnnotationTool.Underline => UnderlinePreviewBrush,
                    AnnotationTool.StrikeOut => StrikeOutPreviewBrush,
                    AnnotationTool.Redact => RedactPreviewFill,
                    AnnotationTool.CropPage => CropPreviewFill,
                    AnnotationTool.SignStamp or AnnotationTool.AddFormField or AnnotationTool.AddCheckboxField => SignPreviewFill,
                    _ => Brushes.Transparent
                },
                Stroke = session.ActiveTool switch
                {
                    AnnotationTool.Square => SquarePreviewStroke,
                    AnnotationTool.Redact => RedactPreviewStroke,
                    AnnotationTool.CropPage => CropPreviewStroke,
                    AnnotationTool.SignStamp or AnnotationTool.AddFormField or AnnotationTool.AddCheckboxField => SignPreviewStroke,
                    _ => null
                },
                StrokeDashArray = session.ActiveTool == AnnotationTool.CropPage ? [5, 3] : null,
                StrokeThickness = session.ActiveTool is AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.StrikeOut ? 0 : 2.0,
                Width = 1,
                Height = 1
            };
            Canvas.SetLeft(_previewRect, pos.X);
            Canvas.SetTop(_previewRect, pos.Y);
            visual.OverlayCanvas.Children.Add(_previewRect);
        }
        else if (session.ActiveTool == AnnotationTool.Ink)
        {
            _inkPointsDip.Clear();
            _inkPointsDip.Add(pos);
            _previewInk = new Polyline
            {
                Stroke = InkPreviewStroke,
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _previewInk.Points.Add(pos);
            visual.OverlayCanvas.Children.Add(_previewInk);
        }

        e.Handled = true;
    }

    private void StartObjectDrag(ActivePageVisual visual, DragTargetKind kind, Rect startDip, bool resizing)
    {
        _dragKind = kind;
        _isResizingObject = resizing;
        _draggedObjectStartDip = startDip;
        _drawingPage = visual;
        visual.SurfaceGrid.CaptureMouse();

        _objectDragPreview = new Rectangle
        {
            Width = Math.Max(10, startDip.Width),
            Height = Math.Max(10, startDip.Height),
            Fill = SignPreviewFill,
            Stroke = SignPreviewStroke,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 2]
        };
        Canvas.SetLeft(_objectDragPreview, startDip.X);
        Canvas.SetTop(_objectDragPreview, startDip.Y);
        visual.OverlayCanvas.Children.Add(_objectDragPreview);
    }

    private void OnPageMouseMove(ActivePageVisual visual, MouseEventArgs e)
    {
        if (_dragKind != DragTargetKind.None && ReferenceEquals(_drawingPage, visual) && _objectDragPreview is not null)
        {
            Point cur = e.GetPosition(visual.SurfaceGrid);
            Rect updated = ComputeMovedOrResizedObjectRect(cur, visual.SurfaceGrid.ActualWidth, visual.SurfaceGrid.ActualHeight);
            Canvas.SetLeft(_objectDragPreview, updated.X);
            Canvas.SetTop(_objectDragPreview, updated.Y);
            _objectDragPreview.Width = updated.Width;
            _objectDragPreview.Height = updated.Height;
            return;
        }

        if (_isSelectingText && ReferenceEquals(_drawingPage, visual) && Session is not null)
        {
            Point selPos = e.GetPosition(visual.SurfaceGrid);
            var pt = DipPointToPdfPoint(selPos, visual.PageIndex, Session);
            int curChar = Session.Document.GetCharIndexAtPos(visual.PageIndex, pt.X, pt.Y, tolerancePt: 18.0);
            if (curChar >= 0)
            {
                int start = Math.Min(_selAnchorChar, curChar);
                int count = Math.Abs(curChar - _selAnchorChar) + 1;
                if (start != _selStartChar || count != _selCharCount)
                {
                    _selStartChar = start;
                    _selCharCount = count;
                    _selRects = Session.Document.GetTextRangeRects(visual.PageIndex, start, count);
                    RenderHighlightsForPage(visual);
                }
            }
            return;
        }

        if (!_isDrawing || !ReferenceEquals(_drawingPage, visual) || Session is null)
        {
            if (!_isSpaceHeld && Session is { ActiveTool: AnnotationTool.None, PageCount: > 0 })
            {
                Point hoverPos = e.GetPosition(visual.SurfaceGrid);
                if ((hoverPos - _lastHoverProbeDip).Length >= 4.0)
                {
                    _lastHoverProbeDip = hoverPos;
                    UpdateHoverCursor(visual, hoverPos, Session);
                }
            }
            return;
        }

        Point pos = e.GetPosition(visual.SurfaceGrid);
        pos.X = Math.Clamp(pos.X, 0, visual.SurfaceGrid.ActualWidth);
        pos.Y = Math.Clamp(pos.Y, 0, visual.SurfaceGrid.ActualHeight);

        if (_previewRect is not null)
        {
            Canvas.SetLeft(_previewRect, Math.Min(_dragStartDip.X, pos.X));
            Canvas.SetTop(_previewRect, Math.Min(_dragStartDip.Y, pos.Y));
            _previewRect.Width = Math.Max(1, Math.Abs(pos.X - _dragStartDip.X));
            _previewRect.Height = Math.Max(1, Math.Abs(pos.Y - _dragStartDip.Y));
        }
        else if (_previewInk is not null)
        {
            if (_inkPointsDip.Count == 0 || (_inkPointsDip[^1] - pos).Length >= 2.5)
            {
                _inkPointsDip.Add(pos);
                _previewInk.Points.Add(pos);
            }
        }
    }

    private void UpdateHoverCursor(ActivePageVisual visual, Point hoverPos, DocumentSession session)
    {
        if (HitTestFormField(visual.PageIndex, hoverPos, session, out _, out _, out bool onFieldResize))
        {
            visual.SurfaceGrid.Cursor = onFieldResize ? Cursors.SizeNWSE : Cursors.Hand;
            return;
        }
        if (HitTestAnnotation(visual.PageIndex, hoverPos, session, out var hAnnot, out var aDip, out bool onAnnotResize))
        {
            visual.SurfaceGrid.ToolTip = string.IsNullOrWhiteSpace(hAnnot.Contents)
                ? hAnnot.TypeDisplay
                : $"{hAnnot.TypeDisplay}: {hAnnot.Contents}";
            visual.SurfaceGrid.Cursor = (onAnnotResize && hAnnot.Subtype == PdfAnnotSubtype.Square)
                ? Cursors.SizeNWSE
                : (hAnnot.Subtype is PdfAnnotSubtype.Text or PdfAnnotSubtype.Square ? Cursors.Hand : Cursors.Arrow);
            return;
        }
        visual.SurfaceGrid.ToolTip = null;

        var hoverPt = DipPointToPdfPoint(hoverPos, visual.PageIndex, session);
        if (session.Document.GetLinkAtPoint(visual.PageIndex, hoverPt.X, hoverPt.Y).Found)
        {
            visual.SurfaceGrid.Cursor = Cursors.Hand;
            return;
        }

        if (session.Document.GetCharIndexAtPos(visual.PageIndex, hoverPt.X, hoverPt.Y, tolerancePt: 8.0) >= 0)
        {
            visual.SurfaceGrid.Cursor = Cursors.IBeam;
            return;
        }

        if (HitTestImageObject(visual.PageIndex, hoverPos, session, out _, out _, out bool onImgResize))
        {
            visual.SurfaceGrid.Cursor = onImgResize ? Cursors.SizeNWSE : Cursors.SizeAll;
            return;
        }

        visual.SurfaceGrid.Cursor = Cursors.Arrow;
    }

    private void OnPageMouseLeftButtonUp(ActivePageVisual visual, MouseButtonEventArgs e)
    {
        if (_dragKind != DragTargetKind.None && ReferenceEquals(_drawingPage, visual) && Session is not null)
        {
            var kind = _dragKind;
            var field = _draggedFormField;
            var annot = _draggedAnnotation;
            var img = _draggedImage;

            _dragKind = DragTargetKind.None;
            _draggedFormField = null;
            _draggedAnnotation = null;
            _draggedImage = null;
            _drawingPage = null;
            visual.SurfaceGrid.ReleaseMouseCapture();

            if (_objectDragPreview is not null)
            {
                visual.OverlayCanvas.Children.Remove(_objectDragPreview);
                _objectDragPreview = null;
            }

            Point objEndPos = e.GetPosition(visual.SurfaceGrid);
            Rect finalDip = ComputeMovedOrResizedObjectRect(objEndPos, visual.SurfaceGrid.ActualWidth, visual.SurfaceGrid.ActualHeight);
            bool movedOrResized = Math.Abs(finalDip.X - _draggedObjectStartDip.X) >= 2.5 ||
                                  Math.Abs(finalDip.Y - _draggedObjectStartDip.Y) >= 2.5 ||
                                  Math.Abs(finalDip.Width - _draggedObjectStartDip.Width) >= 2.5 ||
                                  Math.Abs(finalDip.Height - _draggedObjectStartDip.Height) >= 2.5;

            if (movedOrResized)
            {
                var p1 = DipPointToPdfPoint(new Point(finalDip.Left, finalDip.Top), visual.PageIndex, Session);
                var p2 = DipPointToPdfPoint(new Point(finalDip.Right, finalDip.Bottom), visual.PageIndex, Session);
                var newPdfRect = new PvRect
                {
                    Left = Math.Min(p1.X, p2.X),
                    Right = Math.Max(p1.X, p2.X),
                    Bottom = Math.Min(p1.Y, p2.Y),
                    Top = Math.Max(p1.Y, p2.Y)
                };

                if (kind == DragTargetKind.FormField && field is not null)
                {
                    Session.MoveOrResizeFormField(field.Name, newPdfRect, visual.PageIndex);
                }
                else if (kind == DragTargetKind.Annotation && annot is not null)
                {
                    Session.UpdateExistingAnnotation(annot, newRect: newPdfRect);
                }
                else if (kind == DragTargetKind.ImageObject && img is not null)
                {
                    Session.ResizeOrMoveImageObject(visual.PageIndex, img.Index, img.Bounds, newPdfRect);
                    var refreshed = Session.GetImageObjectsForPage(visual.PageIndex).FirstOrDefault(o => o.Index == img.Index);
                    if (refreshed is not null) _selectedImage = (visual.PageIndex, refreshed);
                }
            }
            else if (!_isResizingObject)
            {
                // Single click without drag -> instant Checkbox toggle, inline TextField edit, or Sticky Note edit!
                if (kind == DragTargetKind.FormField && field is not null)
                {
                    if (string.Equals(field.FieldType, "/Btn", StringComparison.Ordinal))
                    {
                        Session.SetFormFieldValue(field.Name, field.IsChecked ? "Off" : "Yes");
                    }
                    else if (string.Equals(field.FieldType, "/Tx", StringComparison.Ordinal))
                    {
                        OpenInlineFieldEditor(visual, field, _draggedObjectStartDip);
                    }
                }
                else if (kind == DragTargetKind.Annotation && annot is { Subtype: PdfAnnotSubtype.Text } && PromptEditAnnotation is not null)
                {
                    string? edited = PromptEditAnnotation(annot);
                    if (edited is not null && edited != annot.Contents)
                    {
                        Session.UpdateExistingAnnotation(annot, newContents: edited);
                    }
                }
            }

            e.Handled = true;
            return;
        }

        if (_isSelectingText && ReferenceEquals(_drawingPage, visual))
        {
            _isSelectingText = false;
            _drawingPage = null;
            visual.SurfaceGrid.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (!_isDrawing || !ReferenceEquals(_drawingPage, visual) || Session is null)
            return;

        _isDrawing = false;
        _drawingPage = null;
        visual.SurfaceGrid.ReleaseMouseCapture();

        Point endPos = e.GetPosition(visual.SurfaceGrid);
        endPos.X = Math.Clamp(endPos.X, 0, visual.SurfaceGrid.ActualWidth);
        endPos.Y = Math.Clamp(endPos.Y, 0, visual.SurfaceGrid.ActualHeight);

        if (_previewRect is not null)
        {
            visual.OverlayCanvas.Children.Remove(_previewRect);
            _previewRect = null;

            var p1 = DipPointToPdfPoint(_dragStartDip, visual.PageIndex, Session);
            var p2 = DipPointToPdfPoint(endPos, visual.PageIndex, Session);
            bool hasDragArea = Math.Abs(endPos.X - _dragStartDip.X) >= 4 && Math.Abs(endPos.Y - _dragStartDip.Y) >= 4;

            // Text-snapped Highlight / Underline / StrikeOut
            if (Session.ActiveTool is AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.StrikeOut)
            {
                var subtype = Session.ActiveTool switch
                {
                    AnnotationTool.Underline => PdfAnnotSubtype.Underline,
                    AnnotationTool.StrikeOut => PdfAnnotSubtype.StrikeOut,
                    _ => PdfAnnotSubtype.Highlight
                };
                uint markupColor = subtype == PdfAnnotSubtype.Highlight
                    ? ((Session.ActiveColorRgba & 0xFFFFFF00u) | 0x80u)
                    : ((Session.ActiveColorRgba & 0xFFFFFF00u) | 0xFFu);

                int c1 = Session.Document.GetCharIndexAtPos(visual.PageIndex, p1.X, p1.Y, tolerancePt: 16.0);
                int c2 = Session.Document.GetCharIndexAtPos(visual.PageIndex, p2.X, p2.Y, tolerancePt: 16.0);
                if (c1 >= 0 && c2 >= 0)
                {
                    int start = Math.Min(c1, c2);
                    int count = Math.Abs(c2 - c1) + 1;
                    var quads = Session.Document.GetTextRangeRects(visual.PageIndex, start, count);
                    if (quads.Count > 0)
                    {
                        Session.AddTextMarkup(visual.PageIndex, subtype, quads, markupColor);
                        e.Handled = true;
                        return;
                    }
                }

                if (hasDragArea)
                {
                    var fallbackRect = new PvRect
                    {
                        Left = Math.Min(p1.X, p2.X),
                        Right = Math.Max(p1.X, p2.X),
                        Bottom = Math.Min(p1.Y, p2.Y),
                        Top = Math.Max(p1.Y, p2.Y)
                    };
                    Session.AddTextMarkup(visual.PageIndex, subtype, [fallbackRect], markupColor);
                }
                e.Handled = true;
                return;
            }

            // Checkbox field allows either single-click (default 16x16 pt) or drag-to-size
            if (Session.ActiveTool == AnnotationTool.AddCheckboxField)
            {
                Session.ActiveTool = AnnotationTool.None;
                ToolAutoReset?.Invoke();
                var cbRect = hasDragArea
                    ? new PvRect
                    {
                        Left = Math.Min(p1.X, p2.X),
                        Right = Math.Max(p1.X, p2.X),
                        Bottom = Math.Min(p1.Y, p2.Y),
                        Top = Math.Max(p1.Y, p2.Y)
                    }
                    : new PvRect { Left = p1.X, Bottom = p1.Y - 16, Right = p1.X + 16, Top = p1.Y };
                string? cbName = PromptFormFieldName is not null ? PromptFormFieldName() : $"Casilla_{Session.FormFields.Count + 1}";
                if (!string.IsNullOrWhiteSpace(cbName))
                {
                    Session.AddCheckboxFormField(cbName.Trim(), cbRect, initiallyChecked: false, visual.PageIndex);
                }
                e.Handled = true;
                return;
            }

            if (hasDragArea)
            {
                var pdfRect = new PvRect
                {
                    Left = Math.Min(p1.X, p2.X),
                    Right = Math.Max(p1.X, p2.X),
                    Bottom = Math.Min(p1.Y, p2.Y),
                    Top = Math.Max(p1.Y, p2.Y)
                };

                Session.CurrentPageIndex = visual.PageIndex;
                if (Session.ActiveTool == AnnotationTool.Square) Session.AddSquare(pdfRect, Session.ActiveColorRgba);
                else if (Session.ActiveTool == AnnotationTool.Redact) Session.ApplyPermanentRedaction(pdfRect);
                else if (Session.ActiveTool == AnnotationTool.CropPage)
                {
                    Session.ActiveTool = AnnotationTool.None;
                    ToolAutoReset?.Invoke();
                    Session.CropPage(visual.PageIndex, pdfRect);
                }
                else if (Session.ActiveTool == AnnotationTool.SignStamp)
                {
                    Session.ActiveTool = AnnotationTool.None;
                    ToolAutoReset?.Invoke();
                    SignatureRegionSelected?.Invoke(visual.PageIndex, pdfRect);
                }
                else if (Session.ActiveTool == AnnotationTool.AddFormField)
                {
                    Session.ActiveTool = AnnotationTool.None;
                    ToolAutoReset?.Invoke();
                    string? fieldName = PromptFormFieldName is not null ? PromptFormFieldName() : $"Campo_{Session.FormFields.Count + 1}";
                    if (!string.IsNullOrWhiteSpace(fieldName))
                    {
                        Session.AddTextFormField(fieldName.Trim(), pdfRect, "", visual.PageIndex);
                    }
                }
            }
        }
        else if (_previewInk is not null)
        {
            visual.OverlayCanvas.Children.Remove(_previewInk);
            _previewInk = null;

            if (_inkPointsDip.Count >= 2)
            {
                Session.CurrentPageIndex = visual.PageIndex;
                var pdfPts = _inkPointsDip.Select(p => DipPointToPdfPoint(p, visual.PageIndex, Session)).ToArray();
                Session.AddInk(pdfPts, Session.ActiveColorRgba);
            }
            _inkPointsDip.Clear();
        }

        e.Handled = true;
    }

    private static PvPoint DipPointToPdfPoint(Point dip, int pageIndex, DocumentSession session)
    {
        var pageInfo = session.Pages[pageIndex];
        double origW = pageInfo.WidthPt;
        double origH = pageInfo.HeightPt;
        double scale = (96.0 / 72.0) * session.Zoom;

        double ux = dip.X / scale;
        double uy = dip.Y / scale;

        double pdfX, pdfY;
        switch (session.Rotation & 3)
        {
            case 1: pdfX = uy; pdfY = ux; break;
            case 2: pdfX = origW - ux; pdfY = uy; break;
            case 3: pdfX = origW - uy; pdfY = origH - ux; break;
            default: pdfX = ux; pdfY = origH - uy; break;
        }
        return new PvPoint { X = (float)pdfX, Y = (float)pdfY };
    }

    private static Rect PdfRectToDipRect(PvRect r, double origW, double origH, int rotation, double scale)
    {
        double uLeft, uTop, uRight, uBottom;
        switch (rotation & 3)
        {
            case 1: uLeft = r.Bottom; uRight = r.Top; uTop = r.Left; uBottom = r.Right; break;
            case 2: uLeft = origW - r.Right; uRight = origW - r.Left; uTop = r.Bottom; uBottom = r.Top; break;
            case 3: uLeft = origH - r.Top; uRight = origH - r.Bottom; uTop = origW - r.Right; uBottom = origW - r.Left; break;
            default: uLeft = r.Left; uRight = r.Right; uTop = origH - r.Top; uBottom = origH - r.Bottom; break;
        }
        return new Rect(
            Math.Min(uLeft, uRight) * scale,
            Math.Min(uTop, uBottom) * scale,
            Math.Abs(uRight - uLeft) * scale,
            Math.Abs(uBottom - uTop) * scale);
    }

    private bool HitTestFormField(
        int pageIndex,
        Point dipPos,
        DocumentSession session,
        out PdfFormField hitField,
        out Rect fieldDipRect,
        out bool onResizeHandle)
    {
        hitField = null!;
        fieldDipRect = default;
        onResizeHandle = false;

        var fields = session.GetFormFieldsForPage(pageIndex);
        if (fields.Count == 0) return false;

        var pageInfo = session.Pages[pageIndex];
        double scale = (96.0 / 72.0) * session.Zoom;

        for (int i = fields.Count - 1; i >= 0; i--)
        {
            var r = PdfRectToDipRect(fields[i].Rect, pageInfo.WidthPt, pageInfo.HeightPt, session.Rotation, scale);
            var hitBox = new Rect(r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6);
            if (hitBox.Contains(dipPos))
            {
                hitField = fields[i];
                fieldDipRect = r;
                onResizeHandle = dipPos.X >= r.Right - 10 && dipPos.Y >= r.Bottom - 10;
                return true;
            }
        }
        return false;
    }

    private bool HitTestAnnotation(
        int pageIndex,
        Point dipPos,
        DocumentSession session,
        out PdfAnnotationInfo hitAnnot,
        out Rect annotDipRect,
        out bool onResizeHandle)
    {
        hitAnnot = null!;
        annotDipRect = default;
        onResizeHandle = false;

        var annots = session.GetInteractiveAnnotationsForPage(pageIndex);
        if (annots.Count == 0) return false;

        var pageInfo = session.Pages[pageIndex];
        double scale = (96.0 / 72.0) * session.Zoom;

        for (int i = annots.Count - 1; i >= 0; i--)
        {
            var r = PdfRectToDipRect(annots[i].Rect, pageInfo.WidthPt, pageInfo.HeightPt, session.Rotation, scale);
            double w = Math.Max(16, r.Width);
            double h = Math.Max(16, r.Height);
            var hitBox = new Rect(r.X - 2, r.Y - 2, w + 4, h + 4);
            if (hitBox.Contains(dipPos))
            {
                hitAnnot = annots[i];
                annotDipRect = new Rect(r.X, r.Y, w, h);
                onResizeHandle = dipPos.X >= annotDipRect.Right - 10 && dipPos.Y >= annotDipRect.Bottom - 10;
                return true;
            }
        }
        return false;
    }

    private bool HitTestImageObject(
        int pageIndex,
        Point dipPos,
        DocumentSession session,
        out PdfPageObjectInfo hitImg,
        out Rect imgDipRect,
        out bool onResizeHandle)
    {
        hitImg = null!;
        imgDipRect = default;
        onResizeHandle = false;

        var images = session.GetImageObjectsForPage(pageIndex);
        if (images.Count == 0) return false;

        var pageInfo = session.Pages[pageIndex];
        double scale = (96.0 / 72.0) * session.Zoom;

        for (int i = images.Count - 1; i >= 0; i--)
        {
            var r = PdfRectToDipRect(images[i].Bounds, pageInfo.WidthPt, pageInfo.HeightPt, session.Rotation, scale);
            if (r.Width < 4 || r.Height < 4) continue;
            var hitBox = new Rect(r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6);
            if (hitBox.Contains(dipPos))
            {
                hitImg = images[i];
                imgDipRect = r;
                onResizeHandle = dipPos.X >= r.Right - 12 && dipPos.Y >= r.Bottom - 12;
                return true;
            }
        }
        return false;
    }

    private Rect ComputeMovedOrResizedObjectRect(Point currentDip, double maxW, double maxH)
    {
        double dx = currentDip.X - _dragStartDip.X;
        double dy = currentDip.Y - _dragStartDip.Y;

        if (_isResizingObject)
        {
            double newW = Math.Clamp(_draggedObjectStartDip.Width + dx, 14.0, Math.Max(14.0, maxW - _draggedObjectStartDip.X));
            double newH = Math.Clamp(_draggedObjectStartDip.Height + dy, 14.0, Math.Max(14.0, maxH - _draggedObjectStartDip.Y));
            return new Rect(_draggedObjectStartDip.X, _draggedObjectStartDip.Y, newW, newH);
        }

        double newX = Math.Clamp(_draggedObjectStartDip.X + dx, 0.0, Math.Max(0.0, maxW - _draggedObjectStartDip.Width));
        double newY = Math.Clamp(_draggedObjectStartDip.Y + dy, 0.0, Math.Max(0.0, maxH - _draggedObjectStartDip.Height));
        return new Rect(newX, newY, _draggedObjectStartDip.Width, _draggedObjectStartDip.Height);
    }
}
