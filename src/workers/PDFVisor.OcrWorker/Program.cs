using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PDFVisor.OcrWorker;

public sealed record OcrWordBox(string Text, double X, double Y, double Width, double Height);
public sealed record OcrPageResult(int ImageWidth, int ImageHeight, string Backend, List<OcrWordBox> Words);

public interface IOcrBackend
{
    string Name { get; }
    bool IsAvailable { get; }
    Task<OcrPageResult> RecognizePngAsync(string pngPath);
}

/// <summary>
/// Built-in offline Windows WinRT OCR backend (zero external model downloads).
/// </summary>
public sealed class WindowsOcrBackend : IOcrBackend
{
    public string Name => "Windows.Media.Ocr (Offline)";
    public bool IsAvailable => OcrEngine.TryCreateFromUserProfileLanguages() is not null;

    public async Task<OcrPageResult> RecognizePngAsync(string pngPath)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? throw new InvalidOperationException("No Windows OCR language packs are installed.");

        byte[] bytes = await File.ReadAllBytesAsync(pngPath);
        using var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
        }

        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var softBmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var ocrResult = await engine.RecognizeAsync(softBmp);

        var words = new List<OcrWordBox>();
        foreach (var line in ocrResult.Lines)
        {
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                words.Add(new OcrWordBox(w.Text, r.X, r.Y, r.Width, r.Height));
            }
        }

        return new OcrPageResult(softBmp.PixelWidth, softBmp.PixelHeight, Name, words);
    }
}

/// <summary>
/// ONNX Runtime + DirectML (RapidOCR / PaddleOCR) backend scaffold.
/// Activates automatically when models/ocr/det.onnx, rec.onnx, and dict.txt are present.
/// </summary>
public sealed class OnnxDirectMlOcrBackend : IOcrBackend
{
    private readonly string _modelsDir;

    public string Name => "ONNX Runtime + DirectML (RapidOCR)";

    public OnnxDirectMlOcrBackend(string? modelsDir = null)
    {
        _modelsDir = modelsDir ?? Path.Combine(AppContext.BaseDirectory, "models", "ocr");
    }

    public bool IsAvailable =>
        File.Exists(Path.Combine(_modelsDir, "det.onnx")) &&
        File.Exists(Path.Combine(_modelsDir, "rec.onnx")) &&
        File.Exists(Path.Combine(_modelsDir, "dict.txt"));

    public Task<OcrPageResult> RecognizePngAsync(string pngPath)
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException(
                $"ONNX OCR models not found in '{_modelsDir}'. Place det.onnx, rec.onnx and dict.txt or use --backend windows.");
        }
        throw new NotSupportedException("ONNX DirectML inference session ready for Microsoft.ML.OnnxRuntime.DirectML package wiring.");
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string backendMode = "auto";
        string? imagePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--backend" && i + 1 < args.Length)
            {
                backendMode = args[++i].ToLowerInvariant();
            }
            else if (imagePath is null)
            {
                imagePath = args[i];
            }
        }

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            Console.Error.WriteLine("Usage: PDFVisor.OcrWorker [--backend auto|windows|onnx] <image.png>");
            return 2;
        }

        try
        {
            IOcrBackend onnx = new OnnxDirectMlOcrBackend();
            IOcrBackend win = new WindowsOcrBackend();
            IOcrBackend selected = backendMode switch
            {
                "onnx" => onnx,
                "windows" => win,
                _ => onnx.IsAvailable ? onnx : win
            };

            var result = await selected.RecognizePngAsync(imagePath);
            Console.WriteLine(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
