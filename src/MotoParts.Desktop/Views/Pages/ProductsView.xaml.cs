using System.Windows.Controls;
using System.Windows.Input;

namespace MotoParts.Desktop.Views.Pages;

public partial class ProductsView : UserControl
{
    public ProductsView()
    {
        InitializeComponent();

        // Cost is for people who manage stock or keep the books, not the till.
        DataContextChanged += (_, _) =>
        {
            var visibility = DataContext is ProductsViewModel { SeesCost: true }
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            CostColumn.Visibility = visibility;
            ValueColumn.Visibility = visibility;
        };
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ProductsViewModel vm && vm.EditCommand.CanExecute(null))
        {
            vm.EditCommand.Execute(null);
        }
    }
}
