using System.Text;
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
/// Built-in offline Windows WinRT OCR backend (zero external model downloads), caching OcrEngine across batch pages.
/// </summary>
public sealed class WindowsOcrBackend : IOcrBackend
{
    private readonly Lazy<OcrEngine?> _engine = new(OcrEngine.TryCreateFromUserProfileLanguages);

    public string Name => "Windows.Media.Ocr (Offline)";
    public bool IsAvailable => _engine.Value is not null;

    public async Task<OcrPageResult> RecognizePngAsync(string pngPath)
    {
        var engine = _engine.Value
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
/// Reports IsAvailable = false until an ONNX inference session is wired, ensuring safe automatic fallback to WindowsOcrBackend.
/// </summary>
public sealed class OnnxDirectMlOcrBackend : IOcrBackend
{
    private readonly string _modelsDir;

    public string Name => "ONNX Runtime + DirectML (RapidOCR)";

    public OnnxDirectMlOcrBackend(string? modelsDir = null)
    {
        _modelsDir = modelsDir ?? Path.Combine(AppContext.BaseDirectory, "models", "ocr");
    }

    public bool HasModelFiles =>
        File.Exists(Path.Combine(_modelsDir, "det.onnx")) &&
        File.Exists(Path.Combine(_modelsDir, "rec.onnx")) &&
        File.Exists(Path.Combine(_modelsDir, "dict.txt"));

    public bool IsRuntimeWired => false;

    public bool IsAvailable => IsRuntimeWired && HasModelFiles;

    public Task<OcrPageResult> RecognizePngAsync(string pngPath)
    {
        if (!HasModelFiles)
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
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        string backendMode = "auto";
        bool stdinLoop = false;
        string? imagePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--backend" && i + 1 < args.Length)
            {
                backendMode = args[++i].ToLowerInvariant();
            }
            else if (args[i] == "--stdin-loop")
            {
                stdinLoop = true;
            }
            else if (imagePath is null)
            {
                imagePath = args[i];
            }
        }

        var onnx = new OnnxDirectMlOcrBackend();
        var win = new WindowsOcrBackend();

        async Task<OcrPageResult> RunWithFallbackAsync(string path)
        {
            if (backendMode == "onnx")
                return await onnx.RecognizePngAsync(path);
            if (backendMode == "windows")
                return await win.RecognizePngAsync(path);

            if (onnx.IsAvailable)
            {
                try { return await onnx.RecognizePngAsync(path); }
                catch (NotSupportedException) { /* fallback to Windows OCR */ }
            }
            return await win.RecognizePngAsync(path);
        }

        // Persistent batch worker mode: processes multiple pages over stdin/stdout without process startup overhead
        if (stdinLoop)
        {
            while (await Console.In.ReadLineAsync() is string line)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "EXIT", StringComparison.OrdinalIgnoreCase))
                    break;

                try
                {
                    var res = await RunWithFallbackAsync(trimmed);
                    Console.WriteLine(JsonSerializer.Serialize(res));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { Error = ex.Message }));
                }
                await Console.Out.FlushAsync();
            }
            return 0;
        }

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            Console.Error.WriteLine("Usage: PDFVisor.OcrWorker [--backend auto|windows|onnx] [--stdin-loop | <image.png>]");
            return 2;
        }

        try
        {
            var result = await RunWithFallbackAsync(imagePath);
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
