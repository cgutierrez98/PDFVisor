using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFVisor.Core;
using PDFVisor.Interop;

namespace PDFVisor.App.Dialogs;

public partial class ComparePdfDialog : Window
{
    private readonly PdfDocument _leftDoc;
    private readonly PdfDocument _rightDoc;
    private readonly bool _ownsRightDoc;
    private readonly IReadOnlyList<PageDiffItem> _allDiffs;
    private IReadOnlyList<PageDiffItem> _visibleDiffs;
    private readonly string _rightBaseLabel;
    private CancellationTokenSource? _renderCts;
    private double _zoom = 1.0;
    private bool _syncingScroll;

    private static readonly Brush RemovedBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38));
    private static readonly Brush AddedBrush = new SolidColorBrush(Color.FromRgb(22, 163, 74));
    private static readonly Brush ContextBrush = new SolidColorBrush(Color.FromArgb(170, 128, 128, 128));

    static ComparePdfDialog()
    {
        RemovedBrush.Freeze();
        AddedBrush.Freeze();
        ContextBrush.Freeze();
    }

    public ComparePdfDialog(PdfDocument leftDoc, PdfDocument rightDoc, bool ownsRightDoc = true, IReadOnlyList<PageDiffItem>? precomputedDiffs = null)
    {
        InitializeComponent();
        _leftDoc = leftDoc;
        _rightDoc = rightDoc;
        _ownsRightDoc = ownsRightDoc;

        TxtLeftName.Text = $"Documento A: {Path.GetFileName(leftDoc.FilePath)}";
        _rightBaseLabel = $"Documento B: {Path.GetFileName(rightDoc.FilePath)}";
        TxtRightName.Text = _rightBaseLabel;

        _allDiffs = precomputedDiffs ?? PdfDiffService.CompareDocuments(leftDoc, rightDoc);
        _visibleDiffs = _allDiffs;
        DiffList.ItemsSource = _visibleDiffs;

        int changed = _allDiffs.Count(d => d.Status != PageDiffStatus.Identical);
        TxtSummary.Text = changed == 0
            ? $"Documentos idénticos en texto ({_allDiffs.Count} páginas comparadas)"
            : $"{changed} de {_allDiffs.Count} página(s) con diferencias de texto";

        if (_visibleDiffs.Count > 0)
        {
            DiffList.SelectedIndex = 0;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        if (_ownsRightDoc) _rightDoc.Dispose();
    }

    private void ChkFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_allDiffs is null) return;
        _visibleDiffs = ChkOnlyChanges.IsChecked == true
            ? _allDiffs.Where(d => d.Status != PageDiffStatus.Identical).ToList()
            : _allDiffs;
        DiffList.ItemsSource = _visibleDiffs;
        if (_visibleDiffs.Count > 0)
        {
            DiffList.SelectedIndex = 0;
        }
        else
        {
            ImgLeft.Source = null;
            ImgRight.Source = null;
            TxtPageLabel.Text = "0 / 0";
            TxtDiffDetails.Text = "No hay páginas con diferencias de texto.";
        }
    }

    private void ChkVisualDiff_Changed(object sender, RoutedEventArgs e) => RefreshCurrentPageAsync();

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _zoom = Math.Clamp(_zoom * 1.25, 0.5, 3.0);
        TxtZoom.Text = $"{Math.Round(_zoom * 100)}%";
        RefreshCurrentPageAsync();
    }

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _zoom = Math.Clamp(_zoom / 1.25, 0.5, 3.0);
        TxtZoom.Text = $"{Math.Round(_zoom * 100)}%";
        RefreshCurrentPageAsync();
    }

    private void ScrollLeft_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll) return;
        _syncingScroll = true;
        try
        {
            ScrollRight.ScrollToHorizontalOffset(ScrollLeft.HorizontalOffset);
            ScrollRight.ScrollToVerticalOffset(ScrollLeft.VerticalOffset);
        }
        finally { _syncingScroll = false; }
    }

    private void ScrollRight_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll) return;
        _syncingScroll = true;
        try
        {
            ScrollLeft.ScrollToHorizontalOffset(ScrollRight.HorizontalOffset);
            ScrollLeft.ScrollToVerticalOffset(ScrollRight.VerticalOffset);
        }
        finally { _syncingScroll = false; }
    }

    private void DiffList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshCurrentPageAsync();
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (DiffList.SelectedIndex > 0) DiffList.SelectedIndex--;
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (DiffList.SelectedIndex >= 0 && DiffList.SelectedIndex < _visibleDiffs.Count - 1)
            DiffList.SelectedIndex++;
    }

    private async void RefreshCurrentPageAsync()
    {
        if (DiffList.SelectedItem is not PageDiffItem item) return;

        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderCts = new CancellationTokenSource();
        var ct = _renderCts.Token;

        int pageIndex = item.PageNumber - 1;
        TxtPageLabel.Text = $"Página {item.PageNumber} ({DiffList.SelectedIndex + 1} / {_visibleDiffs.Count})";
        PopulateUnifiedTextDiff(item);

        int renderWidth = (int)Math.Round(520 * _zoom);
        bool visualDiff = ChkVisualDiff.IsChecked == true;

        try
        {
            var (leftBmp, rightBmp, visualPct) = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                BitmapSource? l = pageIndex < _leftDoc.PageCount ? RenderThumb(_leftDoc, pageIndex, renderWidth) : null;
                ct.ThrowIfCancellationRequested();

                if (visualDiff)
                {
                    var (diffBgra, dw, dh, ratio) = PdfDiffService.RenderVisualDiffBgra(_leftDoc, _rightDoc, pageIndex, renderWidth);
                    var dbmp = BitmapSource.Create(dw, dh, 96, 96, PixelFormats.Bgra32, null, diffBgra, dw * 4);
                    dbmp.Freeze();
                    return (l, (BitmapSource?)dbmp, ratio * 100.0);
                }
                else
                {
                    BitmapSource? r = pageIndex < _rightDoc.PageCount ? RenderThumb(_rightDoc, pageIndex, renderWidth) : null;
                    return (l, r, -1.0);
                }
            }, ct);

            if (ct.IsCancellationRequested) return;
            ImgLeft.Source = leftBmp;
            ImgRight.Source = rightBmp;
            TxtRightName.Text = visualPct >= 0
                ? $"Mapa de diferencias visuales ({visualPct:0.00}% píxeles distintos)"
                : _rightBaseLabel;
        }
        catch (OperationCanceledException) { }
    }

    private void PopulateUnifiedTextDiff(PageDiffItem item)
    {
        TxtDiffDetails.Inlines.Clear();
        if (item.Status == PageDiffStatus.Identical)
        {
            TxtDiffDetails.Inlines.Add(new Run("Sin diferencias de texto detectadas en esta página (activa 'Diff visual por píxeles' para inspeccionar cambios gráficos o escaneos)."));
            return;
        }

        if (item.UnifiedLines is { Count: > 0 } unified)
        {
            foreach (var line in unified)
            {
                switch (line.Kind)
                {
                    case DiffLineKind.Removed:
                        TxtDiffDetails.Inlines.Add(new Run($"- {line.Text}\n") { Foreground = RemovedBrush });
                        break;
                    case DiffLineKind.Added:
                        TxtDiffDetails.Inlines.Add(new Run($"+ {line.Text}\n") { Foreground = AddedBrush });
                        break;
                    default:
                        TxtDiffDetails.Inlines.Add(new Run($"  {line.Text}\n") { Foreground = ContextBrush });
                        break;
                }
            }
        }
        else
        {
            foreach (var r in item.RemovedLines)
                TxtDiffDetails.Inlines.Add(new Run($"- {r}\n") { Foreground = RemovedBrush });
            foreach (var a in item.AddedLines)
                TxtDiffDetails.Inlines.Add(new Run($"+ {a}\n") { Foreground = AddedBrush });
        }
    }

    private static BitmapSource RenderThumb(PdfDocument doc, int pageIndex, int targetWidth)
    {
        var size = doc.GetPageSize(pageIndex);
        int w = Math.Clamp(targetWidth, 160, 2200);
        int h = Math.Clamp((int)Math.Round(w * (size.HeightPt / Math.Max(1.0, size.WidthPt))), 100, 3200);
        byte[] bgra = doc.RenderPageBgra(pageIndex, w, h);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
