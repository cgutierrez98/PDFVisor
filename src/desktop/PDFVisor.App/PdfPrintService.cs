using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFVisor.Core;

namespace PDFVisor.App;

/// <summary>
/// Native Windows printing service backed by WPF PrintDialog and a 300-DPI DocumentPaginator over PDFium.
/// </summary>
public static class PdfPrintService
{
    private sealed class PdfDocumentPaginator : DocumentPaginator
    {
        private readonly DocumentSession _session;
        private Size _pageSize;

        public PdfDocumentPaginator(DocumentSession session, Size printableAreaSize)
        {
            _session = session;
            _pageSize = printableAreaSize;
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => _session.PageCount;

        public override Size PageSize
        {
            get => _pageSize;
            set => _pageSize = value;
        }

        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            var info = _session.Pages[pageNumber];
            const double printDpi = 300.0;
            int wPx = Math.Clamp((int)Math.Round(info.WidthPt * (printDpi / 72.0)), 1, 6000);
            int hPx = Math.Clamp((int)Math.Round(info.HeightPt * (printDpi / 72.0)), 1, 6000);

            byte[] bgra = _session.Document.RenderPageBgra(pageNumber, wPx, hPx, rotation: 0, renderAnnotations: true, lcdText: false);
            var bmp = BitmapSource.Create(wPx, hPx, printDpi, printDpi, PixelFormats.Bgra32, null, bgra, wPx * 4);
            bmp.Freeze();

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                double scaleX = _pageSize.Width / (info.WidthPt * 96.0 / 72.0);
                double scaleY = _pageSize.Height / (info.HeightPt * 96.0 / 72.0);
                double scale = Math.Min(scaleX, scaleY);
                double drawW = (info.WidthPt * 96.0 / 72.0) * scale;
                double drawH = (info.HeightPt * 96.0 / 72.0) * scale;
                double offsetX = (_pageSize.Width - drawW) * 0.5;
                double offsetY = (_pageSize.Height - drawH) * 0.5;
                dc.DrawImage(bmp, new Rect(offsetX, offsetY, drawW, drawH));
            }

            return new DocumentPage(visual, _pageSize, new Rect(_pageSize), new Rect(_pageSize));
        }
    }

    public static bool Print(DocumentSession session)
    {
        var dlg = new PrintDialog { UserPageRangeEnabled = true };
        if (dlg.ShowDialog() != true) return false;

        var paginator = new PdfDocumentPaginator(session, new Size(dlg.PrintableAreaWidth, dlg.PrintableAreaHeight));
        dlg.PrintDocument(paginator, session.FileName);
        return true;
    }
}
