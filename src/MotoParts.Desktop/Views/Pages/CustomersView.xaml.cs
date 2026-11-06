using System.Windows.Controls;
using System.Windows.Input;

namespace MotoParts.Desktop.Views.Pages;

public partial class CustomersView : UserControl
{
    public CustomersView()
    {
        InitializeComponent();
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is CustomersViewModel vm && vm.EditCommand.CanExecute(null))
        {
            vm.EditCommand.Execute(null);
        }
    }
}
