using System.Windows;
using System.Windows.Controls;

namespace MotoParts.Desktop.Views.Forms;

public partial class RefundFormView : UserControl
{
    public RefundFormView()
    {
        InitializeComponent();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => (DataContext as RefundForm)?.SelectAll();
}
