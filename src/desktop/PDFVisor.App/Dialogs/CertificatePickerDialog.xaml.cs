using System.Security.Cryptography.X509Certificates;
using System.Windows;

namespace PDFVisor.App.Dialogs;

public partial class CertificatePickerDialog : Window
{
    private sealed record CertDisplayItem(X509Certificate2 Certificate, string DisplayName, string Details);

    public X509Certificate2? SelectedCertificate { get; private set; }
    public string Reason => TxtReason.Text.Trim();
    public string? TsaUrl => string.IsNullOrWhiteSpace(TxtTsaUrl.Text) ? null : TxtTsaUrl.Text.Trim();

    public CertificatePickerDialog(IReadOnlyList<X509Certificate2> certificates)
    {
        InitializeComponent();
        var items = certificates.Select(c =>
        {
            string name = c.GetNameInfo(X509NameType.SimpleName, false);
            if (string.IsNullOrWhiteSpace(name)) name = c.Subject;
            return new CertDisplayItem(c, name, $"Emisor: {c.Issuer}  •  Válido hasta {c.NotAfter:dd/MM/yyyy}");
        }).ToList();

        CertList.ItemsSource = items;
        if (items.Count > 0) CertList.SelectedIndex = 0;
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
