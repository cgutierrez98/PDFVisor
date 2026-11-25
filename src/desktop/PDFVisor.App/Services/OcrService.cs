using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFVisor.Core;
using PDFVisor.Interop;

namespace PDFVisor.App.Services;

public static class OcrService
{
    public static string? ResolveWorkerExecutable()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "PDFVisor.OcrWorker.exe"),
            Path.Combine(baseDir, "workers", "PDFVisor.OcrWorker.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "workers", "PDFVisor.OcrWorker", "bin", "Release", "net10.0-windows10.0.19041.0", "PDFVisor.OcrWorker.exe")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "workers", "PDFVisor.OcrWorker", "bin", "Debug", "net10.0-windows10.0.19041.0", "PDFVisor.OcrWorker.exe"))
        ];

        foreach (string c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    public static async Task<int> RecognizePageAsync(
        DocumentSession session,
        int pageIndex,
        CancellationToken ct = default)
    {
        string? workerExe = ResolveWorkerExecutable()
            ?? throw new FileNotFoundException("No se encontró el módulo PDFVisor.OcrWorker.exe.");

        string tempPng = Path.Combine(Path.GetTempPath(), $"pdfvisor_ocr_{Guid.NewGuid():N}.png");
        try
        {
            var (wPx, hPx) = await RenderPageToPngAsync(session, pageIndex, tempPng, ct);
            var psi = new ProcessStartInfo(workerExe, $"--backend auto \"{tempPng}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("No se pudo iniciar el proceso OCR.");
            string json = await proc.StandardOutput.ReadToEndAsync(ct);
            string err = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? "Fallo desconocido en OCR." : err.Trim());

            var pageInfo = session.Pages[pageIndex];
            var ocrWords = ParseOcrWords(json, pageInfo.WidthPt, pageInfo.HeightPt, wPx, hPx);
            return session.AddInvisibleOcrWordsBatch(pageIndex, ocrWords);
        }
        finally
        {
            if (File.Exists(tempPng)) try { File.Delete(tempPng); } catch { }
        }
    }

    public static async Task<int> RecognizeAllPagesAsync(
        DocumentSession session,
        IProgress<(int PageNumber, int TotalPages, int WordsSoFar)>? progress = null,
        CancellationToken ct = default)
    {
        string? workerExe = ResolveWorkerExecutable()
            ?? throw new FileNotFoundException("No se encontró el módulo PDFVisor.OcrWorker.exe.");

        int totalPages = session.PageCount;
        if (totalPages == 0) return 0;

        var psi = new ProcessStartInfo(workerExe, "--backend auto --stdin-loop")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("No se pudo iniciar el proceso OCR por lotes.");

        var perPageWords = new List<(int PageIndex, List<PdfOcrWord> Words)>(totalPages);
        int totalWords = 0;

        try
        {
            for (int p = 0; p < totalPages; p++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((p + 1, totalPages, totalWords));

                string tempPng = Path.Combine(Path.GetTempPath(), $"pdfvisor_ocr_batch_{Guid.NewGuid():N}.png");
                try
                {
                    var (wPx, hPx) = await RenderPageToPngAsync(session, p, tempPng, ct);
                    await proc.StandardInput.WriteLineAsync(tempPng);
                    await proc.StandardInput.FlushAsync(ct);

                    string? jsonLine = await proc.StandardOutput.ReadLineAsync(ct)
                        ?? throw new InvalidOperationException("El proceso OCR finalizó inesperadamente.");

                    using var doc = JsonDocument.Parse(jsonLine);
                    if (doc.RootElement.TryGetProperty("Error", out var errProp))
                        throw new InvalidOperationException(errProp.GetString() ?? "Error en OCR.");

                    var pageInfo = session.Pages[p];
                    var words = ParseOcrWords(jsonLine, pageInfo.WidthPt, pageInfo.HeightPt, wPx, hPx);
                    if (words.Count > 0)
                    {
                        perPageWords.Add((p, words));
                        totalWords += words.Count;
                    }
                }
                finally
                {
                    if (File.Exists(tempPng)) try { File.Delete(tempPng); } catch { }
                }
            }

            await proc.StandardInput.WriteLineAsync("EXIT");
            await proc.StandardInput.FlushAsync(ct);
            await proc.WaitForExitAsync(ct);
        }
        finally
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(); } catch { }
            }
        }

        if (perPageWords.Count > 0)
        {
            string? ttf = DocumentSession.FindWindowsUnicodeFont();
            await Task.Run(() =>
            {
                session.History.Execute(new BatchGeneratedObjectsCommand(
                    session.Document,
                    $"OCR completo ({totalPages} páginas)",
                    doc =>
                    {
                        var inserted = new List<(int PageIndex, int FirstObjIndex, int Count)>(perPageWords.Count);
                        foreach (var (pageIdx, words) in perPageWords)
                        {
                            var (firstIdx, added) = doc.AddInvisibleOcrTextBatch(pageIdx, words, ttf);
                            if (added > 0) inserted.Add((pageIdx, firstIdx, added));
                        }
                        return inserted;
                    }));
            }, ct);

            // Trigger single session refresh via dummy 0-word batch or direct page index notification
            session.CurrentPageIndex = Math.Clamp(session.CurrentPageIndex, 0, session.PageCount - 1);
            session.RefreshCurrentPageObjects();
        }

        return totalWords;
    }

    private static async Task<(int WidthPx, int HeightPx)> RenderPageToPngAsync(
        DocumentSession session,
        int pageIndex,
        string pngPath,
        CancellationToken ct)
    {
        var pageInfo = session.Pages[pageIndex];
        int wPx = Math.Clamp((int)Math.Round(pageInfo.WidthPt * (200.0 / 72.0)), 1, 8192);
        int hPx = Math.Clamp((int)Math.Round(pageInfo.HeightPt * (200.0 / 72.0)), 1, 8192);

        await session.RenderGate.WaitAsync(ct);
        byte[] bgra;
        try
        {
            bgra = await Task.Run(() => session.Document.RenderPageBgra(pageIndex, wPx, hPx), ct);
        }
        finally
        {
            session.RenderGate.Release();
        }

        await Task.Run(() =>
        {
            var bmp = BitmapSource.Create(wPx, hPx, 200, 200, PixelFormats.Bgra32, null, bgra, wPx * 4);
            bmp.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(pngPath);
            encoder.Save(fs);
        }, ct);

        return (wPx, hPx);
    }

    private static List<PdfOcrWord> ParseOcrWords(string json, double widthPt, double heightPt, int wPx, int hPx)
    {
        using var docJson = JsonDocument.Parse(json);
        var wordsElem = docJson.RootElement.GetProperty("Words");
        double scaleX = widthPt / Math.Max(1, wPx);
        double scaleY = heightPt / Math.Max(1, hPx);

        var list = new List<PdfOcrWord>();
        foreach (var w in wordsElem.EnumerateArray())
        {
            string txt = w.GetProperty("Text").GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(txt)) continue;
            double xPt = w.GetProperty("X").GetDouble() * scaleX;
            double yTopPx = w.GetProperty("Y").GetDouble();
            double hWordPx = w.GetProperty("Height").GetDouble();
            double yPt = heightPt - (yTopPx + hWordPx) * scaleY;
            float fontSize = (float)Math.Clamp(hWordPx * scaleY, 6.0, 48.0);
            list.Add(new PdfOcrWord(txt, xPt, yPt, fontSize));
        }
        return list;
    }
}
