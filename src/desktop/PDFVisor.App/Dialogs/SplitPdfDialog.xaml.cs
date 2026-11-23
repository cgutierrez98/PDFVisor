using System.Windows;
using PDFVisor.Core;

namespace PDFVisor.App.Dialogs;

public partial class SplitPdfDialog : Window
{
    private readonly int _totalPages;
    public List<int[]>? SelectedGroups { get; private set; }

    public SplitPdfDialog(int totalPages)
    {
        InitializeComponent();
        _totalPages = totalPages;
        TxtHeader.Text = $"Documento con {totalPages} páginas";
        TxtRanges.Text = totalPages > 1 ? $"1-{totalPages / 2}, {(totalPages / 2) + 1}-{totalPages}" : "1";
    }

    private void BtnSplit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RbRanges.IsChecked == true)
            {
                SelectedGroups = PageRangeParser.ParseGroups(TxtRanges.Text, _totalPages);
            }
            else
            {
                if (!int.TryParse(TxtEveryN.Text.Trim(), out int n) || n <= 0)
                    throw new FormatException("Introduce un número válido mayor que 0.");
                SelectedGroups = PageRangeParser.SplitEveryN(_totalPages, n);
            }
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Rangos inválidos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
