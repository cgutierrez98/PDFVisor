using System.Collections.ObjectModel;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PDFVisor.Core;

namespace PDFVisor.App.Dialogs;

public partial class CertificatePickerDialog : Window
{
    private sealed record CertDisplayItem(X509Certificate2 Certificate, string DisplayName, string Details);

    private readonly ObservableCollection<CertDisplayItem> _items = [];

    public X509Certificate2? SelectedCertificate { get; private set; }
    public string Reason => TxtReason.Text.Trim();
    public string? Location => string.IsNullOrWhiteSpace(TxtLocation.Text) ? null : TxtLocation.Text.Trim();
    public string? TsaUrl => string.IsNullOrWhiteSpace(TxtTsaUrl.Text) ? null : TxtTsaUrl.Text.Trim();
    public bool DrawPositionOnPage => ChkDrawOnPage.IsChecked == true;
    public (byte[] BgraPixels, int WidthPx, int HeightPx)? StampImage { get; private set; }

    public CertificatePickerDialog(IReadOnlyList<X509Certificate2> certificates)
    {
        InitializeComponent();
        foreach (var c in certificates)
        {
            _items.Add(CreateItem(c));
        }

        CertList.ItemsSource = _items;
        if (_items.Count > 0) CertList.SelectedIndex = 0;
        BtnSign.IsEnabled = _items.Count > 0;
    }

    private static CertDisplayItem CreateItem(X509Certificate2 c, string? sourceTag = null)
    {
        string name = c.GetNameInfo(X509NameType.SimpleName, false);
        if (string.IsNullOrWhiteSpace(name)) name = c.Subject;
        string prefix = sourceTag is not null ? $"[{sourceTag}] " : string.Empty;
        return new CertDisplayItem(c, prefix + name, $"Emisor: {c.Issuer}  •  Válido hasta {c.NotAfter:dd/MM/yyyy}");
    }

    private void CertList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BtnSign.IsEnabled = CertList.SelectedItem is CertDisplayItem;
    }

    private void BtnLoadPfx_Click(object sender, RoutedEventArgs e)
    {
        var openDlg = new OpenFileDialog
        {
            Title = "Seleccionar certificado digital PKCS#12",
            Filter = "Certificados PKCS#12 (*.pfx;*.p12)|*.pfx;*.p12|Todos los archivos (*.*)|*.*"
        };
        if (openDlg.ShowDialog(this) != true) return;

        var pwdDlg = new InputDialog(
            "Contraseña del certificado",
            $"Introduce la contraseña de \"{System.IO.Path.GetFileName(openDlg.FileName)}\":",
            isPassword: true)
        {
            Owner = this
        };
        if (pwdDlg.ShowDialog() != true) return;

        try
        {
            var cert = PadesSignatureService.LoadCertificateFromPfx(openDlg.FileName, pwdDlg.ResultText);
            var item = CreateItem(cert, "PFX");
            _items.Insert(0, item);
            CertList.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo cargar el certificado PFX/P12:\n{ex.Message}", "Error de certificado",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnSelectStampImage_Click(object sender, RoutedEventArgs e)
    {
        var openDlg = new OpenFileDialog
        {
            Title = "Seleccionar imagen de rúbrica o sello",
            Filter = "Imágenes (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (openDlg.ShowDialog(this) != true) return;

        try
        {
            var bmp = new BitmapImage(new Uri(openDlg.FileName));
            var formatted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = formatted.PixelWidth;
            int h = formatted.PixelHeight;
            byte[] bgra = new byte[w * h * 4];
            formatted.CopyPixels(bgra, w * 4, 0);

            // Composite semi-transparent pixels over crisp white paper so PDF /DeviceRGB stamp renders cleanly
            for (int i = 0; i < bgra.Length; i += 4)
            {
                int a = bgra[i + 3];
                bgra[i] = (byte)((bgra[i] * a + 255 * (255 - a)) / 255);
                bgra[i + 1] = (byte)((bgra[i + 1] * a + 255 * (255 - a)) / 255);
                bgra[i + 2] = (byte)((bgra[i + 2] * a + 255 * (255 - a)) / 255);
                bgra[i + 3] = 255;
            }

            StampImage = (bgra, w, h);
            TxtStampImageStatus.Text = $"Rúbrica: {System.IO.Path.GetFileName(openDlg.FileName)} ({w}×{h} px)";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"No se pudo cargar la imagen:\n{ex.Message}", "Error de imagen",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnSign_Click(object sender, RoutedEventArgs e)
    {
        if (CertList.SelectedItem is CertDisplayItem item)
        {
            SelectedCertificate = item.Certificate;
            DialogResult = true;
            Close();
        }
    }
}
