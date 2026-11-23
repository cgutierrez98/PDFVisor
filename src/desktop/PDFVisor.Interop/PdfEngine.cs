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
    public static void Initialize()
    {
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
