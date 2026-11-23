using System.IO;
using System.Windows;
using PDFVisor.Interop;

namespace PDFVisor.App.Dialogs;

public partial class DocumentPropertiesDialog : Window
{
    public string MetaTitle => TxtTitle.Text.Trim();
    public string MetaAuthor => TxtAuthor.Text.Trim();
    public string MetaSubject => TxtSubject.Text.Trim();
    public string MetaKeywords => TxtKeywords.Text.Trim();

    public DocumentPropertiesDialog(string filePath, int pageCount, PdfMetadata meta)
    {
        InitializeComponent();
        TxtFileName.Text = Path.GetFileName(filePath);
        TxtPdfVersion.Text = $"PDF {meta.PdfVersion}";

        long bytes = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
        string sizeText = bytes >= 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):0.00} MB"
            : $"{Math.Max(1, bytes / 1024.0):0.0} KB";
        TxtPagesSize.Text = $"{pageCount} páginas  •  {sizeText}";

        string enc = meta.IsEncrypted ? "Cifrado" : "Sin cifrado";
        string lin = meta.IsLinearized ? "Linealizado (carga rápida)" : "Estándar";
        TxtSecurityState.Text = $"{enc}  •  {lin}";

        TxtTitle.Text = meta.Title;
        TxtAuthor.Text = meta.Author;
        TxtSubject.Text = meta.Subject;
        TxtKeywords.Text = meta.Keywords;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
