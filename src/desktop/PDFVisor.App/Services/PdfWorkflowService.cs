using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFVisor.Core;
using PDFVisor.Interop;

namespace PDFVisor.App.Services;

public static class PdfWorkflowService
{
    public static async Task ExportPageAsImageAsync(
        DocumentSession session,
        int pageIndex,
        string outputPath,
        double exportDpi = 200.0)
    {
        var pageInfo = session.Pages[pageIndex];
        bool swapped = (session.Rotation & 1) == 1;
        double wPt = swapped ? pageInfo.HeightPt : pageInfo.WidthPt;
        double hPt = swapped ? pageInfo.WidthPt : pageInfo.HeightPt;
        int rot = session.Rotation;

        await session.RenderGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                int wPx = Math.Clamp((int)Math.Round(wPt * (exportDpi / 72.0)), 1, 8192);
                int hPx = Math.Clamp((int)Math.Round(hPt * (exportDpi / 72.0)), 1, 8192);
                byte[] bgra = session.Document.RenderPageBgra(pageIndex, wPx, hPx, rotation: rot);

                var bmp = BitmapSource.Create(wPx, hPx, exportDpi, exportDpi, PixelFormats.Bgra32, null, bgra, wPx * 4);
                bmp.Freeze();
                BitmapEncoder encoder = outputPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                        outputPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    ? new JpegBitmapEncoder { QualityLevel = 92 }
                    : new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.Create(outputPath);
                encoder.Save(fs);
            });
        }
        finally
        {
            session.RenderGate.Release();
        }
    }

    public static Task CreatePdfFromImagesAsync(
        IReadOnlyList<string> files,
        string destPath,
        IProgress<(int Index, int Total, string FileName)>? progress = null)
    {
        return Task.Run(() =>
        {
            PdfDocument? created = null;
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    progress?.Report((i + 1, files.Count, Path.GetFileName(files[i])));
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(files[i]);
                    bmp.EndInit();
                    bmp.Freeze();

                    var formatted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
                    formatted.Freeze();
                    int w = formatted.PixelWidth;
                    int h = formatted.PixelHeight;
                    byte[] pixels = new byte[w * h * 4];
                    formatted.CopyPixels(pixels, w * 4, 0);

                    double wPt = w * 72.0 / 96.0;
                    double hPt = h * 72.0 / 96.0;
                    int pageIdx;
                    if (created is null)
                    {
                        created = PdfDocument.CreateBlank(wPt, hPt);
                        pageIdx = 0;
                    }
                    else
                    {
                        pageIdx = created.AppendBlankPage(wPt, hPt);
                    }
                    created.AddImageBgra(pageIdx, pixels, w, h, new PvRect { Left = 0, Bottom = 0, Right = wPt, Top = hPt });
                }

                created?.SaveAs(destPath, incremental: false);
            }
            finally
            {
                created?.Dispose();
            }
        });
    }
}
