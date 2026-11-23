using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PDFVisor.Core;
using PDFVisor.Interop;

namespace PDFVisor.App;

public partial class MainWindow : Window
{
    private readonly WorkspaceViewModel _workspace = new();
    private DocumentSession? _boundSession;
    private CancellationTokenSource? _thumbCts;

    public MainWindow()
    {
        InitializeComponent();
        DocumentTabs.ItemsSource = _workspace.OpenSessions;
        _workspace.PropertyChanged += Workspace_PropertyChanged;
        PdfViewport.PromptNoteText = () => PromptInput("Añadir nota adhesiva", "Texto de la nota:");
        PdfViewport.PromptAddText = () => PromptInput("Insertar texto en el PDF", "Contenido del texto:");
        StatusEngine.Text = $"Motor: PDFVisor.Engine {PdfEngine.Version} (PDFium + QPDF)";
        Loaded += (_, _) =>
        {
            foreach (var file in App.StartupFiles)
            {
                if (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    OpenFile(file);
            }
        };
        UpdateUiState();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _workspace.Dispose();
    }

    private void Workspace_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.ActiveSession))
        {
            BindActiveSession(_workspace.ActiveSession);
        }
    }

    private void BindActiveSession(DocumentSession? session)
    {
        if (_boundSession is not null)
        {
            _boundSession.PropertyChanged -= ActiveSession_PropertyChanged;
            _boundSession.History.PropertyChanged -= ActiveSession_PropertyChanged;
        }

        _boundSession = session;
        PdfViewport.Session = session;

        if (session is not null)
        {
            session.PropertyChanged += ActiveSession_PropertyChanged;
            session.History.PropertyChanged += ActiveSession_PropertyChanged;
            PageList.ItemsSource = session.Pages;
            BookmarkList.ItemsSource = session.Bookmarks;
            FormFieldList.ItemsSource = session.FormFields;
            SignatureList.ItemsSource = session.Signatures;
            PageObjectList.ItemsSource = session.CurrentPageObjects;
            DocumentTabs.SelectedItem = session;
            TxtSearch.Text = session.SearchQuery;
            _ = GenerateThumbnailsAsync(session);
        }
        else
        {
            _thumbCts?.Cancel();
            PageList.ItemsSource = null;
            BookmarkList.ItemsSource = null;
            FormFieldList.ItemsSource = null;
            SignatureList.ItemsSource = null;
            PageObjectList.ItemsSource = null;
            DocumentTabs.SelectedItem = null;
            TxtSearch.Text = string.Empty;
        }

        UpdateUiState();
    }

    private void ActiveSession_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.PropertyName == nameof(DocumentSession.DocumentRevision) && _boundSession is not null)
            {
                _ = GenerateThumbnailsAsync(_boundSession);
            }
            UpdateUiState();
        });
    }

    private async Task GenerateThumbnailsAsync(DocumentSession session)
    {
        _thumbCts?.Cancel();
        _thumbCts?.Dispose();
        _thumbCts = new CancellationTokenSource();
        var ct = _thumbCts.Token;

        const int maxThumbWidth = 140;
        var pagesSnapshot = session.Pages.ToArray();
        foreach (var page in pagesSnapshot)
        {
            if (ct.IsCancellationRequested) break;
            if (page.ThumbnailImage is not null) continue;

            double aspect = page.HeightPt / Math.Max(1.0, page.WidthPt);
            int w = maxThumbWidth;
            int h = Math.Clamp((int)Math.Round(w * aspect), 40, 260);

            try
            {
                byte[] bgra = await Task.Run(() =>
                    session.Document.RenderPageBgra(page.PageIndex, w, h, rotation: 0, lcdText: false), ct);

                if (ct.IsCancellationRequested) break;

                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
                bmp.Freeze();
                page.ThumbnailImage = bmp;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (PdfEngineException)
            {
                break;
            }
        }
    }

    private void OpenFile(string filePath)
    {
        try
        {
            _workspace.OpenDocument(filePath);
        }
        catch (PdfEngineException pex) when (pex.Status == PvStatus.PasswordRequired)
        {
            string? pwd = PromptInput("Documento protegido con contraseña", $"Introduce la contraseña para \"{System.IO.Path.GetFileName(filePath)}\":");
            if (string.IsNullOrEmpty(pwd)) return;
            try
            {
                _workspace.OpenDocument(filePath, pwd);
            }
            catch (Exception ex2)
            {
                MessageBox.Show(this, $"Contraseña incorrecta o error al abrir:\n{ex2.Message}", "Error al abrir PDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo abrir el PDF:\n{ex.Message}", "Error al abrir PDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateUiState()
    {
        var session = _boundSession;
        bool hasDoc = session is not null && session.PageCount > 0;
        bool multiPage = hasDoc && session!.PageCount > 1;

        BtnSave.IsEnabled = hasDoc && session!.IsModified;
        BtnSaveAs.IsEnabled = hasDoc;
        BtnPrint.IsEnabled = hasDoc;
        BtnOptimize.IsEnabled = hasDoc;
        BtnEncrypt.IsEnabled = hasDoc;
        BtnSignPades.IsEnabled = hasDoc;
        BtnAddTextField.IsEnabled = hasDoc;
        BtnUndo.IsEnabled = hasDoc && session!.History.CanUndo;
        BtnRedo.IsEnabled = hasDoc && session!.History.CanRedo;
        ToolSelect.IsEnabled = hasDoc;
        ToolHighlight.IsEnabled = hasDoc;
        ToolSquare.IsEnabled = hasDoc;
        ToolNote.IsEnabled = hasDoc;
        ToolInk.IsEnabled = hasDoc;
        ToolAddText.IsEnabled = hasDoc;
        ToolRedact.IsEnabled = hasDoc;
        BtnInsertImage.IsEnabled = hasDoc;
        BtnSanitize.IsEnabled = hasDoc;
        BtnOcrPage.IsEnabled = hasDoc;
        BtnOcrAll.IsEnabled = hasDoc;
        ChkDarkRead.IsEnabled = hasDoc;
        if (hasDoc) ChkDarkRead.IsChecked = session!.DarkReadingMode;
        BtnUndo.ToolTip = session?.History.NextUndoDescription is string u ? $"Deshacer: {u} (Ctrl+Z)" : "Deshacer (Ctrl+Z)";
        BtnRedo.ToolTip = session?.History.NextRedoDescription is string r ? $"Rehacer: {r} (Ctrl+Y)" : "Rehacer (Ctrl+Y)";

        BtnPrev.IsEnabled = hasDoc && session!.CurrentPageIndex > 0;
        BtnNext.IsEnabled = hasDoc && session!.CurrentPageIndex < session.PageCount - 1;
        BtnZoomIn.IsEnabled = hasDoc;
        BtnZoomOut.IsEnabled = hasDoc;
        BtnFitWidth.IsEnabled = hasDoc;
        BtnResetZoom.IsEnabled = hasDoc;

        BtnRotatePage.IsEnabled = hasDoc;
        BtnMoveUp.IsEnabled = multiPage && session!.CurrentPageIndex > 0;
        BtnMoveDown.IsEnabled = multiPage && session!.CurrentPageIndex < session.PageCount - 1;
        BtnMergePdf.IsEnabled = hasDoc;
        BtnExtractPage.IsEnabled = hasDoc;
        BtnDeletePage.IsEnabled = multiPage;

        TxtSearch.IsEnabled = hasDoc;
        BtnSearch.IsEnabled = hasDoc;
        BtnSearchPrev.IsEnabled = hasDoc && session!.TotalSearchMatches > 0;
        BtnSearchNext.IsEnabled = hasDoc && session!.TotalSearchMatches > 0;

        if (hasDoc)
        {
            Title = $"{session!.DisplayTabTitle} — PDFVisor";
            TxtPageIndicator.Text = $"{session.CurrentPageNumber} / {session.PageCount}";
            TxtZoom.Text = session.ZoomPercentText;
            TxtSearchStatus.Text = session.SearchStatusText;
            StatusFile.Text = session.FilePath;
            StatusPageSize.Text = session.CurrentPageSizeText;
            if (PageList.SelectedIndex != session.CurrentPageIndex && session.CurrentPageIndex < PageList.Items.Count)
            {
                PageList.SelectedIndex = session.CurrentPageIndex;
                PageList.ScrollIntoView(PageList.SelectedItem);
            }
        }
        else
        {
            Title = "PDFVisor — Visor y Editor PDF Profesional";
            TxtPageIndicator.Text = "0 / 0";
            TxtZoom.Text = "100%";
            TxtSearchStatus.Text = string.Empty;
            StatusFile.Text = "Sin documento abierto — arrastra archivos .pdf o pulsa Abrir (Ctrl+O)";
            StatusPageSize.Text = string.Empty;
        }
    }

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Abrir documentos PDF",
            Filter = "Documentos PDF (*.pdf)|*.pdf|Todos los archivos (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) == true)
        {
            foreach (var file in dlg.FileNames)
            {
                OpenFile(file);
            }
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        try
        {
            _boundSession.Save();
            StatusFile.Text = $"Guardado: {_boundSession.FilePath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al guardar:\n{ex.Message}", "Error al guardar",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnSaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Guardar PDF como...",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = _boundSession.FileName
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _boundSession.SaveAs(dlg.FileName);
                StatusFile.Text = $"Copia guardada en: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al guardar copia:\n{ex.Message}", "Error al guardar",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e) => _boundSession?.Undo();
    private void BtnRedo_Click(object sender, RoutedEventArgs e) => _boundSession?.Redo();

    private void BtnPrint_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        try
        {
            if (PdfPrintService.Print(_boundSession))
            {
                StatusFile.Text = $"Enviado a impresora: {_boundSession.FileName}";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al imprimir:\n{ex.Message}", "Error de impresión",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ChkDarkRead_Changed(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null)
        {
            _boundSession.DarkReadingMode = ChkDarkRead.IsChecked == true;
        }
    }

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        if (ToolHighlight.IsChecked == true) _boundSession.ActiveTool = AnnotationTool.Highlight;
        else if (ToolSquare.IsChecked == true) _boundSession.ActiveTool = AnnotationTool.Square;
        else if (ToolNote.IsChecked == true) _boundSession.ActiveTool = AnnotationTool.Note;
        else if (ToolInk.IsChecked == true) _boundSession.ActiveTool = AnnotationTool.Ink;
        else if (ToolAddText.IsChecked == true) _boundSession.ActiveTool = AnnotationTool.AddText;
        else if (ToolRedact.IsChecked == true) _boundSession.ActiveTool = AnnotationTool.Redact;
        else _boundSession.ActiveTool = AnnotationTool.None;
    }

    private void BtnSanitize_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Exportar PDF saneado (sin metadatos ni historial)",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_boundSession.FileName)}_saneado.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _boundSession.SanitizeAndExport(dlg.FileName);
                StatusFile.Text = $"PDF saneado guardado en: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al sanear PDF:\n{ex.Message}", "Error QPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void BtnOcrPage_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || _boundSession.PageCount == 0) return;
        int words = await RunOcrOnPageAsync(_boundSession, _boundSession.CurrentPageIndex);
        if (words >= 0)
        {
            StatusFile.Text = $"OCR completado: {words} palabras inyectadas como capa buscable invisible.";
        }
    }

    private async Task<int> RunOcrOnPageAsync(DocumentSession session, int pageIndex)
    {
        var pageInfo = session.Pages[pageIndex];

        string installedWorker = System.IO.Path.Combine(AppContext.BaseDirectory, "workers", "PDFVisor.OcrWorker.exe");
        string devWorker = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "workers", "PDFVisor.OcrWorker", "bin", "Release", "net10.0-windows10.0.19041.0", "PDFVisor.OcrWorker.exe"));
        string workerExe = System.IO.File.Exists(installedWorker) ? installedWorker : devWorker;
        if (!System.IO.File.Exists(workerExe))
        {
            MessageBox.Show(this, $"No se encontró PDFVisor.OcrWorker.exe en:\n{workerExe}", "Worker OCR no encontrado",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return -1;
        }

        string tempPng = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pdfvisor_ocr_{Guid.NewGuid():N}.png");
        try
        {
            int wPx = (int)Math.Round(pageInfo.WidthPt * (200.0 / 72.0));
            int hPx = (int)Math.Round(pageInfo.HeightPt * (200.0 / 72.0));
            byte[] bgra = await Task.Run(() => session.Document.RenderPageBgra(pageIndex, wPx, hPx));

            var bmp = BitmapSource.Create(wPx, hPx, 200, 200, PixelFormats.Bgra32, null, bgra, wPx * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(tempPng)) encoder.Save(fs);

            var psi = new System.Diagnostics.ProcessStartInfo(workerExe, $"--backend auto \"{tempPng}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string json = await proc.StandardOutput.ReadToEndAsync();
            string err = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                MessageBox.Show(this, $"Error en OCR Worker:\n{err}", "Error OCR", MessageBoxButton.OK, MessageBoxImage.Error);
                return -1;
            }

            using var docJson = System.Text.Json.JsonDocument.Parse(json);
            var words = docJson.RootElement.GetProperty("Words");
            double scaleX = pageInfo.WidthPt / wPx;
            double scaleY = pageInfo.HeightPt / hPx;
            int injected = 0;
            foreach (var w in words.EnumerateArray())
            {
                string txt = w.GetProperty("Text").GetString() ?? "";
                if (string.IsNullOrWhiteSpace(txt)) continue;
                double xPt = w.GetProperty("X").GetDouble() * scaleX;
                double yTopPx = w.GetProperty("Y").GetDouble();
                double hWordPx = w.GetProperty("Height").GetDouble();
                double yPt = pageInfo.HeightPt - (yTopPx + hWordPx) * scaleY;
                float fontSize = (float)Math.Clamp(hWordPx * scaleY, 6.0, 48.0);
                session.AddInvisibleOcrText(txt, xPt, yPt, fontSize);
                injected++;
            }
            return injected;
        }
        finally
        {
            if (System.IO.File.Exists(tempPng)) try { System.IO.File.Delete(tempPng); } catch { }
        }
    }

    private void BtnInsertImage_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Insertar imagen en el PDF",
            Filter = "Imágenes (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var bmp = new BitmapImage(new Uri(dlg.FileName));
            var formatted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = formatted.PixelWidth;
            int h = formatted.PixelHeight;
            byte[] pixels = new byte[w * h * 4];
            formatted.CopyPixels(pixels, w * 4, 0);

            double targetW = Math.Min(220.0, w * 72.0 / 96.0);
            double targetH = targetW * ((double)h / Math.Max(1, w));
            var rect = new PvRect { Left = 72, Bottom = 500, Right = 72 + targetW, Top = 500 + targetH };
            _boundSession.AddImageBgra(pixels, w, h, rect);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo insertar la imagen:\n{ex.Message}", "Error de imagen",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PageObjectList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_boundSession is null || PageObjectList.SelectedItem is not PdfPageObjectInfo obj) return;
        if (obj.Type != PdfPageObjectType.Text) return;

        string? edited = PromptInput("Editar texto del PDF", "Nuevo texto:");
        if (string.IsNullOrWhiteSpace(edited)) return;
        try
        {
            _boundSession.EditTextObject(obj.Index, obj.Text, edited);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo editar el texto:\n{ex.Message}", "Error al editar texto",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnOptimize_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Exportar PDF optimizado y linealizado (QPDF)",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_boundSession.FileName)}_optimizado.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _boundSession.OptimizeAndExport(dlg.FileName, linearize: true, recompress: true, objectStreams: true);
                StatusFile.Text = $"PDF optimizado y linealizado en: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al optimizar con QPDF:\n{ex.Message}", "Error QPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnEncrypt_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        string? password = PromptInput("Cifrar PDF con AES-256 (QPDF)", "Contraseña de apertura (usuario):");
        if (string.IsNullOrWhiteSpace(password)) return;

        var dlg = new SaveFileDialog
        {
            Title = "Guardar PDF cifrado AES-256",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_boundSession.FileName)}_cifrado.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _boundSession.EncryptAes256AndExport(dlg.FileName, userPassword: password, ownerPassword: password);
                StatusFile.Text = $"PDF cifrado AES-256 guardado en: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al cifrar con QPDF:\n{ex.Message}", "Error AES-256",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnSignPades_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var certs = PadesSignatureService.ListWindowsSigningCertificates();
        if (certs.Count == 0)
        {
            MessageBox.Show(this,
                "No se encontraron certificados con clave privada en el almacén personal de Windows (CurrentUser\\My).",
                "Sin certificados disponibles",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var (selectedCert, tsaUrl) = PromptCertificateAndTsa(certs);
        if (selectedCert is null) return;

        var dlg = new SaveFileDialog
        {
            Title = "Guardar PDF firmado (PAdES)",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_boundSession.FileName)}_firmado.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _boundSession.SignPadesAndExport(dlg.FileName, selectedCert, tsaUrl: string.IsNullOrWhiteSpace(tsaUrl) ? null : tsaUrl.Trim());
                StatusFile.Text = $"PDF firmado (PAdES) en: {dlg.FileName}";
                OpenFile(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al firmar PDF:\n{ex.Message}", "Error de firma PAdES",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private (System.Security.Cryptography.X509Certificates.X509Certificate2? Cert, string? TsaUrl) PromptCertificateAndTsa(
        IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2> certs)
    {
        var win = new Window
        {
            Title = "Seleccionar certificado de firma (Windows CNG)",
            Width = 480,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this
        };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var bottom = new StackPanel { Orientation = Orientation.Vertical };
        DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(new TextBlock { Text = "URL de sellado de tiempo RFC 3161 (TSA, opcional para PAdES-B-T):", Margin = new Thickness(0, 8, 0, 4) });
        var tbTsa = new TextBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 10) };
        bottom.Children.Add(tbTsa);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Firmar", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };
        ok.Click += (_, _) => { win.DialogResult = true; win.Close(); };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        bottom.Children.Add(btns);
        panel.Children.Add(bottom);

        var list = new ListBox { SelectedIndex = 0 };
        foreach (var c in certs)
        {
            string name = c.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
            list.Items.Add($"{name} — Emisor: {c.Issuer} (expira {c.NotAfter:yyyy-MM-dd})");
        }
        panel.Children.Add(list);
        win.Content = panel;

        if (win.ShowDialog() == true && list.SelectedIndex >= 0)
            return (certs[list.SelectedIndex], tbTsa.Text);
        return (null, null);
    }

    private async void BtnOcrAll_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || _boundSession.PageCount == 0) return;
        var session = _boundSession;
        int totalPages = session.PageCount;
        int totalWords = 0;

        for (int p = 0; p < totalPages; p++)
        {
            StatusFile.Text = $"OCR por lotes: procesando página {p + 1} / {totalPages}...";
            session.CurrentPageIndex = p;
            int words = await RunOcrOnPageAsync(session, p);
            if (words < 0) return;
            totalWords += words;
        }
        StatusFile.Text = $"OCR por lotes completado: {totalWords} palabras inyectadas en {totalPages} páginas.";
    }

    private void BtnAddTextField_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        string? name = PromptInput("Nuevo campo de formulario", "Nombre del campo de texto:");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var rect = new PvRect { Left = 72, Bottom = 660, Right = 320, Top = 686 };
            _boundSession.AddTextFormField(name.Trim(), rect, "");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al añadir campo:\n{ex.Message}", "Error de formulario",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FormFieldList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_boundSession is null || FormFieldList.SelectedItem is not PdfFormField field) return;
        string? newVal = PromptInput($"Editar campo \"{field.Name}\"", "Nuevo valor:");
        if (newVal is null) return;
        try
        {
            _boundSession.SetFormFieldValue(field.Name, newVal);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al actualizar campo:\n{ex.Message}", "Error de formulario",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? PromptInput(string title, string label)
    {
        var win = new Window
        {
            Title = title,
            Width = 380,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = this
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox { Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(6, 4, 6, 4) };
        panel.Children.Add(tb);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Aceptar", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Padding = new Thickness(14, 4, 14, 4) };
        ok.Click += (_, _) => { win.DialogResult = true; win.Close(); };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(btns);
        win.Content = panel;
        win.Loaded += (_, _) => tb.Focus();
        return win.ShowDialog() == true ? tb.Text : null;
    }

    private void DocumentTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DocumentTabs.SelectedItem is DocumentSession selected &&
            !ReferenceEquals(_workspace.ActiveSession, selected))
        {
            _workspace.ActiveSession = selected;
        }
    }

    private void BtnCloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DocumentSession session })
        {
            if (session.IsModified)
            {
                var answer = MessageBox.Show(
                    this,
                    $"El documento \"{session.FileName}\" tiene cambios sin guardar. ¿Guardar antes de cerrar?",
                    "Cambios sin guardar",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.Yes) session.Save();
            }
            _workspace.CloseSession(session);
        }
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null) _boundSession.CurrentPageIndex--;
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null) _boundSession.CurrentPageIndex++;
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null) _boundSession.Zoom *= 1.25;
    }

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null) _boundSession.Zoom /= 1.25;
    }

    private void BtnFitWidth_Click(object sender, RoutedEventArgs e) => PdfViewport.FitToWidth();

    private void BtnResetZoom_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null) _boundSession.Zoom = 1.0;
    }

    private void BtnRotatePage_Click(object sender, RoutedEventArgs e)
    {
        _boundSession?.RotateCurrentPagePermanent(1);
    }

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null && _boundSession.CurrentPageIndex > 0)
        {
            _boundSession.MoveCurrentPage(_boundSession.CurrentPageIndex - 1);
        }
    }

    private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null && _boundSession.CurrentPageIndex < _boundSession.PageCount - 1)
        {
            _boundSession.MoveCurrentPage(_boundSession.CurrentPageIndex + 1);
        }
    }

    private void BtnMergePdf_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Seleccionar PDF para unir al final del documento",
            Filter = "Documentos PDF (*.pdf)|*.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _boundSession.ImportPagesFromFile(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"No se pudieron importar páginas:\n{ex.Message}", "Error al unir PDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnExtractPage_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var dlg = new SaveFileDialog
        {
            Title = $"Extraer página {_boundSession.CurrentPageNumber} como PDF",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"pagina_{_boundSession.CurrentPageNumber}.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _boundSession.ExtractCurrentPage(dlg.FileName);
                StatusFile.Text = $"Página {_boundSession.CurrentPageNumber} extraída en: {dlg.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"No se pudo extraer la página:\n{ex.Message}", "Error al extraer",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnDeletePage_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || _boundSession.PageCount <= 1) return;
        _boundSession.DeleteCurrentPage();
    }

    private async void BtnSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        await _boundSession.SearchAsync(TxtSearch.Text);
    }

    private async void TxtSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _boundSession is not null)
        {
            if (string.Equals(_boundSession.SearchQuery, TxtSearch.Text, StringComparison.Ordinal) &&
                _boundSession.TotalSearchMatches > 0)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    _boundSession.PreviousSearchMatch();
                else
                    _boundSession.NextSearchMatch();
            }
            else
            {
                await _boundSession.SearchAsync(TxtSearch.Text);
            }
            e.Handled = true;
        }
    }

    private void BtnSearchPrev_Click(object sender, RoutedEventArgs e) => _boundSession?.PreviousSearchMatch();
    private void BtnSearchNext_Click(object sender, RoutedEventArgs e) => _boundSession?.NextSearchMatch();

    private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_boundSession is not null && PageList.SelectedIndex >= 0 && PageList.SelectedIndex < _boundSession.PageCount)
        {
            _boundSession.CurrentPageIndex = PageList.SelectedIndex;
        }
    }

    private void BookmarkList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_boundSession is not null &&
            BookmarkList.SelectedItem is BookmarkItemViewModel bm &&
            bm.PageIndex >= 0 && bm.PageIndex < _boundSession.PageCount)
        {
            _boundSession.CurrentPageIndex = bm.PageIndex;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files?.Any(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) == true)
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files is not null)
            {
                foreach (var file in files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
                {
                    OpenFile(file);
                }
            }
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.S) { BtnSaveAs_Click(sender, e); e.Handled = true; }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.O) { BtnOpen_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.S) { BtnSave_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.P) { BtnPrint_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.C && !TxtSearch.IsKeyboardFocused)
            {
                string selected = PdfViewport.GetSelectedText();
                if (!string.IsNullOrEmpty(selected))
                {
                    Clipboard.SetText(selected);
                    StatusFile.Text = $"Copiado al portapapeles ({selected.Length} caracteres)";
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Z && !TxtSearch.IsKeyboardFocused) { BtnUndo_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Y && !TxtSearch.IsKeyboardFocused) { BtnRedo_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.F) { TxtSearch.Focus(); TxtSearch.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.W && _boundSession is not null) { _workspace.CloseSession(_boundSession); e.Handled = true; }
            else if (e.Key is Key.Add or Key.OemPlus) { BtnZoomIn_Click(sender, e); e.Handled = true; }
            else if (e.Key is Key.Subtract or Key.OemMinus) { BtnZoomOut_Click(sender, e); e.Handled = true; }
            else if (e.Key is Key.D0 or Key.NumPad0) { BtnResetZoom_Click(sender, e); e.Handled = true; }
        }
        else if (e.Key == Key.PageDown && !TxtSearch.IsKeyboardFocused) { BtnNext_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.PageUp && !TxtSearch.IsKeyboardFocused) { BtnPrev_Click(sender, e); e.Handled = true; }
    }
}
