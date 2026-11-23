using System.Reflection;
using System.Runtime.InteropServices;

namespace PDFVisor.Interop;

public sealed class PdfEngineException : Exception
{
    public PvStatus Status { get; }

    public PdfEngineException(PvStatus status, string message)
        : base(message)
    {
        Status = status;
    }
}

public static class PdfEngine
{
    private static readonly object ResolverLock = new();
    private static bool _resolverInstalled;
    private static nint _engineHandle;

    static PdfEngine()
    {
        EnsureNativeResolver();
    }

    private static void EnsureNativeResolver()
    {
        lock (ResolverLock)
        {
            if (_resolverInstalled) return;
            _resolverInstalled = true;

            NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, (libraryName, assembly, searchPath) =>
            {
                if (!string.Equals(libraryName, "PDFVisor.Engine.dll", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(libraryName, "PDFVisor.Engine", StringComparison.OrdinalIgnoreCase))
                {
                    return nint.Zero;
                }

                if (_engineHandle != nint.Zero)
                    return _engineHandle;

                string baseDir = AppContext.BaseDirectory;
                // Preload native dependencies from AppContext.BaseDirectory so Windows resolves them reliably
                // even when testhost.exe or another host runs from a different working directory.
                string[] deps = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "pdfium.dll", "qpdf30.dll"];
                foreach (string dep in deps)
                {
                    string depPath = Path.Combine(baseDir, dep);
                    if (File.Exists(depPath))
                    {
                        NativeLibrary.TryLoad(depPath, out _);
                    }
                }

                string enginePath = Path.Combine(baseDir, "PDFVisor.Engine.dll");
                if (File.Exists(enginePath))
                {
                    _engineHandle = NativeLibrary.Load(enginePath);
                    return _engineHandle;
                }

                return nint.Zero;
            });
        }
    }

    public static void Initialize()
    {
        EnsureNativeResolver();
        var status = NativeMethods.pv_engine_init();
        ThrowIfFailed(status);
    }

    public static void Shutdown()
    {
        NativeMethods.pv_engine_shutdown();
    }

    public static string Version =>
        Marshal.PtrToStringUTF8(NativeMethods.pv_engine_version()) ?? "unknown";

    public static string LastError =>
        Marshal.PtrToStringUTF8(NativeMethods.pv_get_last_error()) ?? string.Empty;

    internal static void ThrowIfFailed(PvStatus status)
    {
        if (status == PvStatus.Ok) return;
        var detail = LastError;
        var msg = string.IsNullOrWhiteSpace(detail)
            ? $"PDFVisor native engine error: {status}"
            : $"{status}: {detail}";
        throw new PdfEngineException(status, msg);
    }
}
