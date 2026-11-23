using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
    private readonly IReadOnlyList<PageDiffItem> _diffs;
    private int _currentIndex;

    public ComparePdfDialog(PdfDocument leftDoc, PdfDocument rightDoc, bool ownsRightDoc = true, IReadOnlyList<PageDiffItem>? precomputedDiffs = null)
    {
        InitializeComponent();
        _leftDoc = leftDoc;
        _rightDoc = rightDoc;
        _ownsRightDoc = ownsRightDoc;

        TxtLeftName.Text = $"Documento A: {Path.GetFileName(leftDoc.FilePath)}";
        TxtRightName.Text = $"Documento B: {Path.GetFileName(rightDoc.FilePath)}";

        _diffs = precomputedDiffs ?? PdfDiffService.CompareDocuments(leftDoc, rightDoc);
        DiffList.ItemsSource = _diffs;

        int changed = _diffs.Count(d => d.Status != PageDiffStatus.Identical);
        TxtSummary.Text = changed == 0
            ? $"Documentos idénticos en texto ({_diffs.Count} páginas comparadas)"
            : $"{changed} de {_diffs.Count} página(s) con diferencias";

        if (_diffs.Count > 0)
        {
            DiffList.SelectedIndex = 0;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_ownsRightDoc) _rightDoc.Dispose();
    }

    private void DiffList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DiffList.SelectedIndex >= 0)
        {
            ShowPage(DiffList.SelectedIndex);
        }
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex > 0) DiffList.SelectedIndex = _currentIndex - 1;
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < _diffs.Count - 1) DiffList.SelectedIndex = _currentIndex + 1;
    }

    private void ShowPage(int index)
    {
        _currentIndex = index;
        var item = _diffs[index];
        TxtPageLabel.Text = $"Página {item.PageNumber} / {_diffs.Count}";

        ImgLeft.Source = index < _leftDoc.PageCount ? RenderThumb(_leftDoc, index) : null;
        ImgRight.Source = index < _rightDoc.PageCount ? RenderThumb(_rightDoc, index) : null;

        var sb = new StringBuilder();
        if (item.Status == PageDiffStatus.Identical)
        {
            sb.AppendLine("Sin diferencias de texto detectadas en esta página.");
        }
        else
        {
            foreach (var r in item.RemovedLines) sb.AppendLine($"- {r}");
            foreach (var a in item.AddedLines) sb.AppendLine($"+ {a}");
        }
        TxtDiffDetails.Text = sb.ToString();
    }

    private static BitmapSource RenderThumb(PdfDocument doc, int pageIndex)
    {
        var size = doc.GetPageSize(pageIndex);
        int w = 520;
        int h = Math.Clamp((int)Math.Round(w * (size.HeightPt / Math.Max(1.0, size.WidthPt))), 100, 900);
        byte[] bgra = doc.RenderPageBgra(pageIndex, w, h);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
