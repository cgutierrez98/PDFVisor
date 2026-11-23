using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PDFVisor.App.Dialogs;
using PDFVisor.Core;
using PDFVisor.Interop;

namespace PDFVisor.App;

public partial class MainWindow : Window
{
    private readonly WorkspaceViewModel _workspace = new();
    private DocumentSession? _boundSession;
    private CancellationTokenSource? _thumbCts;
    private bool _isUpdatingControls;
    private bool _isSidebarOpen = true;

    public MainWindow()
    {
        InitializeComponent();
        DocumentTabs.ItemsSource = _workspace.OpenSessions;
        _workspace.PropertyChanged += Workspace_PropertyChanged;
        PdfViewport.PromptNoteText = () => PromptInput("Añadir nota adhesiva", "Escribe el texto de la nota:");
        PdfViewport.PromptAddText = () => PromptInput("Insertar texto", "Escribe el texto que deseas añadir a la página:");

        RefreshRecentFilesUi();
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

    private void RefreshRecentFilesUi()
    {
        var recents = RecentFilesService.Load();
        RecentFilesList.ItemsSource = recents;
        RecentFilesSection.Visibility = recents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
            SyncToolToggleButtons(session.ActiveTool);
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
            FloatingSearchBar.Visibility = Visibility.Collapsed;
            RefreshRecentFilesUi();
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

        const int maxThumbWidth = 148;
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
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (PdfEngineException) { break; }
        }
    }

