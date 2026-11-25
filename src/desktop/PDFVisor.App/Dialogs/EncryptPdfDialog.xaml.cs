using System.Windows;

namespace PDFVisor.App.Dialogs;

public partial class EncryptPdfDialog : Window
{
    public string UserPassword => PwdUser.Password;
    public string OwnerPassword => string.IsNullOrWhiteSpace(PwdOwner.Password) ? PwdUser.Password : PwdOwner.Password;
    public bool AllowPrint => ChkAllowPrint.IsChecked == true;
    public bool AllowExtract => ChkAllowExtract.IsChecked == true;
    public bool AllowModify => ChkAllowModify.IsChecked == true;

    public EncryptPdfDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PwdUser.Focus();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PwdUser.Password) && string.IsNullOrWhiteSpace(PwdOwner.Password))
        {
            MessageBox.Show(this, "Debes introducir al menos una contraseña.", "Contraseña requerida",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }
}
