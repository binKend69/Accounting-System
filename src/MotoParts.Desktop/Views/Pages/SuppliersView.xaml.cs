using System.Windows.Controls;
using System.Windows.Input;

namespace MotoParts.Desktop.Views.Pages;

public partial class SuppliersView : UserControl
{
    public SuppliersView()
    {
        InitializeComponent();
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SuppliersViewModel vm && vm.EditCommand.CanExecute(null))
        {
            vm.EditCommand.Execute(null);
        }
    }
}
