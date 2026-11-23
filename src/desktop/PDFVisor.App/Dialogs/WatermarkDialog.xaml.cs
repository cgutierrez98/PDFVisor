using System.Windows;

namespace PDFVisor.App.Dialogs;

public partial class WatermarkDialog : Window
{
    public bool ApplyWatermark => ChkWatermark.IsChecked == true && !string.IsNullOrWhiteSpace(TxtWatermark.Text);
    public string WatermarkText => TxtWatermark.Text.Trim();
    public byte WatermarkOpacity => (byte)Math.Clamp((int)SldOpacity.Value, 10, 220);

    public bool ApplyPageNumbers => ChkPageNumbers.IsChecked == true && !string.IsNullOrWhiteSpace(TxtPageNumFormat.Text);
    public string PageNumberFormat => TxtPageNumFormat.Text.Trim();

    public WatermarkDialog()
    {
        InitializeComponent();
    }

    private void SldOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtOpacityPct is not null)
        {
            TxtOpacityPct.Text = $"{Math.Round(e.NewValue * 100.0 / 255.0)}%";
        }
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