    private void OpenFile(string filePath)
    {
        try
        {
            _workspace.OpenDocument(filePath);
            RecentFilesService.Record(filePath);
            StatusMessage.Text = System.IO.Path.GetFileName(filePath);
        }
        catch (PdfEngineException pex) when (pex.Status == PvStatus.PasswordRequired)
        {
            string? pwd = PromptInput(
                "Documento protegido",
                $"Introduce la contraseña para abrir \"{System.IO.Path.GetFileName(filePath)}\":",
                isPassword: true);
            if (string.IsNullOrEmpty(pwd)) return;
            try
            {
                _workspace.OpenDocument(filePath, pwd);
                RecentFilesService.Record(filePath);
                StatusMessage.Text = System.IO.Path.GetFileName(filePath);
            }
            catch (Exception ex2)
            {
                MessageBox.Show(this, $"Contraseña incorrecta o error al abrir:\n{ex2.Message}", "Error al abrir PDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo abrir el documento:\n{ex.Message}", "Error al abrir PDF",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateUiState()
    {
        var session = _boundSession;
        bool hasDoc = session is not null && session.PageCount > 0;
        bool multiPage = hasDoc && session!.PageCount > 1;

        WorkspaceContainer.Visibility = hasDoc ? Visibility.Visible : Visibility.Collapsed;
        TabStripBorder.Visibility = _workspace.OpenSessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WelcomeContainer.Visibility = hasDoc ? Visibility.Collapsed : Visibility.Visible;

        BtnSave.IsEnabled = hasDoc && session!.IsModified;
        MenuSave.IsEnabled = BtnSave.IsEnabled;
        MenuSaveAs.IsEnabled = hasDoc;
        MenuOptimize.IsEnabled = hasDoc;
        MenuPrint.IsEnabled = hasDoc;
        BtnPrint.IsEnabled = hasDoc;
        MenuCloseTab.IsEnabled = hasDoc;

        BtnUndo.IsEnabled = hasDoc && session!.History.CanUndo;
        BtnRedo.IsEnabled = hasDoc && session!.History.CanRedo;
        MenuUndo.IsEnabled = BtnUndo.IsEnabled;
        MenuRedo.IsEnabled = BtnRedo.IsEnabled;
        BtnUndo.ToolTip = session?.History.NextUndoDescription is string u ? $"Deshacer: {u} (Ctrl+Z)" : "Deshacer (Ctrl+Z)";
        BtnRedo.ToolTip = session?.History.NextRedoDescription is string r ? $"Rehacer: {r} (Ctrl+Y)" : "Rehacer (Ctrl+Y)";

        MenuCopy.IsEnabled = hasDoc;
        MenuFind.IsEnabled = hasDoc;
        MenuInsertImage.IsEnabled = hasDoc;
        MenuAddTextField.IsEnabled = hasDoc;
        MenuZoomIn.IsEnabled = hasDoc;
        MenuZoomOut.IsEnabled = hasDoc;
        MenuResetZoom.IsEnabled = hasDoc;
        MenuFitWidth.IsEnabled = hasDoc;
        MenuDarkRead.IsEnabled = hasDoc;

        MenuRotatePage.IsEnabled = hasDoc;
        MenuMoveUp.IsEnabled = multiPage && session!.CurrentPageIndex > 0;
        MenuMoveDown.IsEnabled = multiPage && session!.CurrentPageIndex < session.PageCount - 1;
        MenuMergePdf.IsEnabled = hasDoc;
        MenuExtractPage.IsEnabled = hasDoc;
        MenuDeletePage.IsEnabled = multiPage;

        MenuOcrPage.IsEnabled = hasDoc;
        MenuOcrAll.IsEnabled = hasDoc;
        MenuSignPades.IsEnabled = hasDoc;
        MenuEncrypt.IsEnabled = hasDoc;
        MenuSanitize.IsEnabled = hasDoc;

        BtnPrev.IsEnabled = hasDoc && session!.CurrentPageIndex > 0;
        BtnNext.IsEnabled = hasDoc && session!.CurrentPageIndex < session.PageCount - 1;
        TxtPageJump.IsEnabled = hasDoc;
        BtnZoomIn.IsEnabled = hasDoc;
        BtnZoomOut.IsEnabled = hasDoc;
        CmbZoom.IsEnabled = hasDoc;
        BtnRotatePage.IsEnabled = hasDoc;

        ToolSelect.IsEnabled = hasDoc;
        ToolHighlight.IsEnabled = hasDoc;
        ToolSquare.IsEnabled = hasDoc;
        ToolNote.IsEnabled = hasDoc;
        ToolInk.IsEnabled = hasDoc;
        ToolAddText.IsEnabled = hasDoc;
        ToolRedact.IsEnabled = hasDoc;
        BtnDarkRead.IsEnabled = hasDoc;
        BtnSearchToggle.IsEnabled = hasDoc;

        _isUpdatingControls = true;
        try
        {
            if (hasDoc)
            {
                Title = $"{session!.DisplayTabTitle} — PDFVisor";
                if (!TxtPageJump.IsKeyboardFocused)
                    TxtPageJump.Text = session.CurrentPageNumber.ToString();
                TxtPageTotal.Text = $"/ {session.PageCount}";
                CmbZoom.Text = session.ZoomPercentText;
                TxtSearchStatus.Text = session.SearchStatusText;
                StatusPageSize.Text = session.CurrentPageSizeText;
                StatusZoom.Text = session.ZoomPercentText;
                BtnDarkRead.IsChecked = session.DarkReadingMode;
                MenuDarkRead.IsChecked = session.DarkReadingMode;

                if (PageList.SelectedIndex != session.CurrentPageIndex && session.CurrentPageIndex < PageList.Items.Count)
                {
                    PageList.SelectedIndex = session.CurrentPageIndex;
                    PageList.ScrollIntoView(PageList.SelectedItem);
                }
            }
            else
            {
                Title = "PDFVisor";
                TxtPageJump.Text = string.Empty;
                TxtPageTotal.Text = "/ 0";
                CmbZoom.SelectedIndex = 2; // 100%
                StatusMessage.Text = "Listo";
                StatusPageSize.Text = string.Empty;
                StatusZoom.Text = string.Empty;
                BtnDarkRead.IsChecked = false;
                MenuDarkRead.IsChecked = false;
            }
        }
        finally
        {
            _isUpdatingControls = false;
        }
    }

    // --- Activity Rail & Collapsible Side Panel ---
    private void RailButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        ToggleButton[] rail = [RailThumbnails, RailBookmarks, RailForms, RailSignatures, RailObjects];
        bool alreadyActive = clicked.IsChecked == false;
        if (alreadyActive)
        {
            SetSidebarOpen(false);
            return;
        }

        foreach (var btn in rail)
            btn.IsChecked = ReferenceEquals(btn, clicked);

        PageList.Visibility = ReferenceEquals(clicked, RailThumbnails) ? Visibility.Visible : Visibility.Collapsed;
        BookmarkList.Visibility = ReferenceEquals(clicked, RailBookmarks) ? Visibility.Visible : Visibility.Collapsed;
        FormsPanel.Visibility = ReferenceEquals(clicked, RailForms) ? Visibility.Visible : Visibility.Collapsed;
        SignaturesPanel.Visibility = ReferenceEquals(clicked, RailSignatures) ? Visibility.Visible : Visibility.Collapsed;
        PageObjectList.Visibility = ReferenceEquals(clicked, RailObjects) ? Visibility.Visible : Visibility.Collapsed;

        TxtSidePanelTitle.Text = clicked switch
        {
            _ when ReferenceEquals(clicked, RailThumbnails) => "Miniaturas",
            _ when ReferenceEquals(clicked, RailBookmarks) => "Marcadores",
            _ when ReferenceEquals(clicked, RailForms) => "Formularios",
            _ when ReferenceEquals(clicked, RailSignatures) => "Firmas digitales",
            _ => "Objetos de página"
        };

        SetSidebarOpen(true);
    }

    private void BtnCollapseSidebar_Click(object sender, RoutedEventArgs e)
    {
        ToggleButton[] rail = [RailThumbnails, RailBookmarks, RailForms, RailSignatures, RailObjects];
        foreach (var btn in rail) btn.IsChecked = false;
        SetSidebarOpen(false);
    }

    private void SetSidebarOpen(bool open)
    {
        _isSidebarOpen = open;
        SidePanelBorder.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SideSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidePanelColumn.Width = open ? new GridLength(240) : new GridLength(0);
        SideSplitterColumn.Width = open ? new GridLength(4) : new GridLength(0);
    }

    // --- Segmented Tool Toggle Buttons ---
    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || sender is not ToggleButton clicked) return;

        AnnotationTool chosen = clicked switch
        {
            _ when ReferenceEquals(clicked, ToolHighlight) => AnnotationTool.Highlight,
            _ when ReferenceEquals(clicked, ToolSquare) => AnnotationTool.Square,
            _ when ReferenceEquals(clicked, ToolNote) => AnnotationTool.Note,
            _ when ReferenceEquals(clicked, ToolInk) => AnnotationTool.Ink,
            _ when ReferenceEquals(clicked, ToolAddText) => AnnotationTool.AddText,
            _ when ReferenceEquals(clicked, ToolRedact) => AnnotationTool.Redact,
            _ => AnnotationTool.None
        };

        _boundSession.ActiveTool = chosen;
        SyncToolToggleButtons(chosen);
    }

    private void SyncToolToggleButtons(AnnotationTool tool)
    {
        ToolSelect.IsChecked = tool == AnnotationTool.None;
        ToolHighlight.IsChecked = tool == AnnotationTool.Highlight;
        ToolSquare.IsChecked = tool == AnnotationTool.Square;
        ToolNote.IsChecked = tool == AnnotationTool.Note;
        ToolInk.IsChecked = tool == AnnotationTool.Ink;
        ToolAddText.IsChecked = tool == AnnotationTool.AddText;
        ToolRedact.IsChecked = tool == AnnotationTool.Redact;
    }

    // --- File & Tab Actions ---
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
            foreach (var file in dlg.FileNames) OpenFile(file);
        }
    }

    private void RecentFilesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentFilesList.SelectedItem is RecentFileItem item)
            OpenFile(item.FilePath);
    }

    private void BtnClearRecent_Click(object sender, RoutedEventArgs e)
    {
        RecentFilesService.Clear();
        RefreshRecentFilesUi();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        try
        {
            _boundSession.Save();
            StatusMessage.Text = $"Guardado: {_boundSession.FileName}";
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
                StatusMessage.Text = $"Copia guardada: {System.IO.Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al guardar copia:\n{ex.Message}", "Error al guardar",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnPrint_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        try
        {
            if (PdfPrintService.Print(_boundSession))
                StatusMessage.Text = $"Enviado a impresora: {_boundSession.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al imprimir:\n{ex.Message}", "Error de impresión",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MenuCloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null) TryCloseSession(_boundSession);
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

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
            TryCloseSession(session);
    }

    private void TryCloseSession(DocumentSession session)
    {
        if (session.IsModified)
        {
            var answer = MessageBox.Show(
                this,
                $"¿Deseas guardar los cambios en \"{session.FileName}\" antes de cerrar?",
                "Cambios sin guardar",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes) session.Save();
        }
        _workspace.CloseSession(session);
    }

    // --- Navigation & Zoom ---
    private void BtnPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null) _boundSession.CurrentPageIndex--;
    }

    private void BtnNext_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null) _boundSession.CurrentPageIndex++;
    }

    private void TxtPageJump_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitPageJump();
            e.Handled = true;
        }
    }

    private void TxtPageJump_LostFocus(object sender, RoutedEventArgs e) => CommitPageJump();

    private void CommitPageJump()
    {
        if (_boundSession is null) return;
        if (int.TryParse(TxtPageJump.Text.Trim(), out int pageNum))
        {
            _boundSession.CurrentPageIndex = Math.Clamp(pageNum - 1, 0, _boundSession.PageCount - 1);
        }
        else
        {
            TxtPageJump.Text = _boundSession.CurrentPageNumber.ToString();
        }
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

    private void CmbZoom_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingControls || _boundSession is null || CmbZoom.SelectedItem is not ComboBoxItem item) return;
        string? text = item.Content?.ToString();
        if (text == "Ajustar")
        {
            PdfViewport.FitToWidth();
        }
        else if (text is not null && text.EndsWith('%') && double.TryParse(text.TrimEnd('%'), out double pct))
        {
            _boundSession.Zoom = pct / 100.0;
        }
    }

    private void BtnDarkRead_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null)
            _boundSession.DarkReadingMode = BtnDarkRead.IsChecked == true;
    }

    private void MenuDarkRead_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null)
            _boundSession.DarkReadingMode = MenuDarkRead.IsChecked;
    }

    // --- Edit, History, Pages ---
    private void BtnUndo_Click(object sender, RoutedEventArgs e) => _boundSession?.Undo();
    private void BtnRedo_Click(object sender, RoutedEventArgs e) => _boundSession?.Redo();

    private void MenuCopy_Click(object sender, RoutedEventArgs e)
    {
        string selected = PdfViewport.GetSelectedText();
        if (!string.IsNullOrEmpty(selected))
        {
            Clipboard.SetText(selected);
            StatusMessage.Text = $"Copiado al portapapeles ({selected.Length} caracteres)";
        }
    }

    private void BtnRotatePage_Click(object sender, RoutedEventArgs e) => _boundSession?.RotateCurrentPagePermanent(1);

    private void BtnMoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null && _boundSession.CurrentPageIndex > 0)
            _boundSession.MoveCurrentPage(_boundSession.CurrentPageIndex - 1);
    }

    private void BtnMoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null && _boundSession.CurrentPageIndex < _boundSession.PageCount - 1)
            _boundSession.MoveCurrentPage(_boundSession.CurrentPageIndex + 1);
    }

    private void BtnMergePdf_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var dlg = new OpenFileDialog { Title = "Seleccionar PDF para unir", Filter = "Documentos PDF (*.pdf)|*.pdf" };
        if (dlg.ShowDialog(this) == true)
        {
            try { _boundSession.ImportPagesFromFile(dlg.FileName); }
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
            Title = $"Extraer página {_boundSession.CurrentPageNumber}",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"pagina_{_boundSession.CurrentPageNumber}.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _boundSession.ExtractCurrentPage(dlg.FileName);
                StatusMessage.Text = $"Página {_boundSession.CurrentPageNumber} extraída";
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
        if (_boundSession is not null && _boundSession.PageCount > 1)
            _boundSession.DeleteCurrentPage();
    }

    private void BtnInsertImage_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Insertar imagen en el documento",
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
            _boundSession.AddImageBgra(pixels, w, h, new PvRect { Left = 72, Bottom = 500, Right = 72 + targetW, Top = 500 + targetH });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo insertar la imagen:\n{ex.Message}", "Error de imagen",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnAddTextField_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        string? name = PromptInput("Nuevo campo de formulario", "Nombre del campo de texto:");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            _boundSession.AddTextFormField(name.Trim(), new PvRect { Left = 72, Bottom = 660, Right = 320, Top = 686 }, "");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al añadir campo:\n{ex.Message}", "Error de formulario",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Security, Optimization & OCR ---
    private void BtnOptimize_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Exportar PDF optimizado",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_boundSession.FileName)}_optimizado.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _boundSession.OptimizeAndExport(dlg.FileName, linearize: true, recompress: true, objectStreams: true);
                StatusMessage.Text = $"PDF optimizado guardado: {System.IO.Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al optimizar:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnEncrypt_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        string? password = PromptInput("Proteger con contraseña", "Introduce la contraseña para abrir este documento:", isPassword: true);
        if (string.IsNullOrWhiteSpace(password)) return;

        var dlg = new SaveFileDialog
        {
            Title = "Guardar PDF protegido",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_boundSession.FileName)}_protegido.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _boundSession.EncryptAes256AndExport(dlg.FileName, userPassword: password, ownerPassword: password);
                StatusMessage.Text = $"Documento protegido guardado: {System.IO.Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al proteger el documento:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void BtnSanitize_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Exportar PDF saneado",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_boundSession.FileName)}_saneado.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _boundSession.SanitizeAndExport(dlg.FileName);
                StatusMessage.Text = $"Documento saneado guardado: {System.IO.Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al sanear:\n{ex.Message}", "Error",
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
                "No se encontraron certificados con clave privada en el almacén personal de Windows.",
                "Sin certificados disponibles",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var picker = new CertificatePickerDialog(certs) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedCertificate is null) return;

        var dlg = new SaveFileDialog
        {
            Title = "Guardar PDF firmado",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(_boundSession.FileName)}_firmado.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                _boundSession.SignPadesAndExport(dlg.FileName, picker.SelectedCertificate, reason: picker.Reason, tsaUrl: picker.TsaUrl);
                StatusMessage.Text = $"Documento firmado: {System.IO.Path.GetFileName(dlg.FileName)}";
                OpenFile(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al firmar:\n{ex.Message}", "Error de firma",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void BtnOcrPage_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || _boundSession.PageCount == 0) return;
        int words = await RunOcrOnPageAsync(_boundSession, _boundSession.CurrentPageIndex);
        if (words >= 0) StatusMessage.Text = $"OCR completado ({words} palabras reconocidas)";
    }

    private async void BtnOcrAll_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || _boundSession.PageCount == 0) return;
        var session = _boundSession;
        int totalPages = session.PageCount;
        int totalWords = 0;

        for (int p = 0; p < totalPages; p++)
        {
            StatusMessage.Text = $"Reconociendo texto (OCR): página {p + 1} de {totalPages}...";
            session.CurrentPageIndex = p;
            int words = await RunOcrOnPageAsync(session, p);
            if (words < 0) return;
            totalWords += words;
        }
        StatusMessage.Text = $"OCR completado: {totalWords} palabras en {totalPages} páginas";
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
            MessageBox.Show(this, "No se encontró el módulo OCR instalado.", "OCR no disponible",
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
                MessageBox.Show(this, $"Error en OCR:\n{err}", "Error OCR", MessageBoxButton.OK, MessageBoxImage.Error);
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

    // --- Floating Search Overlay ---
    private void BtnToggleSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        FloatingSearchBar.Visibility = Visibility.Visible;
        TxtSearch.Focus();
        TxtSearch.SelectAll();
    }

    private async void BtnCloseSearch_Click(object sender, RoutedEventArgs e)
    {
        FloatingSearchBar.Visibility = Visibility.Collapsed;
        if (_boundSession is not null)
        {
            await _boundSession.SearchAsync(string.Empty);
        }
    }

    private async void TxtSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            BtnCloseSearch_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && _boundSession is not null)
        {
            if (string.Equals(_boundSession.SearchQuery, TxtSearch.Text, StringComparison.Ordinal) &&
                _boundSession.TotalSearchMatches > 0)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift) _boundSession.PreviousSearchMatch();
                else _boundSession.NextSearchMatch();
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

    // --- Side Panel Interactions ---
    private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUpdatingControls && _boundSession is not null &&
            PageList.SelectedIndex >= 0 && PageList.SelectedIndex < _boundSession.PageCount)
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

    private void FormFieldList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_boundSession is null || FormFieldList.SelectedItem is not PdfFormField field) return;
        string? newVal = PromptInput($"Editar campo \"{field.Name}\"", "Nuevo valor:", initialValue: field.Value);
        if (newVal is null) return;
        try { _boundSession.SetFormFieldValue(field.Name, newVal); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al actualizar campo:\n{ex.Message}", "Error de formulario",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PageObjectList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_boundSession is null || PageObjectList.SelectedItem is not PdfPageObjectInfo obj) return;
        if (obj.Type != PdfPageObjectType.Text) return;

        string? edited = PromptInput("Editar texto", "Nuevo texto:", initialValue: obj.Text);
        if (string.IsNullOrWhiteSpace(edited)) return;
        try { _boundSession.EditTextObject(obj.Index, obj.Text, edited); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo editar el texto:\n{ex.Message}", "Error al editar texto",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? PromptInput(string title, string prompt, string initialValue = "", bool isPassword = false)
    {
        var dlg = new InputDialog(title, prompt, initialValue, isPassword) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.ResultText : null;
    }

    // --- Drag & Drop + Keyboard Shortcuts ---
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
                    OpenFile(file);
            }
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool typingInBox = TxtSearch.IsKeyboardFocused || TxtPageJump.IsKeyboardFocused;

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.S) { BtnSaveAs_Click(sender, e); e.Handled = true; }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.O) { BtnOpen_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.S) { BtnSave_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.P) { BtnPrint_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.F) { BtnToggleSearch_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.W && _boundSession is not null) { TryCloseSession(_boundSession); e.Handled = true; }
            else if (e.Key == Key.C && !typingInBox) { MenuCopy_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Z && !typingInBox) { BtnUndo_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Y && !typingInBox) { BtnRedo_Click(sender, e); e.Handled = true; }
            else if (e.Key is Key.Add or Key.OemPlus) { BtnZoomIn_Click(sender, e); e.Handled = true; }
            else if (e.Key is Key.Subtract or Key.OemMinus) { BtnZoomOut_Click(sender, e); e.Handled = true; }
            else if (e.Key is Key.D0 or Key.NumPad0) { BtnResetZoom_Click(sender, e); e.Handled = true; }
        }
        else if (e.Key == Key.Escape && FloatingSearchBar.Visibility == Visibility.Visible)
        {
            BtnCloseSearch_Click(sender, e);
            e.Handled = true;
        }
        else if (!typingInBox)
        {
            if (e.Key == Key.PageDown) { BtnNext_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.PageUp) { BtnPrev_Click(sender, e); e.Handled = true; }
        }
    }
}
