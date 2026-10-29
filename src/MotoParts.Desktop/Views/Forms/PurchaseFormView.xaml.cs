using System.Windows.Controls;
using System.Windows.Input;

namespace MotoParts.Desktop.Views.Forms;

public partial class PurchaseFormView : UserControl
{
    public PurchaseFormView()
    {
        InitializeComponent();
    }

    private PurchaseForm? Form => DataContext as PurchaseForm;

    private void OnProductKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Form is not null)
        {
            Form.FindProductCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => Form?.PickResultCommand.Execute(null);
}
