using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFVisor.Core;

namespace PDFVisor.App;

/// <summary>
/// Native Windows printing service backed by WPF PrintDialog and a 300-DPI DocumentPaginator over PDFium.
/// Honors user-selected page ranges, view rotation, and automatic landscape-to-paper orientation matching.
/// </summary>
public static class PdfPrintService
{
    private sealed class PdfDocumentPaginator : DocumentPaginator
    {
        private readonly DocumentSession _session;
        private readonly int[] _pageIndices;
        private Size _pageSize;

        public PdfDocumentPaginator(DocumentSession session, int[] pageIndices, Size printableAreaSize)
        {
            _session = session;
            _pageIndices = pageIndices;
            _pageSize = printableAreaSize;
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => _pageIndices.Length;

        public override Size PageSize
        {
            get => _pageSize;
            set => _pageSize = value;
        }

        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            int actualPageIndex = _pageIndices[pageNumber];
            var info = _session.Pages[actualPageIndex];
            const double printDpi = 300.0;

            int rot = _session.Rotation & 3;
            double effWidthPt = (rot % 2 == 1) ? info.HeightPt : info.WidthPt;
            double effHeightPt = (rot % 2 == 1) ? info.WidthPt : info.HeightPt;

            // Auto-rotate 90° when page aspect (landscape vs portrait) mismatches printable sheet orientation
            bool pageIsLandscape = effWidthPt > effHeightPt * 1.05;
            bool paperIsLandscape = _pageSize.Width > _pageSize.Height * 1.05;
            if (pageIsLandscape != paperIsLandscape)
            {
                rot = (rot + 1) & 3;
                (effWidthPt, effHeightPt) = (effHeightPt, effWidthPt);
            }

            int wPx = Math.Clamp((int)Math.Round(effWidthPt * (printDpi / 72.0)), 1, 6000);
            int hPx = Math.Clamp((int)Math.Round(effHeightPt * (printDpi / 72.0)), 1, 6000);

            byte[] bgra = _session.Document.RenderPageBgra(actualPageIndex, wPx, hPx, rotation: rot, renderAnnotations: true, lcdText: false);
            var bmp = BitmapSource.Create(wPx, hPx, printDpi, printDpi, PixelFormats.Bgra32, null, bgra, wPx * 4);
            bmp.Freeze();

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                double pageDipW = effWidthPt * (96.0 / 72.0);
                double pageDipH = effHeightPt * (96.0 / 72.0);
                double scale = Math.Min(_pageSize.Width / pageDipW, _pageSize.Height / pageDipH);
                double drawW = pageDipW * scale;
                double drawH = pageDipH * scale;
                double offsetX = (_pageSize.Width - drawW) * 0.5;
                double offsetY = (_pageSize.Height - drawH) * 0.5;
                dc.DrawImage(bmp, new Rect(offsetX, offsetY, drawW, drawH));
            }

            return new DocumentPage(visual, _pageSize, new Rect(_pageSize), new Rect(_pageSize));
        }
    }

    public static bool Print(DocumentSession session)
    {
        if (session.PageCount == 0) return false;

        var dlg = new PrintDialog
        {
            UserPageRangeEnabled = true,
            CurrentPageEnabled = true,
            MinPage = 1,
            MaxPage = (uint)session.PageCount
        };
        if (dlg.ShowDialog() != true) return false;

        int[] pagesToPrint = dlg.PageRangeSelection switch
        {
            PageRangeSelection.CurrentPage => [session.CurrentPageIndex],
            PageRangeSelection.UserPages => BuildClampedRange(dlg.PageRange.PageFrom, dlg.PageRange.PageTo, session.PageCount),
            _ => Enumerable.Range(0, session.PageCount).ToArray()
        };

        if (pagesToPrint.Length == 0) return false;

        var paginator = new PdfDocumentPaginator(session, pagesToPrint, new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight));
        dlg.PrintDocument(paginator, session.FileName);
        return true;
    }

    private static int[] BuildClampedRange(int fromOneBased, int toOneBased, int totalPages)
    {
        int start = Math.Clamp(Math.Min(fromOneBased, toOneBased) - 1, 0, totalPages - 1);
        int end = Math.Clamp(Math.Max(fromOneBased, toOneBased) - 1, 0, totalPages - 1);
        return Enumerable.Range(start, end - start + 1).ToArray();
    }
}
