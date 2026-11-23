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
    private readonly AppSettings _settings;
    private DocumentSession? _boundSession;
    private CancellationTokenSource? _thumbCts;
    private UpdateInfo? _pendingUpdate;
    private bool _isUpdatingControls;
    private bool _isSidebarOpen = true;
    private bool _isFullscreen;
    private WindowStyle _prevWindowStyle;
    private WindowState _prevWindowState;
    private Point _thumbDragStart;
    private int _thumbDragIndex = -1;
    private int _busyDepth;

    private void SetBusy(bool busy, string title = "Procesando...", string detail = "", double? progressPercent = null)
    {
        if (busy)
        {
            _busyDepth++;
            TxtBusyTitle.Text = title;
            TxtBusyDetail.Text = detail;
            TxtBusyDetail.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
            if (progressPercent.HasValue)
            {
                BusyProgressBar.IsIndeterminate = false;
                BusyProgressBar.Value = Math.Clamp(progressPercent.Value, 0, 100);
            }
            else
            {
                BusyProgressBar.IsIndeterminate = true;
            }
            BusyOverlay.Visibility = Visibility.Visible;
            Mouse.OverrideCursor = Cursors.Wait;
        }
        else
        {
            _busyDepth = Math.Max(0, _busyDepth - 1);
            if (_busyDepth == 0)
            {
                BusyOverlay.Visibility = Visibility.Collapsed;
                Mouse.OverrideCursor = null;
            }
        }
    }

    private void UpdateBusyProgress(string detail, double? progressPercent = null)
    {
        Dispatcher.Invoke(() =>
        {
            TxtBusyDetail.Text = detail;
            TxtBusyDetail.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
            if (progressPercent.HasValue)
            {
                BusyProgressBar.IsIndeterminate = false;
                BusyProgressBar.Value = Math.Clamp(progressPercent.Value, 0, 100);
            }
            else
            {
                BusyProgressBar.IsIndeterminate = true;
            }
        });
    }

    private async Task RunBusyAsync(string title, string detail, Func<Task> work)
    {
        SetBusy(true, title, detail);
        try
        {
            // Yield so WPF renders the loading overlay before heavy work starts
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await work();
        }
        finally
        {
            SetBusy(false);
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        ApplySavedSettings();

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
            _ = CheckUpdatesInBackgroundAsync(silent: true);
        };
        UpdateUiState();
    }

    private void ApplySavedSettings()
    {
        if (_settings.WindowWidth >= MinWidth) Width = _settings.WindowWidth;
        if (_settings.WindowHeight >= MinHeight) Height = _settings.WindowHeight;
        if (_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.WindowLeft.Value;
            Top = _settings.WindowTop.Value;
        }
        if (_settings.IsMaximized) WindowState = WindowState.Maximized;
        SetSidebarOpen(_settings.IsSidebarOpen);
    }

    private void SaveCurrentSettings()
    {
        _settings.IsMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
        }
        _settings.IsSidebarOpen = _isSidebarOpen;
        if (_boundSession is not null)
        {
            _settings.DarkReadingMode = _boundSession.DarkReadingMode;
            _settings.DefaultZoom = _boundSession.Zoom;
        }
        _settings.Save();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var dirtySessions = _workspace.OpenSessions.Where(s => s.IsModified).ToList();
        foreach (var session in dirtySessions)
        {
            var answer = MessageBox.Show(
                this,
                $"¿Deseas guardar los cambios en \"{session.FileName}\" antes de salir?",
                "Cambios sin guardar",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (answer == MessageBoxResult.Yes)
            {
                try
                {
                    session.Save();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"No se pudo guardar \"{session.FileName}\":\n{ex.Message}", "Error al guardar",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Cancel = true;
                    return;
                }
            }
        }
        SaveCurrentSettings();
        base.OnClosing(e);
    }

    public void ReceiveExternalFiles(IEnumerable<string> files)
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        foreach (var file in files)
        {
            if (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                OpenFile(file);
        }
    }

    private void CancelThumbnailGeneration()
    {
        var cts = _thumbCts;
        _thumbCts = null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _workspace.PropertyChanged -= Workspace_PropertyChanged;
        if (_boundSession is not null)
        {
            _boundSession.PropertyChanged -= ActiveSession_PropertyChanged;
            _boundSession.History.PropertyChanged -= ActiveSession_PropertyChanged;
            _boundSession = null;
        }
        CancelThumbnailGeneration();
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
            CancelThumbnailGeneration();
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
        CancelThumbnailGeneration();
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
        ToolbarBorder.Visibility = (hasDoc && !_isFullscreen) ? Visibility.Visible : Visibility.Collapsed;
        TabStripBorder.Visibility = (_workspace.OpenSessions.Count > 0 && !_isFullscreen) ? Visibility.Visible : Visibility.Collapsed;
        WelcomeContainer.Visibility = hasDoc ? Visibility.Collapsed : Visibility.Visible;

        BtnSave.IsEnabled = hasDoc && session!.IsModified;
        MenuSave.IsEnabled = BtnSave.IsEnabled;
        MenuSaveAs.IsEnabled = hasDoc;
        MenuExportImage.IsEnabled = hasDoc;
        MenuProperties.IsEnabled = hasDoc;
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
        MenuTwoPage.IsEnabled = hasDoc;
        MenuDarkRead.IsEnabled = hasDoc;

        MenuRotatePage.IsEnabled = hasDoc;
        MenuMoveUp.IsEnabled = multiPage && session!.CurrentPageIndex > 0;
        MenuMoveDown.IsEnabled = multiPage && session!.CurrentPageIndex < session.PageCount - 1;
        MenuMergePdf.IsEnabled = hasDoc;
        MenuSplitPdf.IsEnabled = multiPage;
        MenuExtractPage.IsEnabled = hasDoc;
        MenuDeletePage.IsEnabled = multiPage;

        MenuWatermark.IsEnabled = hasDoc;
        MenuComparePdf.IsEnabled = hasDoc;
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
                MenuTwoPage.IsChecked = session.TwoPageMode;

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

        bool showColorPicker = tool is AnnotationTool.Highlight or AnnotationTool.Square or AnnotationTool.Note or AnnotationTool.Ink or AnnotationTool.AddText;
        ToolOptionsBar.Visibility = showColorPicker ? Visibility.Visible : Visibility.Collapsed;
        CmbFontSize.Visibility = tool == AnnotationTool.AddText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || sender is not Button { Tag: string hex }) return;
        if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint rgba))
        {
            _boundSession.ActiveColorRgba = rgba;
            StatusMessage.Text = "Color de herramienta actualizado";
        }
    }

    private void CmbFontSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingControls || _boundSession is null || CmbFontSize.SelectedItem is not ComboBoxItem item) return;
        string? raw = item.Content?.ToString()?.Replace(" pt", "").Trim();
        if (float.TryParse(raw, out float pt))
        {
            _boundSession.ActiveFontSize = pt;
        }
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

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var session = _boundSession;
        try
        {
            await RunBusyAsync("Guardando documento...", session.FileName, () => Task.Run(() => session.Save()));
            StatusMessage.Text = $"Guardado: {session.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al guardar:\n{ex.Message}", "Error al guardar",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnSaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var session = _boundSession;
        var dlg = new SaveFileDialog
        {
            Title = "Guardar PDF como...",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = session.FileName
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                string dest = dlg.FileName;
                await RunBusyAsync("Guardando copia...", System.IO.Path.GetFileName(dest), () => Task.Run(() => session.SaveAs(dest)));
                StatusMessage.Text = $"Copia guardada: {System.IO.Path.GetFileName(dest)}";
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

    // --- Auto-Update Channel (GitHub Releases) ---
    private async void MenuCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        StatusMessage.Text = "Buscando actualizaciones...";
        StatusProgressBar.Visibility = Visibility.Visible;
        try
        {
            await CheckUpdatesInBackgroundAsync(silent: false);
        }
        finally
        {
            StatusProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private async Task CheckUpdatesInBackgroundAsync(bool silent)
    {
        try
        {
            if (silent) await Task.Delay(1800);
            var info = await UpdateService.CheckForUpdatesAsync();
            if (info is not null && info.IsUpdateAvailable)
            {
                _pendingUpdate = info;
                TxtUpdateBanner.Text = $"Nueva versión {info.TagName} disponible (actual: v{info.CurrentVersion})";
                BtnDownloadUpdate.Visibility = string.IsNullOrWhiteSpace(info.InstallerDownloadUrl)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                UpdateBanner.Visibility = Visibility.Visible;
                if (!silent) StatusMessage.Text = $"Actualización disponible: {info.TagName}";
            }
            else if (!silent)
            {
                StatusMessage.Text = "PDFVisor está actualizado";
                MessageBox.Show(
                    this,
                    $"Ya tienes instalada la última versión de PDFVisor (v{UpdateService.GetCurrentVersion()}).",
                    "Sin actualizaciones",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                StatusMessage.Text = "No se pudo comprobar actualizaciones";
                MessageBox.Show(
                    this,
                    $"No se pudo conectar con GitHub Releases:\n{ex.Message}",
                    "Error al buscar actualizaciones",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private async void BtnDownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        BtnDownloadUpdate.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p =>
            {
                int pct = (int)Math.Round(p * 100);
                TxtUpdateBanner.Text = $"Descargando {_pendingUpdate.TagName}... {pct}%";
                StatusMessage.Text = $"Descargando actualización ({pct}%)...";
            });

            string installerPath = await UpdateService.DownloadInstallerAsync(_pendingUpdate, progress);
            TxtUpdateBanner.Text = "Descarga completada. Iniciando instalador...";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installerPath)
            {
                Arguments = "/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true
            });
            Close();
        }
        catch (Exception ex)
        {
            BtnDownloadUpdate.IsEnabled = true;
            TxtUpdateBanner.Text = $"Nueva versión {_pendingUpdate.TagName} disponible";
            MessageBox.Show(this, $"No se pudo descargar el instalador:\n{ex.Message}", "Error de actualización",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnOpenReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null || string.IsNullOrWhiteSpace(_pendingUpdate.ReleaseUrl)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_pendingUpdate.ReleaseUrl)
        {
            UseShellExecute = true
        });
    }

    private void BtnDismissUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            $"PDFVisor v{UpdateService.GetCurrentVersion()}\nVisor y editor de PDF nativo para Windows (x64).\n\nLicencia MIT — GitHub: cgutierrez98/PDFVisor",
            "Acerca de PDFVisor",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void MenuReportIssue_Click(object sender, RoutedEventArgs e)
    {
        string ver = UpdateService.GetCurrentVersion().ToString();
        string os = Environment.OSVersion.VersionString;
        string body = Uri.EscapeDataString($"**Versión:** PDFVisor v{ver}\n**Sistema:** {os}\n\n### Descripción del problema\n");
        string url = $"https://github.com/{UpdateService.DefaultOwner}/{UpdateService.DefaultRepo}/issues/new?body={body}";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void MenuOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PDFVisor",
            "logs");
        System.IO.Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private async void MenuExportImage_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || _boundSession.PageCount == 0) return;
        var session = _boundSession;
        int pageIdx = session.CurrentPageIndex;
        int pageNum = session.CurrentPageNumber;
        var dlg = new SaveFileDialog
        {
            Title = $"Exportar página {pageNum} como imagen",
            Filter = "Imagen PNG (*.png)|*.png|Imagen JPEG (*.jpg)|*.jpg",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(session.FileName)}_p{pageNum}.png"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var pageInfo = session.Pages[pageIdx];
            string outPath = dlg.FileName;
            await RunBusyAsync("Exportando página como imagen...", $"Renderizando página {pageNum} a 200 DPI...", () => Task.Run(() =>
            {
                const double exportDpi = 200.0;
                int wPx = Math.Clamp((int)Math.Round(pageInfo.WidthPt * (exportDpi / 72.0)), 1, 8192);
                int hPx = Math.Clamp((int)Math.Round(pageInfo.HeightPt * (exportDpi / 72.0)), 1, 8192);
                byte[] bgra = session.Document.RenderPageBgra(pageIdx, wPx, hPx);

                var bmp = BitmapSource.Create(wPx, hPx, exportDpi, exportDpi, PixelFormats.Bgra32, null, bgra, wPx * 4);
                bmp.Freeze();
                BitmapEncoder encoder = outPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    ? new JpegBitmapEncoder { QualityLevel = 92 }
                    : new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = System.IO.File.Create(outPath);
                encoder.Save(fs);
            }));
            StatusMessage.Text = $"Página exportada: {System.IO.Path.GetFileName(outPath)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al exportar imagen:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MenuCreateFromImages_Click(object sender, RoutedEventArgs e)
    {
        var openDlg = new OpenFileDialog
        {
            Title = "Seleccionar imágenes para crear un PDF",
            Filter = "Imágenes (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            Multiselect = true
        };
        if (openDlg.ShowDialog(this) != true || openDlg.FileNames.Length == 0) return;

        var saveDlg = new SaveFileDialog
        {
            Title = "Guardar nuevo PDF",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = "documento_imagenes.pdf"
        };
        if (saveDlg.ShowDialog(this) != true) return;

        try
        {
            string[] files = openDlg.FileNames;
            string destPath = saveDlg.FileName;
            await RunBusyAsync("Creando PDF desde imágenes...", $"Procesando {files.Length} imagen(es)...", () => Task.Run(() =>
            {
                PdfDocument? created = null;
                try
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        UpdateBusyProgress($"Imagen {i + 1} de {files.Length}: {System.IO.Path.GetFileName(files[i])}", (i * 100.0) / files.Length);
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

                    UpdateBusyProgress("Guardando PDF generado...", 98);
                    created?.SaveAs(destPath, incremental: false);
                }
                finally
                {
                    created?.Dispose();
                }
            }));

            OpenFile(destPath);
            StatusMessage.Text = $"PDF creado desde {files.Length} imagen(es)";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al crear PDF desde imágenes:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

    private void MenuTwoPage_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null)
            _boundSession.TwoPageMode = MenuTwoPage.IsChecked;
    }

    private void MenuFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        MenuFullscreen.IsChecked = _isFullscreen;

        if (_isFullscreen)
        {
            _prevWindowStyle = WindowStyle;
            _prevWindowState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            MainMenuBar.Visibility = Visibility.Collapsed;
            MainStatusBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            WindowStyle = _prevWindowStyle;
            WindowState = _prevWindowState;
            MainMenuBar.Visibility = Visibility.Visible;
            MainStatusBar.Visibility = Visibility.Visible;
        }
        UpdateUiState();
    }

    private async void MenuProperties_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var session = _boundSession;
        try
        {
            PdfMetadata meta = default!;
            await RunBusyAsync("Leyendo propiedades...", session.FileName, async () =>
            {
                meta = await Task.Run(() => session.GetMetadata());
            });
            var dlg = new DocumentPropertiesDialog(session.FilePath, session.PageCount, meta) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                await RunBusyAsync("Guardando metadatos...", session.FileName, () =>
                    session.UpdateMetadataAsync(dlg.MetaTitle, dlg.MetaAuthor, dlg.MetaSubject, dlg.MetaKeywords));
                StatusMessage.Text = "Metadatos del documento actualizados";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al leer o guardar metadatos:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MenuSplitPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || _boundSession.PageCount <= 1) return;
        var session = _boundSession;
        var dlg = new SplitPdfDialog(session.PageCount) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedGroups is null) return;

        var folderDlg = new OpenFolderDialog { Title = "Seleccionar carpeta de destino para los PDFs divididos" };
        if (folderDlg.ShowDialog(this) != true) return;

        try
        {
            string baseName = System.IO.Path.GetFileNameWithoutExtension(session.FileName);
            string outDir = folderDlg.FolderName;
            var groups = dlg.SelectedGroups;
            IReadOnlyList<string> files = Array.Empty<string>();
            await RunBusyAsync("Dividiendo documento PDF...", $"Generando {groups.Count} archivo(s)...", () => Task.Run(() =>
            {
                files = session.SplitIntoFiles(groups, outDir, baseName);
            }));
            StatusMessage.Text = $"Documento dividido en {files.Count} archivos";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(outDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al dividir el documento:\n{ex.Message}", "Error al dividir",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MenuWatermark_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var session = _boundSession;
        var dlg = new WatermarkDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var progress = new Progress<(int Current, int Total)>(p =>
                UpdateBusyProgress($"Página {p.Current} de {p.Total}...", (p.Current * 100.0) / Math.Max(1, p.Total)));

            await RunBusyAsync("Aplicando marca de agua y numeración...", $"Procesando {session.PageCount} página(s)...", async () =>
            {
                if (dlg.ApplyWatermark)
                    await session.ApplyWatermarkToAllPagesAsync(dlg.WatermarkText, dlg.WatermarkOpacity, progress: progress);
                if (dlg.ApplyPageNumbers)
                    await session.ApplyPageNumbersToAllPagesAsync(dlg.PageNumberFormat, progress: progress);
            });
            StatusMessage.Text = "Marca de agua / numeración aplicada";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al aplicar marca de agua:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MenuComparePdf_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var session = _boundSession;
        var dlg = new OpenFileDialog
        {
            Title = "Seleccionar segundo PDF para comparar",
            Filter = "Documentos PDF (*.pdf)|*.pdf"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            PdfDocument? rightDoc = null;
            IReadOnlyList<PageDiffItem>? diffs = null;
            string rightPath = dlg.FileName;
            await RunBusyAsync("Comparando documentos PDF...", $"Analizando texto página a página con {System.IO.Path.GetFileName(rightPath)}...", () => Task.Run(() =>
            {
                rightDoc = PdfDocument.Open(rightPath);
                diffs = PdfDiffService.CompareDocuments(session.Document, rightDoc);
            }));

            var cmp = new ComparePdfDialog(session.Document, rightDoc!, ownsRightDoc: true, precomputedDiffs: diffs) { Owner = this };
            cmp.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo comparar el documento:\n{ex.Message}", "Error de comparación",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Thumbnail Drag & Drop Reordering ---
    private void PageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _thumbDragStart = e.GetPosition(PageList);
        _thumbDragIndex = GetThumbnailIndexAt(e.OriginalSource as DependencyObject);
    }

    private void PageList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _thumbDragIndex < 0 || _boundSession is null || _boundSession.PageCount <= 1)
            return;
        Point current = e.GetPosition(PageList);
        if (Math.Abs(current.X - _thumbDragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _thumbDragStart.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            int idx = _thumbDragIndex;
            _thumbDragIndex = -1;
            DragDrop.DoDragDrop(PageList, new DataObject("PDFVisor.PageIndex", idx), DragDropEffects.Move);
        }
    }

    private void PageList_Drop(object sender, DragEventArgs e)
    {
        if (_boundSession is null || !e.Data.GetDataPresent("PDFVisor.PageIndex")) return;
        int fromIdx = (int)e.Data.GetData("PDFVisor.PageIndex");
        int toIdx = GetThumbnailIndexAt(e.OriginalSource as DependencyObject);
        if (toIdx >= 0 && fromIdx != toIdx)
        {
            _boundSession.MovePage(fromIdx, toIdx);
            StatusMessage.Text = $"Página {fromIdx + 1} movida a la posición {toIdx + 1}";
        }
        e.Handled = true;
    }

    private int GetThumbnailIndexAt(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        if (source is ListBoxItem item && item.DataContext is PageItemViewModel vm)
            return vm.PageIndex;
        return -1;
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

    private async void BtnMergePdf_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var session = _boundSession;
        var dlg = new OpenFileDialog { Title = "Seleccionar PDF para unir", Filter = "Documentos PDF (*.pdf)|*.pdf" };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                string srcPath = dlg.FileName;
                await RunBusyAsync("Uniendo documentos PDF...", System.IO.Path.GetFileName(srcPath), () =>
                {
                    session.ImportPagesFromFile(srcPath);
                    return Task.CompletedTask;
                });
                StatusMessage.Text = $"Páginas importadas desde {System.IO.Path.GetFileName(srcPath)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"No se pudieron importar páginas:\n{ex.Message}", "Error al unir PDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void BtnExtractPage_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var session = _boundSession;
        int pageNum = session.CurrentPageNumber;
        var dlg = new SaveFileDialog
        {
            Title = $"Extraer página {pageNum}",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"pagina_{pageNum}.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                string outPath = dlg.FileName;
                await RunBusyAsync("Extrayendo página...", $"Guardando página {pageNum}...", () => Task.Run(() => session.ExtractCurrentPage(outPath)));
                StatusMessage.Text = $"Página {pageNum} extraída";
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
    private async void BtnOptimize_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var session = _boundSession;
        var dlg = new SaveFileDialog
        {
            Title = "Exportar PDF optimizado",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(session.FileName)}_optimizado.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                string dest = dlg.FileName;
                await RunBusyAsync("Optimizando documento PDF...", "Recomprimiendo flujos y linealizando para carga rápida...", () =>
                    Task.Run(() => session.OptimizeAndExport(dest, linearize: true, recompress: true, objectStreams: true)));
                StatusMessage.Text = $"PDF optimizado guardado: {System.IO.Path.GetFileName(dest)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al optimizar:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void BtnEncrypt_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var session = _boundSession;
        string? password = PromptInput("Proteger con contraseña", "Introduce la contraseña para abrir este documento:", isPassword: true);
        if (string.IsNullOrWhiteSpace(password)) return;

        var dlg = new SaveFileDialog
        {
            Title = "Guardar PDF protegido",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(session.FileName)}_protegido.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                string dest = dlg.FileName;
                await RunBusyAsync("Cifrando documento (AES-256)...", System.IO.Path.GetFileName(dest), () =>
                    Task.Run(() => session.EncryptAes256AndExport(dest, userPassword: password, ownerPassword: password)));
                StatusMessage.Text = $"Documento protegido guardado: {System.IO.Path.GetFileName(dest)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al proteger el documento:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void BtnSanitize_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var session = _boundSession;
        var dlg = new SaveFileDialog
        {
            Title = "Exportar PDF saneado",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(session.FileName)}_saneado.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                string dest = dlg.FileName;
                await RunBusyAsync("Saneando metadatos e historial...", System.IO.Path.GetFileName(dest), () =>
                    Task.Run(() => session.SanitizeAndExport(dest)));
                StatusMessage.Text = $"Documento saneado guardado: {System.IO.Path.GetFileName(dest)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al sanear:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void BtnSignPades_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var session = _boundSession;
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
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(session.FileName)}_firmado.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                string dest = dlg.FileName;
                var cert = picker.SelectedCertificate;
                string? reason = picker.Reason;
                string? tsaUrl = picker.TsaUrl;
                string detail = string.IsNullOrWhiteSpace(tsaUrl)
                    ? "Aplicando firma digital PAdES..."
                    : "Aplicando firma digital PAdES y sello de tiempo TSA...";
                await RunBusyAsync("Firmando documento digitalmente...", detail, () =>
                    Task.Run(() => session.SignPadesAndExport(dest, cert, reason: reason, tsaUrl: tsaUrl)));
                StatusMessage.Text = $"Documento firmado: {System.IO.Path.GetFileName(dest)}";
                OpenFile(dest);
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
        var session = _boundSession;
        int pageIdx = session.CurrentPageIndex;
        int words = -1;
        await RunBusyAsync("Reconociendo texto (OCR)...", $"Analizando página {pageIdx + 1}...", async () =>
        {
            words = await RunOcrOnPageAsync(session, pageIdx);
        });
        if (words >= 0) StatusMessage.Text = $"OCR completado ({words} palabras reconocidas)";
    }

    private async void BtnOcrAll_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || _boundSession.PageCount == 0) return;
        var session = _boundSession;
        int totalPages = session.PageCount;
        int totalWords = 0;

        await RunBusyAsync("Reconociendo texto (OCR)...", $"Página 1 de {totalPages}...", async () =>
        {
            for (int p = 0; p < totalPages; p++)
            {
                UpdateBusyProgress($"Página {p + 1} de {totalPages}...", (p * 100.0) / Math.Max(1, totalPages));
                StatusMessage.Text = $"Reconociendo texto (OCR): página {p + 1} de {totalPages}...";
                session.CurrentPageIndex = p;
                int words = await RunOcrOnPageAsync(session, p);
                if (words < 0) return;
                totalWords += words;
            }
            UpdateBusyProgress("Finalizando...", 100);
        });
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
                StatusProgressBar.Visibility = Visibility.Visible;
                StatusMessage.Text = "Buscando en el documento...";
                try
                {
                    await _boundSession.SearchAsync(TxtSearch.Text);
                }
                finally
                {
                    StatusProgressBar.Visibility = Visibility.Collapsed;
                    StatusMessage.Text = string.IsNullOrWhiteSpace(_boundSession.SearchStatusText) ? "Listo" : $"Búsqueda: {_boundSession.SearchStatusText}";
                }
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
            else if (e.Key == Key.D) { MenuProperties_Click(sender, e); e.Handled = true; }
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
        else if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _isFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
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
