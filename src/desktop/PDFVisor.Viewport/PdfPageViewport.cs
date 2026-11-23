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
/// and per-page 60-120Hz vector overlays for search highlights and interactive annotations/redactions.
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
        public Dictionary<TileKey, Image> MountedTiles { get; } = new();
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

    private static readonly Brush TextSelectionBrush = new SolidColorBrush(Color.FromArgb(95, 0, 120, 215));
    private static readonly Brush MatchFillBrush = new SolidColorBrush(Color.FromArgb(95, 255, 215, 0));
    private static readonly Brush ActiveMatchFillBrush = new SolidColorBrush(Color.FromArgb(145, 255, 140, 0));
    private static readonly Brush ActiveMatchStrokeBrush = new SolidColorBrush(Color.FromArgb(220, 210, 90, 0));
    private static readonly Brush HighlightPreviewBrush = new SolidColorBrush(Color.FromArgb(110, 255, 234, 0));
    private static readonly Brush SquarePreviewStroke = new SolidColorBrush(Color.FromRgb(229, 57, 53));
    private static readonly Brush InkPreviewStroke = new SolidColorBrush(Color.FromRgb(30, 136, 229));
    private static readonly Brush RedactPreviewFill = new SolidColorBrush(Color.FromArgb(140, 20, 20, 20));
    private static readonly Brush RedactPreviewStroke = new SolidColorBrush(Color.FromRgb(220, 38, 38));

    static PdfPageViewport()
    {
        TextSelectionBrush.Freeze();
        MatchFillBrush.Freeze();
        ActiveMatchFillBrush.Freeze();
        ActiveMatchStrokeBrush.Freeze();
        HighlightPreviewBrush.Freeze();
        SquarePreviewStroke.Freeze();
        InkPreviewStroke.Freeze();
        RedactPreviewFill.Freeze();
        RedactPreviewStroke.Freeze();
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
    public Func<string?>? PromptAddText { get; set; }

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
        _scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
        _scrollViewer.SizeChanged += (_, _) => UpdateVisiblePages();

        Content = _scrollViewer;
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Session is null) return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            Session.Zoom *= e.Delta > 0 ? 1.15 : (1.0 / 1.15);
            e.Handled = true;
        }
    }

    private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Session is null || _layoutSlots.Length == 0) return;

        UpdateVisiblePages();

        // Sync CurrentPageIndex with the page currently intersecting the viewport vertical center
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
            int anchorPage = Session.CurrentPageIndex;
            if (e.PropertyName is nameof(DocumentSession.DocumentRevision) or nameof(DocumentSession.DarkReadingMode))
            {
                _tileCache.Clear();
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
            // First compute max row width so every spread is centered cleanly
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
                    LeftDip = -1, // centered below
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

        // 1. Virtualize out: remove page containers outside the visible window
        var toRemove = _activePages.Keys.Where(idx => !neededPages.Contains(idx)).ToList();
        foreach (int idx in toRemove)
        {
            _documentCanvas.Children.Remove(_activePages[idx].Border);
            _activePages.Remove(idx);
        }

        // 2. Mount visible pages and render only their visible 512x512 tiles
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

        var surfaceGrid = new Grid
        {
            Width = slot.WidthDip,
            Height = slot.HeightDip,
            Background = Brushes.Transparent,
            Cursor = Session?.ActiveTool is AnnotationTool.None or null ? Cursors.Arrow : Cursors.Cross
        };
        surfaceGrid.Children.Add(tileCanvas);
        surfaceGrid.Children.Add(overlayCanvas);

        var border = new Border
        {
            Background = Brushes.White,
            Child = surfaceGrid,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 3,
                Opacity = 0.28,
                Color = Colors.Black
            }
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
            OverlayCanvas = overlayCanvas
        };

        surfaceGrid.MouseLeftButtonDown += (_, e) => OnPageMouseLeftButtonDown(visual, e);
        surfaceGrid.MouseMove += (_, e) => OnPageMouseMove(visual, e);
        surfaceGrid.MouseLeftButtonUp += (_, e) => OnPageMouseLeftButtonUp(visual, e);

        return visual;
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

        // Visible sub-region in page-local pixel coordinates
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

        // Unmount off-screen tiles from this page's visual tree (they remain in LRU cache!)
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
            byte[] bgra = await Task.Run(() =>
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
                    // Smart dark reading inversion (maps white paper -> #1E1F22, black text -> #EAECEF)
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        pixels[i] = (byte)(255 - pixels[i] * 221 / 255);         // B
                        pixels[i + 1] = (byte)(255 - pixels[i + 1] * 224 / 255); // G
                        pixels[i + 2] = (byte)(255 - pixels[i + 2] * 225 / 255); // R
                    }
                }
                return pixels;
            }, ct);

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
        var cursor = Session?.ActiveTool is AnnotationTool.None or null ? Cursors.Arrow : Cursors.Cross;
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

    private void RenderHighlightsForPage(ActivePageVisual visual)
    {
        visual.OverlayCanvas.Children.Clear();
        var session = Session;
        if (session is null || session.PageCount == 0) return;

        var pageInfo = session.Pages[visual.PageIndex];
        double origW = pageInfo.WidthPt;
        double origH = pageInfo.HeightPt;
        double scale = (96.0 / 72.0) * session.Zoom;
        int rot = session.Rotation;

        // 1. Render interactive text selection rects if present on this page
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

        if (visual.PageIndex != session.CurrentPageIndex)
            return;

        // 2. Render search match highlights
        var highlights = session.CurrentPageHighlights;
        if (highlights.Count == 0) return;

        var active = session.ActiveMatchOnCurrentPage;

        foreach (var r in highlights)
        {
            var dipRect = PdfRectToDipRect(r, origW, origH, rot, scale);
            bool isActive = active.HasValue &&
                Math.Abs(active.Value.Left - r.Left) < 0.01 &&
                Math.Abs(active.Value.Top - r.Top) < 0.01 &&
                Math.Abs(active.Value.Right - r.Right) < 0.01 &&
                Math.Abs(active.Value.Bottom - r.Bottom) < 0.01;

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

        Point pos = e.GetPosition(visual.SurfaceGrid);

        // Cursor mode -> Interactive Text Selection!
        if (session.ActiveTool == AnnotationTool.None)
        {
            ClearTextSelection();
            var pt = DipPointToPdfPoint(pos, visual.PageIndex, session);
            int charIdx = session.Document.GetCharIndexAtPos(visual.PageIndex, pt.X, pt.Y, tolerancePt: 12.0);
            if (charIdx >= 0)
            {
                _isSelectingText = true;
                _drawingPage = visual;
                _selPageIndex = visual.PageIndex;
                _selAnchorChar = charIdx;
                _selStartChar = charIdx;
                _selCharCount = 1;
                _selRects = session.Document.GetTextRangeRects(visual.PageIndex, charIdx, 1);
                visual.SurfaceGrid.CaptureMouse();
            }
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
                session.AddTextObject(newText, pt.X, pt.Y, "Helvetica", session.ActiveFontSize, session.ActiveColorRgba);
            }
            e.Handled = true;
            return;
        }

        _isDrawing = true;
        _drawingPage = visual;
        _dragStartDip = pos;
        visual.SurfaceGrid.CaptureMouse();

        if (session.ActiveTool is AnnotationTool.Highlight or AnnotationTool.Square or AnnotationTool.Redact)
        {
            _previewRect = new Rectangle
            {
                Fill = session.ActiveTool switch
                {
                    AnnotationTool.Highlight => HighlightPreviewBrush,
                    AnnotationTool.Redact => RedactPreviewFill,
                    _ => Brushes.Transparent
                },
                Stroke = session.ActiveTool switch
                {
                    AnnotationTool.Square => SquarePreviewStroke,
                    AnnotationTool.Redact => RedactPreviewStroke,
                    _ => null
                },
                StrokeThickness = session.ActiveTool == AnnotationTool.Highlight ? 0 : 2.0,
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

    private void OnPageMouseMove(ActivePageVisual visual, MouseEventArgs e)
    {
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

        if (!_isDrawing || !ReferenceEquals(_drawingPage, visual) || Session is null) return;
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

    private void OnPageMouseLeftButtonUp(ActivePageVisual visual, MouseButtonEventArgs e)
    {
        if (_isSelectingText && ReferenceEquals(_drawingPage, visual))
        {
            _isSelectingText = false;
            _drawingPage = null;
            visual.SurfaceGrid.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (!_isDrawing || !ReferenceEquals(_drawingPage, visual) || Session is null) return;

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

            if (Math.Abs(endPos.X - _dragStartDip.X) >= 4 && Math.Abs(endPos.Y - _dragStartDip.Y) >= 4)
            {
                var p1 = DipPointToPdfPoint(_dragStartDip, visual.PageIndex, Session);
                var p2 = DipPointToPdfPoint(endPos, visual.PageIndex, Session);
                var pdfRect = new PvRect
                {
                    Left = Math.Min(p1.X, p2.X),
                    Right = Math.Max(p1.X, p2.X),
                    Bottom = Math.Min(p1.Y, p2.Y),
                    Top = Math.Max(p1.Y, p2.Y)
                };

                if (Session.ActiveTool == AnnotationTool.Highlight)
                {
                    uint hlColor = (Session.ActiveColorRgba & 0xFFFFFF00u) | 0x80u; // semi-transparent highlight
                    Session.AddHighlight(pdfRect, hlColor);
                }
                else if (Session.ActiveTool == AnnotationTool.Square) Session.AddSquare(pdfRect, Session.ActiveColorRgba);
                else if (Session.ActiveTool == AnnotationTool.Redact) Session.ApplyPermanentRedaction(pdfRect);
            }
        }
        else if (_previewInk is not null)
        {
            visual.OverlayCanvas.Children.Remove(_previewInk);
            _previewInk = null;

            if (_inkPointsDip.Count >= 2)
            {
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
}
