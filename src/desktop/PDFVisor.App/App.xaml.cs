using System.Windows;
using PDFVisor.Interop;

namespace PDFVisor.App;

public partial class App : Application
{
    public static string[] StartupFiles { get; private set; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        PdfEngine.Initialize();
        StartupFiles = e.Args.Where(a => !string.IsNullOrWhiteSpace(a) && System.IO.File.Exists(a)).ToArray();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        PdfEngine.Shutdown();
    }
}
