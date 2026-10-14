using System.Windows.Controls;
using System.Windows.Input;

namespace MotoParts.Desktop.Views.Pages;

public partial class SalesView : UserControl
{
    public SalesView()
    {
        InitializeComponent();
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SalesViewModel vm && vm.ViewDetailsCommand.CanExecute(null))
        {
            vm.ViewDetailsCommand.Execute(null);
        }
    }
}
