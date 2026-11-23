using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using PDFVisor.Interop;

namespace PDFVisor.App;

public partial class App : Application
{
    private const string MutexName = "Local\\PDFVisor_SingleInstance_Mutex";
    private const string PipeName = "PDFVisor_SingleInstance_Pipe";

    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _pipeCts;

    public static string[] StartupFiles { get; private set; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupFiles = e.Args.Where(a => !string.IsNullOrWhiteSpace(a) && File.Exists(a)).ToArray();

        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            ForwardArgsToRunningInstance(StartupFiles);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        PdfEngine.Initialize();
        StartSingleInstancePipeListener();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        base.OnExit(e);
        if (_singleInstanceMutex is not null)
        {
            PdfEngine.Shutdown();
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
        }
    }

    private static void ForwardArgsToRunningInstance(string[] files)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: false);
            foreach (string f in files)
            {
                writer.WriteLine(Path.GetFullPath(f));
            }
            writer.Flush();
        }
        catch
        {
            // Ignore if primary instance is closing
        }
    }

    private void StartSingleInstancePipeListener()
    {
        _pipeCts = new CancellationTokenSource();
        var ct = _pipeCts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var incoming = new List<string>();
                    while (await reader.ReadLineAsync(ct) is string line)
                    {
                        if (!string.IsNullOrWhiteSpace(line) && File.Exists(line))
                            incoming.Add(line);
                    }

                    Dispatcher.Invoke(() =>
                    {
                        if (MainWindow is MainWindow mw)
                        {
                            mw.ReceiveExternalFiles(incoming);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(250, ct);
                }
            }
        }, ct);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string logPath = WriteCrashLog(e.Exception);
        MessageBox.Show(
            $"Se produjo un error inesperado:\n{e.Exception.Message}\n\nSe ha guardado un registro en:\n{logPath}",
            "Error inesperado — PDFVisor",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            WriteCrashLog(ex);
        }
    }

    private static string WriteCrashLog(Exception ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PDFVisor",
                "logs");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"crash_{DateTime.Now:yyyyMMdd}.log");
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n{new string('-', 72)}\n";
            File.AppendAllText(path, entry, Encoding.UTF8);
            return path;
        }
        catch
        {
            return "(no disponible)";
        }
    }
}
