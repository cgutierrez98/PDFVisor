using System.Windows;

namespace PDFVisor.App.Dialogs;

public partial class InputDialog : Window
{
    private readonly bool _isPassword;

    public string ResultText => _isPassword ? PwdValue.Password : TxtValue.Text;

    public InputDialog(string title, string prompt, string initialValue = "", bool isPassword = false)
    {
        InitializeComponent();
        Title = title;
        TxtPrompt.Text = prompt;
        _isPassword = isPassword;

        if (_isPassword)
        {
            TxtValue.Visibility = Visibility.Collapsed;
            PwdValue.Visibility = Visibility.Visible;
            Loaded += (_, _) => PwdValue.Focus();
        }
        else
        {
            TxtValue.Text = initialValue;
            Loaded += (_, _) =>
            {
                TxtValue.Focus();
                TxtValue.SelectAll();
            };
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
