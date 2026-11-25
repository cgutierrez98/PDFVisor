using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PDFVisor.App.Dialogs;
using PDFVisor.App.Services;
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
    private Point _tabDragStart;
    private int _tabDragIndex = -1;
    private int _busyDepth;
    private (System.Security.Cryptography.X509Certificates.X509Certificate2 Cert, string? Reason, string? Location, string? TsaUrl, (byte[] BgraPixels, int WidthPx, int HeightPx)? StampImage)? _pendingSignatureRequest;

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
        LocalizationService.LanguageChanged += ApplyLocalization;
        ApplySavedSettings();

        DocumentTabs.ItemsSource = _workspace.OpenSessions;
        _workspace.PropertyChanged += Workspace_PropertyChanged;
        PdfViewport.PromptNoteText = () => PromptInput("Añadir nota adhesiva", "Escribe el texto de la nota:");
        PdfViewport.PromptEditAnnotation = annot => PromptInput($"Editar {annot.TypeDisplay}", "Texto / comentario:", annot.Contents);
        PdfViewport.PromptAddText = () => PromptInput("Insertar texto", "Escribe el texto que deseas añadir a la página:");
        PdfViewport.PromptFormFieldName = () => PromptInput("Nuevo campo de formulario", "Nombre identificador del campo:");
        PdfViewport.FormFieldDoubleClicked = EditFormFieldInteractive;
        PdfViewport.ExternalUriRequested = OnExternalUriRequested;
        PdfViewport.SignatureRegionSelected = OnSignatureRegionSelected;
        PdfViewport.ToolAutoReset = () => SyncToolToggleButtons(_boundSession?.ActiveTool ?? AnnotationTool.None);
        PdfViewport.SelectionChanged = () => SyncToolToggleButtons(_boundSession?.ActiveTool ?? AnnotationTool.None);

        RefreshRecentFilesUi();
        Loaded += (_, _) =>
        {
            if (App.StartupFiles.Length > 0)
            {
                foreach (var file in App.StartupFiles)
                {
                    if (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                        OpenFile(file);
                }
            }
            else if (_settings.RestoreOpenTabs && _settings.LastOpenTabPaths.Count > 0)
            {
                foreach (var path in _settings.LastOpenTabPaths.Where(System.IO.File.Exists))
                {
                    OpenFile(path);
                }
                if (_settings.LastActiveTabIndex >= 0 && _settings.LastActiveTabIndex < _workspace.OpenSessions.Count)
                {
                    _workspace.ActiveSession = _workspace.OpenSessions[_settings.LastActiveTabIndex];
                }
            }

            _ = CheckUpdatesInBackgroundAsync(silent: true);
        };
        ApplyLocalization();
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
        MenuRestoreTabs.IsChecked = _settings.RestoreOpenTabs;
        LocalizationService.CurrentLanguage = _settings.Language;
        SetSidebarOpen(_settings.IsSidebarOpen);
    }

    private void ApplyLocalization()
    {
        string lang = LocalizationService.CurrentLanguage;
        MenuLangEs.IsChecked = lang == "es";
        MenuLangEn.IsChecked = lang == "en";

        MenuRootFile.Header = LocalizationService.Get("Menu.File");
        MenuRootEdit.Header = LocalizationService.Get("Menu.Edit");
        MenuRootView.Header = LocalizationService.Get("Menu.View");
        MenuRootPages.Header = LocalizationService.Get("Menu.Pages");
        MenuRootTools.Header = LocalizationService.Get("Menu.Tools");
        MenuRootSecurity.Header = LocalizationService.Get("Menu.Security");
        MenuRootHelp.Header = LocalizationService.Get("Menu.Help");

        MenuOpen.Header = LocalizationService.Get("Menu.Open");
        MenuSave.Header = LocalizationService.Get("Menu.Save");
        MenuSaveAs.Header = LocalizationService.Get("Menu.SaveAs");
        MenuPrint.Header = LocalizationService.Get("Menu.Print");
        MenuCloseTab.Header = LocalizationService.Get("Menu.CloseTab");
        MenuExit.Header = LocalizationService.Get("Menu.Exit");
        MenuRootLanguage.Header = LocalizationService.Get("Menu.Language");

        TxtWelcomeSubtitle.Text = LocalizationService.Get("Welcome.Subtitle");
        BtnWelcomeOpen.Content = LocalizationService.Get("Welcome.OpenButton");
        TxtWelcomeRecent.Text = LocalizationService.Get("Welcome.Recent");
        BtnWelcomeClear.Content = LocalizationService.Get("Welcome.Clear");

        if (_boundSession is null)
            StatusMessage.Text = LocalizationService.Get("Status.Ready");

        UpdateSidePanelHeaderText();
    }

    private void UpdateSidePanelHeaderText()
    {
        TxtSidePanelTitle.Text = true switch
        {
            _ when RailThumbnails.IsChecked == true => LocalizationService.Get("Panel.Thumbnails"),
            _ when RailBookmarks.IsChecked == true => LocalizationService.Get("Panel.Bookmarks"),
            _ when RailComments.IsChecked == true => LocalizationService.Get("Panel.Comments"),
            _ when RailForms.IsChecked == true => LocalizationService.Get("Panel.Forms"),
            _ when RailSignatures.IsChecked == true => LocalizationService.Get("Panel.Signatures"),
            _ when RailAttachments.IsChecked == true => LocalizationService.Get("Panel.Attachments"),
            _ => LocalizationService.Get("Panel.Objects")
        };
    }

    private void MenuLang_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string code }) return;
        LocalizationService.CurrentLanguage = code;
        _settings.Language = LocalizationService.CurrentLanguage;
        _settings.Save();
    }

    private void MenuRestoreTabs_Click(object sender, RoutedEventArgs e)
    {
        _settings.RestoreOpenTabs = MenuRestoreTabs.IsChecked;
        _settings.Save();
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
        _settings.Language = LocalizationService.CurrentLanguage;
        _settings.RestoreOpenTabs = MenuRestoreTabs.IsChecked;

        if (_boundSession is not null)
        {
            _settings.DarkReadingMode = _boundSession.DarkReadingMode;
            _settings.DefaultZoom = _boundSession.Zoom;
        }

        // Persist per-document reading position & open tab session
        foreach (var s in _workspace.OpenSessions)
        {
            if (System.IO.File.Exists(s.FilePath))
            {
                RecentFilesService.Record(s.FilePath, s.CurrentPageIndex, s.Zoom);
            }
        }

        if (_settings.RestoreOpenTabs)
        {
            _settings.LastOpenTabPaths = _workspace.OpenSessions
                .Select(s => s.FilePath)
                .Where(System.IO.File.Exists)
                .ToList();
            _settings.LastActiveTabIndex = _boundSession is not null
                ? Math.Max(0, _workspace.OpenSessions.IndexOf(_boundSession))
                : 0;
        }
        else
        {
            _settings.LastOpenTabPaths.Clear();
            _settings.LastActiveTabIndex = 0;
        }

        _settings.Save();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        PdfViewport.CommitAndCloseInlineEditor(save: true);
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
        LocalizationService.LanguageChanged -= ApplyLocalization;
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
            BookmarkTreeView.ItemsSource = session.BookmarkTree;
            AnnotationList.ItemsSource = session.Annotations;
            FormFieldList.ItemsSource = session.FormFields;
            SignatureList.ItemsSource = session.Signatures;
            AttachmentList.ItemsSource = session.Attachments;
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
            BookmarkTreeView.ItemsSource = null;
            AnnotationList.ItemsSource = null;
            FormFieldList.ItemsSource = null;
            SignatureList.ItemsSource = null;
            AttachmentList.ItemsSource = null;
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

        try
        {
            await Task.Delay(120, ct);
        }
        catch (OperationCanceledException) { return; }

        const int maxThumbWidth = 148;
        var pagesSnapshot = session.Pages.ToArray();
        if (pagesSnapshot.Length == 0) return;

        int anchor = Math.Clamp(session.CurrentPageIndex, 0, pagesSnapshot.Length - 1);
        var ordered = pagesSnapshot
            .OrderBy(p => Math.Abs(p.PageIndex - anchor))
            .ToArray();

        foreach (var page in ordered)
        {
            if (ct.IsCancellationRequested) break;
            if (page.ThumbnailImage is not null) continue;

            double aspect = page.HeightPt / Math.Max(1.0, page.WidthPt);
            int w = maxThumbWidth;
            int h = Math.Clamp((int)Math.Round(w * aspect), 40, 260);

            try
            {
                await session.RenderGate.WaitAsync(ct);
                byte[] bgra;
                try
                {
                    if (ct.IsCancellationRequested) break;
                    bgra = await Task.Run(() =>
                        session.Document.RenderPageBgra(page.PageIndex, w, h, rotation: 0, lcdText: false), ct);
                }
                finally
                {
                    try { session.RenderGate.Release(); } catch (ObjectDisposedException) { }
                }

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
        bool alreadyOpen = _workspace.OpenSessions.Any(s => string.Equals(s.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        var prevHistory = RecentFilesService.Find(filePath);

        try
        {
            var session = _workspace.OpenDocument(filePath);
            if (!alreadyOpen)
            {
                ApplyInitialSessionPreferences(session, prevHistory);
            }
            RecentFilesService.Record(filePath, session.CurrentPageIndex, session.Zoom);
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
                var session = _workspace.OpenDocument(filePath, pwd);
                if (!alreadyOpen)
                {
                    ApplyInitialSessionPreferences(session, prevHistory);
                }
                RecentFilesService.Record(filePath, session.CurrentPageIndex, session.Zoom);
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

    private void ApplyInitialSessionPreferences(DocumentSession session, RecentFileItem? recent)
    {
        session.DarkReadingMode = _settings.DarkReadingMode;
        if (recent is not null && recent.LastZoom >= 0.25)
        {
            session.Zoom = recent.LastZoom;
        }
        else if (_settings.DefaultZoom >= 0.25)
        {
            session.Zoom = _settings.DefaultZoom;
        }

        if (recent is not null && recent.LastPageIndex > 0 && recent.LastPageIndex < session.PageCount)
        {
            session.CurrentPageIndex = recent.LastPageIndex;
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

        BtnNavBack.IsEnabled = hasDoc && session!.CanNavigateBack;
        BtnNavForward.IsEnabled = hasDoc && session!.CanNavigateForward;
        MenuNavBack.IsEnabled = BtnNavBack.IsEnabled;
        MenuNavForward.IsEnabled = BtnNavForward.IsEnabled;

        MenuCopy.IsEnabled = hasDoc;
        MenuFind.IsEnabled = hasDoc;
        MenuInsertImage.IsEnabled = hasDoc;
        MenuAddTextField.IsEnabled = hasDoc;
        MenuAddCheckboxField.IsEnabled = hasDoc;
        MenuZoomIn.IsEnabled = hasDoc;
        MenuZoomOut.IsEnabled = hasDoc;
        MenuResetZoom.IsEnabled = hasDoc;
        MenuFitWidth.IsEnabled = hasDoc;
        MenuFitPage.IsEnabled = hasDoc;
        MenuTwoPage.IsEnabled = hasDoc;
        MenuDarkRead.IsEnabled = hasDoc;
        MenuShowStickyNotes.IsEnabled = hasDoc;
        MenuExpandAllNotes.IsEnabled = hasDoc && session!.StickyNoteCount > 0;
        MenuCollapseAllNotes.IsEnabled = hasDoc && session!.StickyNoteCount > 0;

        MenuInsertBlankPage.IsEnabled = hasDoc;
        MenuCropPage.IsEnabled = hasDoc;
        MenuRotatePage.IsEnabled = hasDoc;
        MenuMoveUp.IsEnabled = multiPage && session!.CurrentPageIndex > 0;
        MenuMoveDown.IsEnabled = multiPage && session!.CurrentPageIndex < session.PageCount - 1;
        MenuMergePdf.IsEnabled = hasDoc;
        MenuSplitPdf.IsEnabled = multiPage;
        MenuExtractPage.IsEnabled = hasDoc;
        MenuDeletePage.IsEnabled = multiPage;

        MenuWatermark.IsEnabled = hasDoc;
        MenuComparePdf.IsEnabled = hasDoc;
        MenuFlattenPdf.IsEnabled = hasDoc;
        MenuOcrPage.IsEnabled = hasDoc;
        MenuOcrAll.IsEnabled = hasDoc;
        MenuSignPades.IsEnabled = hasDoc;
        MenuEncrypt.IsEnabled = hasDoc;
        MenuDecrypt.IsEnabled = hasDoc;
        MenuSanitize.IsEnabled = hasDoc;

        BtnPrev.IsEnabled = hasDoc && session!.CurrentPageIndex > 0;
        BtnNext.IsEnabled = hasDoc && session!.CurrentPageIndex < session.PageCount - 1;
        TxtPageJump.IsEnabled = hasDoc;
        BtnZoomIn.IsEnabled = hasDoc;
        BtnZoomOut.IsEnabled = hasDoc;
        CmbZoom.IsEnabled = hasDoc;
        BtnRotatePage.IsEnabled = hasDoc;

        ToolSelect.IsEnabled = hasDoc;
        ToolHand.IsEnabled = hasDoc;
        ToolHighlight.IsEnabled = hasDoc;
        ToolUnderline.IsEnabled = hasDoc;
        ToolStrikeOut.IsEnabled = hasDoc;
        ToolSquare.IsEnabled = hasDoc;
        ToolNote.IsEnabled = hasDoc;
        ToolInk.IsEnabled = hasDoc;
        ToolAddText.IsEnabled = hasDoc;
        ToolRedact.IsEnabled = hasDoc;
        BtnToggleStickyNotes.IsEnabled = hasDoc;
        BtnDarkRead.IsEnabled = hasDoc;
        BtnSearchToggle.IsEnabled = hasDoc;

        if (hasDoc && session!.CommentCount > 0)
        {
            TxtCommentsCount.Text = session.CommentCount > 99 ? "99+" : session.CommentCount.ToString();
            CommentsCountBadge.Visibility = Visibility.Visible;
        }
        else
        {
            CommentsCountBadge.Visibility = Visibility.Collapsed;
        }

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
                BtnToggleStickyNotes.IsChecked = session.ShowStickyNotes;
                MenuShowStickyNotes.IsChecked = session.ShowStickyNotes;
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
                StatusMessage.Text = LocalizationService.Get("Status.Ready");
                StatusPageSize.Text = string.Empty;
                StatusZoom.Text = string.Empty;
                BtnDarkRead.IsChecked = false;
                MenuDarkRead.IsChecked = false;
                BtnToggleStickyNotes.IsChecked = true;
                MenuShowStickyNotes.IsChecked = true;
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

        ToggleButton[] rail = [RailThumbnails, RailBookmarks, RailComments, RailForms, RailSignatures, RailAttachments, RailObjects];
        bool alreadyActive = clicked.IsChecked == false;
        if (alreadyActive)
        {
            SetSidebarOpen(false);
            return;
        }

        foreach (var btn in rail)
            btn.IsChecked = ReferenceEquals(btn, clicked);

        PageList.Visibility = ReferenceEquals(clicked, RailThumbnails) ? Visibility.Visible : Visibility.Collapsed;
        BookmarksPanel.Visibility = ReferenceEquals(clicked, RailBookmarks) ? Visibility.Visible : Visibility.Collapsed;
        CommentsPanel.Visibility = ReferenceEquals(clicked, RailComments) ? Visibility.Visible : Visibility.Collapsed;
        FormsPanel.Visibility = ReferenceEquals(clicked, RailForms) ? Visibility.Visible : Visibility.Collapsed;
        SignaturesPanel.Visibility = ReferenceEquals(clicked, RailSignatures) ? Visibility.Visible : Visibility.Collapsed;
        AttachmentsPanel.Visibility = ReferenceEquals(clicked, RailAttachments) ? Visibility.Visible : Visibility.Collapsed;
        PageObjectList.Visibility = ReferenceEquals(clicked, RailObjects) ? Visibility.Visible : Visibility.Collapsed;

        UpdateSidePanelHeaderText();
        SetSidebarOpen(true);
    }

    private void BtnCollapseSidebar_Click(object sender, RoutedEventArgs e)
    {
        ToggleButton[] rail = [RailThumbnails, RailBookmarks, RailComments, RailForms, RailSignatures, RailAttachments, RailObjects];
        foreach (var btn in rail) btn.IsChecked = false;
        SetSidebarOpen(false);
    }

    private void SetSidebarOpen(bool open)
    {
        _isSidebarOpen = open;
        SidePanelBorder.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SideSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidePanelColumn.Width = open ? new GridLength(250) : new GridLength(0);
        SideSplitterColumn.Width = open ? new GridLength(4) : new GridLength(0);
    }

    // --- Segmented Tool Toggle Buttons ---
    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || sender is not ToggleButton clicked) return;

        AnnotationTool chosen = clicked switch
        {
            _ when ReferenceEquals(clicked, ToolHand) => AnnotationTool.HandPan,
            _ when ReferenceEquals(clicked, ToolHighlight) => AnnotationTool.Highlight,
            _ when ReferenceEquals(clicked, ToolUnderline) => AnnotationTool.Underline,
            _ when ReferenceEquals(clicked, ToolStrikeOut) => AnnotationTool.StrikeOut,
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
        ToolHand.IsChecked = tool == AnnotationTool.HandPan;
        ToolHighlight.IsChecked = tool == AnnotationTool.Highlight;
        ToolUnderline.IsChecked = tool == AnnotationTool.Underline;
        ToolStrikeOut.IsChecked = tool == AnnotationTool.StrikeOut;
        ToolSquare.IsChecked = tool == AnnotationTool.Square;
        ToolNote.IsChecked = tool == AnnotationTool.Note;
        ToolInk.IsChecked = tool == AnnotationTool.Ink;
        ToolAddText.IsChecked = tool == AnnotationTool.AddText;
        ToolRedact.IsChecked = tool == AnnotationTool.Redact;

        bool hasSelectedTextObj = PdfViewport.SelectedTextObject is not null;
        bool showColorPicker = hasSelectedTextObj || tool is AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.StrikeOut
            or AnnotationTool.Square or AnnotationTool.Note or AnnotationTool.Ink or AnnotationTool.AddText;
        ToolOptionsBar.Visibility = showColorPicker ? Visibility.Visible : Visibility.Collapsed;
        CmbFontSize.Visibility = (tool == AnnotationTool.AddText || hasSelectedTextObj) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || sender is not Button { Tag: string hex }) return;
        if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint rgba))
        {
            _boundSession.ActiveColorRgba = rgba;
            if (PdfViewport.UpdateSelectedTextStyle(newColorRgba: rgba))
                StatusMessage.Text = "Color del texto actualizado";
            else
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
            if (PdfViewport.UpdateSelectedTextStyle(newFontSize: pt))
                StatusMessage.Text = $"Tamaño de texto actualizado a {pt:0.#} pt";
        }
    }

    // --- Context Menu Selection Markup ---
    private void MenuMarkupHighlight_Click(object sender, RoutedEventArgs e)
    {
        if (PdfViewport.ApplyMarkupToSelection(PdfAnnotSubtype.Highlight))
            StatusMessage.Text = "Selección resaltada";
    }

    private void MenuMarkupUnderline_Click(object sender, RoutedEventArgs e)
    {
        if (PdfViewport.ApplyMarkupToSelection(PdfAnnotSubtype.Underline))
            StatusMessage.Text = "Selección subrayada";
    }

    private void MenuMarkupStrikeOut_Click(object sender, RoutedEventArgs e)
    {
        if (PdfViewport.ApplyMarkupToSelection(PdfAnnotSubtype.StrikeOut))
            StatusMessage.Text = "Selección tachada";
    }

    // --- File & Tab Actions (including Middle-Click Close & Drag Reorder) ---
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
        PdfViewport.CommitAndCloseInlineEditor(save: true);
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
        PdfViewport.CommitAndCloseInlineEditor(save: true);
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
        PdfViewport.CommitAndCloseInlineEditor(save: true);
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

    private void DocumentTabs_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        int idx = GetTabIndexAt(e.OriginalSource as DependencyObject, out var hitSession);
        if (e.ChangedButton == MouseButton.Middle && hitSession is not null)
        {
            TryCloseSession(hitSession);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left)
        {
            _tabDragStart = e.GetPosition(DocumentTabs);
            _tabDragIndex = idx;
        }
    }

    private void DocumentTabs_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tabDragIndex < 0 || _workspace.OpenSessions.Count <= 1)
            return;
        Point cur = e.GetPosition(DocumentTabs);
        if (Math.Abs(cur.X - _tabDragStart.X) > SystemParameters.MinimumHorizontalDragDistance * 1.5)
        {
            int idx = _tabDragIndex;
            _tabDragIndex = -1;
            DragDrop.DoDragDrop(DocumentTabs, new DataObject("PDFVisor.TabIndex", idx), DragDropEffects.Move);
        }
    }

    private void DocumentTabs_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("PDFVisor.TabIndex")) return;
        int fromIdx = (int)e.Data.GetData("PDFVisor.TabIndex");
        int toIdx = GetTabIndexAt(e.OriginalSource as DependencyObject, out _);
        if (toIdx >= 0 && fromIdx != toIdx)
        {
            _workspace.ReorderSession(fromIdx, toIdx);
            DocumentTabs.SelectedItem = _workspace.ActiveSession;
        }
        e.Handled = true;
    }

    private int GetTabIndexAt(DependencyObject? source, out DocumentSession? session)
    {
        session = null;
        while (source is not null && source is not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        if (source is ListBoxItem item && item.DataContext is DocumentSession s)
        {
            session = s;
            return _workspace.OpenSessions.IndexOf(s);
        }
        return -1;
    }

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
            string outPath = dlg.FileName;
            await RunBusyAsync("Exportando página como imagen...", $"Renderizando página {pageNum} a 200 DPI...", () =>
                PdfWorkflowService.ExportPageAsImageAsync(session, pageIdx, outPath, exportDpi: 200.0));
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
            var progress = new Progress<(int Index, int Total, string FileName)>(p =>
                UpdateBusyProgress($"Imagen {p.Index} de {p.Total}: {p.FileName}", (p.Index * 100.0) / Math.Max(1, p.Total)));

            await RunBusyAsync("Creando PDF desde imágenes...", $"Procesando {files.Length} imagen(es)...", () =>
                PdfWorkflowService.CreatePdfFromImagesAsync(files, destPath, progress));

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
        PdfViewport.CommitAndCloseInlineEditor(save: true);
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
        if (System.IO.File.Exists(session.FilePath))
        {
            RecentFilesService.Record(session.FilePath, session.CurrentPageIndex, session.Zoom);
        }
        _workspace.CloseSession(session);
    }

    // --- Navigation, Back/Forward History & Zoom ---
    private void BtnNavBack_Click(object sender, RoutedEventArgs e) => _boundSession?.NavigateBack();
    private void BtnNavForward_Click(object sender, RoutedEventArgs e) => _boundSession?.NavigateForward();

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
            _boundSession.JumpToPageWithHistory(Math.Clamp(pageNum - 1, 0, _boundSession.PageCount - 1));
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
    private void BtnFitPage_Click(object sender, RoutedEventArgs e) => PdfViewport.FitToPage();

    private void BtnResetZoom_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null) _boundSession.Zoom = 1.0;
    }

    private void CmbZoom_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingControls || _boundSession is null || CmbZoom.SelectedItem is not ComboBoxItem item) return;
        string? text = item.Content?.ToString();
        if (text is "Ajustar" or "Ancho")
        {
            PdfViewport.FitToWidth();
        }
        else if (text == "Página")
        {
            PdfViewport.FitToPage();
        }
        else if (text is not null && text.EndsWith('%') && double.TryParse(text.TrimEnd('%'), out double pct))
        {
            _boundSession.Zoom = pct / 100.0;
        }
    }

    private void BtnDarkRead_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null)
        {
            _boundSession.DarkReadingMode = BtnDarkRead.IsChecked == true;
            _settings.DarkReadingMode = _boundSession.DarkReadingMode;
        }
    }

    private void MenuDarkRead_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null)
        {
            _boundSession.DarkReadingMode = MenuDarkRead.IsChecked;
            _settings.DarkReadingMode = _boundSession.DarkReadingMode;
        }
    }

    private void MenuTwoPage_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is not null)
            _boundSession.TwoPageMode = MenuTwoPage.IsChecked;
    }

    private void BtnToggleStickyNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        _boundSession.ShowStickyNotes = BtnToggleStickyNotes.IsChecked == true;
        StatusMessage.Text = _boundSession.ShowStickyNotes
            ? "Notas adhesivas visibles en la página"
            : "Notas adhesivas ocultas en la página";
    }

    private void MenuShowStickyNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        _boundSession.ShowStickyNotes = MenuShowStickyNotes.IsChecked;
        StatusMessage.Text = _boundSession.ShowStickyNotes
            ? "Notas adhesivas visibles en la página"
            : "Notas adhesivas ocultas en la página";
    }

    private void MenuContextToggleNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        _boundSession.ShowStickyNotes = !_boundSession.ShowStickyNotes;
        StatusMessage.Text = _boundSession.ShowStickyNotes
            ? "Notas adhesivas visibles en la página"
            : "Notas adhesivas ocultas en la página";
    }

    private void BtnExpandAllNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        _boundSession.ExpandAllStickyNotes();
        StatusMessage.Text = "Todas las notas adhesivas desplegadas en sus páginas";
    }

    private void BtnCollapseAllNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        _boundSession.CollapseAllStickyNotes();
        StatusMessage.Text = "Notas adhesivas contraídas a iconos";
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

    // --- Edit, History, Pages, Blank Page, Crop & Flatten ---
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

    private void BtnInsertBlankPage_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        _boundSession.InsertBlankPageAtCurrent();
        StatusMessage.Text = $"Página en blanco insertada (página {_boundSession.CurrentPageNumber})";
    }

    private void BtnCropPage_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        _boundSession.ActiveTool = AnnotationTool.CropPage;
        SyncToolToggleButtons(AnnotationTool.CropPage);
        StatusMessage.Text = "Arrastra con el ratón el área que deseas conservar de la página (Esc para cancelar)";
    }

    private void BtnFlattenPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var confirm = MessageBox.Show(
            this,
            "¿Deseas aplanar todos los campos de formulario y anotaciones para convertirlos en contenido fijo de la página?\n(Podrás deshacerlo con Ctrl+Z mientras la sesión siga abierta).",
            "Aplanar documento PDF",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            PdfViewport.CommitAndCloseInlineEditor(save: true);
            _boundSession.FlattenDocument();
            StatusMessage.Text = "Formularios y anotaciones aplanados correctamente";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo aplanar el documento:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
            StatusMessage.Text = "Imagen insertada (puedes arrastrarla o redimensionarla desde la esquina inferior derecha)";
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
        _boundSession.ActiveTool = AnnotationTool.AddFormField;
        SyncToolToggleButtons(AnnotationTool.AddFormField);
        StatusMessage.Text = "Arrastra con el ratón sobre la página donde deseas crear el campo de texto (Esc para cancelar)";
    }

    private void BtnAddCheckboxField_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        _boundSession.ActiveTool = AnnotationTool.AddCheckboxField;
        SyncToolToggleButtons(AnnotationTool.AddCheckboxField);
        StatusMessage.Text = "Haz clic o arrastra sobre la página donde deseas crear la casilla de verificación (Esc para cancelar)";
    }

    // --- Security, Decrypt, Optimization & OCR ---
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
        var encDlg = new EncryptPdfDialog { Owner = this };
        if (encDlg.ShowDialog() != true) return;

        var saveDlg = new SaveFileDialog
        {
            Title = "Guardar PDF protegido (AES-256)",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(session.FileName)}_protegido.pdf"
        };
        if (saveDlg.ShowDialog(this) == true)
        {
            try
            {
                string dest = saveDlg.FileName;
                string uPwd = encDlg.UserPassword;
                string oPwd = encDlg.OwnerPassword;
                bool allowPrint = encDlg.AllowPrint;
                bool allowModify = encDlg.AllowModify;
                bool allowExtract = encDlg.AllowExtract;

                await RunBusyAsync("Cifrando documento (AES-256)...", System.IO.Path.GetFileName(dest), () =>
                    Task.Run(() => session.EncryptAes256AndExport(dest, uPwd, oPwd, allowPrint, allowModify, allowExtract)));
                StatusMessage.Text = $"Documento protegido guardado: {System.IO.Path.GetFileName(dest)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al proteger el documento:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void BtnDecrypt_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var session = _boundSession;
        var dlg = new SaveFileDialog
        {
            Title = "Guardar copia sin contraseña",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(session.FileName)}_sin_clave.pdf"
        };
        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                string dest = dlg.FileName;
                await RunBusyAsync("Eliminando cifrado del PDF...", System.IO.Path.GetFileName(dest), () =>
                    Task.Run(() => session.DecryptAndExport(dest)));
                StatusMessage.Text = $"PDF sin contraseña guardado: {System.IO.Path.GetFileName(dest)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"No se pudo quitar la contraseña:\n{ex.Message}", "Error",
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

    private void OnExternalUriRequested(string rawUri)
    {
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeMailto))
        {
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"¿Deseas abrir el siguiente enlace externo?\n\n{uri}",
            "Abrir enlace externo",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private async void OnSignatureRegionSelected(int pageIndex, PvRect pdfRect)
    {
        SyncToolToggleButtons(AnnotationTool.None);
        if (_boundSession is null || _pendingSignatureRequest is not { } req) return;
        _pendingSignatureRequest = null;
        _boundSession.CurrentPageIndex = pageIndex;
        await ExecuteSignPadesExportAsync(_boundSession, req.Cert, req.Reason, req.Location, req.TsaUrl, pdfRect, req.StampImage);
    }

    private async void BtnSignPades_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        PdfViewport.CommitAndCloseInlineEditor(save: true);
        var session = _boundSession;
        var certs = PadesSignatureService.ListWindowsSigningCertificates();

        var picker = new CertificatePickerDialog(certs) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedCertificate is null) return;

        if (picker.DrawPositionOnPage)
        {
            _pendingSignatureRequest = (picker.SelectedCertificate, picker.Reason, picker.Location, picker.TsaUrl, picker.StampImage);
            session.ActiveTool = AnnotationTool.SignStamp;
            SyncToolToggleButtons(AnnotationTool.SignStamp);
            StatusMessage.Text = "Arrastra con el ratón sobre la página donde deseas estampar tu firma digital (Esc para cancelar)";
            return;
        }

        await ExecuteSignPadesExportAsync(session, picker.SelectedCertificate, picker.Reason, picker.Location, picker.TsaUrl, customRect: null, stampImage: picker.StampImage);
    }

    private async Task ExecuteSignPadesExportAsync(
        DocumentSession session,
        System.Security.Cryptography.X509Certificates.X509Certificate2 cert,
        string? reason,
        string? location,
        string? tsaUrl,
        PvRect? customRect,
        (byte[] BgraPixels, int WidthPx, int HeightPx)? stampImage)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Guardar PDF firmado",
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = $"{System.IO.Path.GetFileNameWithoutExtension(session.FileName)}_firmado.pdf"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            string dest = dlg.FileName;
            string detail = string.IsNullOrWhiteSpace(tsaUrl)
                ? "Aplicando firma digital PAdES..."
                : "Aplicando firma digital PAdES y sello de tiempo TSA...";
            await RunBusyAsync("Firmando documento digitalmente...", detail, () =>
                Task.Run(() => session.SignPadesAndExport(dest, cert, reason: reason, location: location, tsaUrl: tsaUrl, customWidgetRect: customRect, stampImage: stampImage)));
            StatusMessage.Text = $"Documento firmado: {System.IO.Path.GetFileName(dest)}";
            OpenFile(dest);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al firmar:\n{ex.Message}", "Error de firma",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnOcrPage_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || _boundSession.PageCount == 0) return;
        var session = _boundSession;
        int pageIdx = session.CurrentPageIndex;
        try
        {
            int words = 0;
            await RunBusyAsync("Reconociendo texto (OCR)...", $"Analizando página {pageIdx + 1}...", async () =>
            {
                words = await OcrService.RecognizePageAsync(session, pageIdx);
            });
            StatusMessage.Text = $"OCR completado ({words} palabras reconocidas)";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error en OCR:\n{ex.Message}", "Error OCR", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnOcrAll_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || _boundSession.PageCount == 0) return;
        var session = _boundSession;
        int totalPages = session.PageCount;

        try
        {
            int totalWords = 0;
            var progress = new Progress<(int PageNumber, int TotalPages, int WordsSoFar)>(p =>
            {
                UpdateBusyProgress($"Página {p.PageNumber} de {p.TotalPages} ({p.WordsSoFar} palabras)...", (p.PageNumber - 1) * 100.0 / Math.Max(1, p.TotalPages));
                StatusMessage.Text = $"Reconociendo texto (OCR): página {p.PageNumber} de {p.TotalPages}...";
            });

            await RunBusyAsync("Reconociendo texto (OCR por lotes)...", $"Página 1 de {totalPages}...", async () =>
            {
                totalWords = await OcrService.RecognizeAllPagesAsync(session, progress);
                session.NotifyExternalBatchMutated(session.CurrentPageIndex);
                UpdateBusyProgress("Finalizando...", 100);
            });
            StatusMessage.Text = $"OCR completado: {totalWords} palabras en {totalPages} páginas";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error en OCR por lotes:\n{ex.Message}", "Error OCR", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private async Task RunSearchFromUiAsync()
    {
        if (_boundSession is null) return;
        bool matchCase = BtnSearchMatchCase.IsChecked == true;
        bool wholeWord = BtnSearchWholeWord.IsChecked == true;

        StatusProgressBar.Visibility = Visibility.Visible;
        StatusMessage.Text = "Buscando en el documento...";
        try
        {
            await _boundSession.SearchAsync(TxtSearch.Text, matchCase, wholeWord);
        }
        finally
        {
            StatusProgressBar.Visibility = Visibility.Collapsed;
            StatusMessage.Text = string.IsNullOrWhiteSpace(_boundSession.SearchStatusText) ? "Listo" : $"Búsqueda: {_boundSession.SearchStatusText}";
        }
    }

    private async void BtnSearchOption_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || string.IsNullOrWhiteSpace(TxtSearch.Text)) return;
        await RunSearchFromUiAsync();
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
            bool sameOptions = _boundSession.SearchMatchCase == (BtnSearchMatchCase.IsChecked == true) &&
                               _boundSession.SearchWholeWord == (BtnSearchWholeWord.IsChecked == true);
            if (sameOptions &&
                string.Equals(_boundSession.SearchQuery, TxtSearch.Text, StringComparison.Ordinal) &&
                _boundSession.TotalSearchMatches > 0)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift) _boundSession.PreviousSearchMatch();
                else _boundSession.NextSearchMatch();
            }
            else
            {
                await RunSearchFromUiAsync();
            }
            e.Handled = true;
        }
    }

    private void BtnSearchPrev_Click(object sender, RoutedEventArgs e) => _boundSession?.PreviousSearchMatch();
    private void BtnSearchNext_Click(object sender, RoutedEventArgs e) => _boundSession?.NextSearchMatch();

    // --- Side Panel Interactions (Thumbnails, Bookmarks TreeView, Comments, Forms, Attachments, Objects) ---
    private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isUpdatingControls && _boundSession is not null &&
            PageList.SelectedIndex >= 0 && PageList.SelectedIndex < _boundSession.PageCount)
        {
            _boundSession.CurrentPageIndex = PageList.SelectedIndex;
        }
    }

    private void BookmarkTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_boundSession is not null &&
            e.NewValue is BookmarkItemViewModel bm &&
            bm.PageIndex >= 0 && bm.PageIndex < _boundSession.PageCount)
        {
            _boundSession.JumpToPageWithHistory(bm.PageIndex);
        }
    }

    private void BtnAddBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        string? title = PromptInput("Nuevo marcador", $"Título del marcador para la página {_boundSession.CurrentPageNumber}:", $"Página {_boundSession.CurrentPageNumber}");
        if (string.IsNullOrWhiteSpace(title)) return;
        int level = BookmarkTreeView.SelectedItem is BookmarkItemViewModel selected ? selected.Level : 0;
        try
        {
            _boundSession.AddBookmark(title.Trim(), _boundSession.CurrentPageIndex, level);
            StatusMessage.Text = $"Marcador \"{title.Trim()}\" añadido";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo añadir el marcador:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnRenameBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || BookmarkTreeView.SelectedItem is not BookmarkItemViewModel bm) return;
        string? newTitle = PromptInput("Renombrar marcador", "Nuevo título:", bm.Title);
        if (string.IsNullOrWhiteSpace(newTitle)) return;
        try
        {
            _boundSession.RenameBookmark(bm, newTitle.Trim());
            StatusMessage.Text = "Marcador renombrado";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo renombrar el marcador:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnDeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || BookmarkTreeView.SelectedItem is not BookmarkItemViewModel bm) return;
        try
        {
            _boundSession.DeleteBookmark(bm);
            StatusMessage.Text = $"Marcador \"{bm.Title}\" eliminado";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo eliminar el marcador:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AnnotationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_boundSession is not null && AnnotationList.SelectedItem is PdfAnnotationInfo annot &&
            annot.PageIndex >= 0 && annot.PageIndex < _boundSession.PageCount)
        {
            _boundSession.CurrentPageIndex = annot.PageIndex;
            if (annot.Subtype == PdfAnnotSubtype.Text)
            {
                _boundSession.ShowStickyNotes = true;
                _boundSession.SetStickyNoteExpanded(annot.PageIndex, annot.Index, true);
            }
        }
    }

    private void AnnotationList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => BtnEditAnnotation_Click(sender, e);

    private void BtnEditAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || AnnotationList.SelectedItem is not PdfAnnotationInfo annot) return;
        if (annot.Subtype == PdfAnnotSubtype.Text)
        {
            PdfViewport.FocusStickyNote(annot.PageIndex, annot.Index);
            StatusMessage.Text = "Nota adhesiva abierta sobre la página para edición";
            return;
        }

        string? edited = PromptInput($"Editar {annot.TypeDisplay}", "Comentario / nota:", annot.Contents);
        if (edited is null) return;
        try
        {
            _boundSession.UpdateExistingAnnotation(annot, newContents: edited);
            StatusMessage.Text = "Comentario actualizado";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al editar anotación:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnDeleteAnnotation_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || AnnotationList.SelectedItem is not PdfAnnotationInfo annot) return;
        try
        {
            _boundSession.DeleteExistingAnnotation(annot);
            StatusMessage.Text = "Anotación eliminada (Ctrl+Z para deshacer)";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al eliminar anotación:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AnnotationList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            BtnDeleteAnnotation_Click(sender, e);
            e.Handled = true;
        }
    }

    private void BtnAddAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null) return;
        var dlg = new OpenFileDialog { Title = "Adjuntar archivo dentro del PDF", Filter = "Todos los archivos (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _boundSession.AddAttachmentFromFile(dlg.FileName);
            StatusMessage.Text = $"Archivo adjuntado: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo adjuntar el archivo:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnSaveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || AttachmentList.SelectedItem is not PdfAttachmentInfo att) return;
        var dlg = new SaveFileDialog { Title = "Guardar archivo adjunto", FileName = att.Name, Filter = "Todos los archivos (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _boundSession.ExtractAttachmentToFile(att.Index, dlg.FileName);
            StatusMessage.Text = $"Adjunto guardado: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo extraer el adjunto:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnDeleteAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (_boundSession is null || AttachmentList.SelectedItem is not PdfAttachmentInfo att) return;
        try
        {
            _boundSession.DeleteAttachmentAt(att.Index, att.Name);
            StatusMessage.Text = $"Adjunto eliminado: {att.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo eliminar el adjunto:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AttachmentList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => BtnSaveAttachment_Click(sender, e);

    private void AttachmentList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            BtnDeleteAttachment_Click(sender, e);
            e.Handled = true;
        }
    }

    private void FormFieldList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FormFieldList.SelectedItem is PdfFormField field)
            EditFormFieldInteractive(field);
    }

    private void EditFormFieldInteractive(PdfFormField field)
    {
        if (_boundSession is null) return;

        if (string.Equals(field.FieldType, "/Btn", StringComparison.Ordinal))
        {
            try
            {
                _boundSession.SetFormFieldValue(field.Name, field.IsChecked ? "Off" : "Yes");
                StatusMessage.Text = $"Casilla \"{field.Name}\" actualizada";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Error al alternar casilla:\n{ex.Message}", "Error de formulario",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        string? newVal = PromptInput($"Editar campo \"{field.Name}\"", "Nuevo valor:", initialValue: field.Value);
        if (newVal is null) return;
        try { _boundSession.SetFormFieldValue(field.Name, newVal); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al actualizar campo:\n{ex.Message}", "Error de formulario",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FormFieldList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _boundSession is null || FormFieldList.SelectedItem is not PdfFormField field)
            return;

        try
        {
            _boundSession.RemoveFormField(field.Name);
            StatusMessage.Text = $"Campo \"{field.Name}\" eliminado (Ctrl+Z para deshacer)";
            e.Handled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Error al eliminar campo:\n{ex.Message}", "Error de formulario",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PageObjectList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || _boundSession is null || PageObjectList.SelectedItem is not PdfPageObjectInfo obj)
            return;

        try
        {
            int pageIdx = _boundSession.CurrentPageIndex;
            int objIdx = obj.Index;
            _boundSession.RemovePageObjectUndoable(pageIdx, objIdx);
            StatusMessage.Text = $"Objeto #{objIdx} eliminado de la página {pageIdx + 1} (Ctrl+Z para deshacer)";
            e.Handled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo eliminar el objeto:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PageObjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingControls || _boundSession is null || PageObjectList.SelectedItem is not PdfPageObjectInfo obj)
            return;
        if (obj.Type == PdfPageObjectType.Text && !obj.IsInvisible)
        {
            PdfViewport.SelectTextObject(_boundSession.CurrentPageIndex, obj, openInlineEditor: false);
            StatusMessage.Text = "Texto seleccionado en la página (arrastra para mover, esquina o A+/A- para tamaño, doble clic para editar)";
        }
    }

    private void PageObjectList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_boundSession is null || PageObjectList.SelectedItem is not PdfPageObjectInfo obj) return;
        if (obj.Type != PdfPageObjectType.Text) return;

        PdfViewport.SelectTextObject(_boundSession.CurrentPageIndex, obj, openInlineEditor: true);
        StatusMessage.Text = "Editando texto directamente sobre la página (Enter para guardar, Esc para cancelar)";
    }

    private string? PromptInput(string title, string prompt, string initialValue = "", bool isPassword = false)
    {
        var dlg = new InputDialog(title, prompt, initialValue, isPassword) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.ResultText : null;
    }

    // --- Drag & Drop + Keyboard Shortcuts & Spacebar Panning ---
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

    private bool IsTypingInTextControl() =>
        TxtSearch.IsKeyboardFocused ||
        TxtPageJump.IsKeyboardFocused ||
        PdfViewport.IsEditingInlineField ||
        Keyboard.FocusedElement is TextBox or PasswordBox;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && !IsTypingInTextControl())
        {
            PdfViewport.SetSpacePanning(true);
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            PdfViewport.SetSpacePanning(false);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool typingInBox = IsTypingInTextControl();

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.S) { BtnSaveAs_Click(sender, e); e.Handled = true; }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            Key actual = e.Key == Key.System ? e.SystemKey : e.Key;
            if (actual == Key.Left && !typingInBox) { BtnNavBack_Click(sender, e); e.Handled = true; }
            else if (actual == Key.Right && !typingInBox) { BtnNavForward_Click(sender, e); e.Handled = true; }
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
        else if (e.Key == Key.Escape && _boundSession?.ActiveTool is AnnotationTool.SignStamp or AnnotationTool.AddFormField or AnnotationTool.AddCheckboxField or AnnotationTool.CropPage)
        {
            _pendingSignatureRequest = null;
            _boundSession.ActiveTool = AnnotationTool.None;
            SyncToolToggleButtons(AnnotationTool.None);
            StatusMessage.Text = "Herramienta cancelada";
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
            if (e.Key == Key.Delete && PdfViewport.DeleteSelectedCanvasItem())
            {
                StatusMessage.Text = "Elemento eliminado (Ctrl+Z para deshacer)";
                e.Handled = true;
            }
            else if (e.Key == Key.PageDown) { BtnNext_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.PageUp) { BtnPrev_Click(sender, e); e.Handled = true; }
        }
    }
}
