using System.Windows.Controls;
using System.Windows.Input;

namespace MotoParts.Desktop.Views.Pages;

public partial class PurchasesView : UserControl
{
    public PurchasesView()
    {
        InitializeComponent();
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PurchasesViewModel vm && vm.OpenCommand.CanExecute(null))
        {
            vm.OpenCommand.Execute(null);
        }
    }
}
