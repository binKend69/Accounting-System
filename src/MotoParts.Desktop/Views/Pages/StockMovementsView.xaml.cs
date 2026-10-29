using System.Windows.Controls;

namespace MotoParts.Desktop.Views.Pages;

public partial class StockMovementsView : UserControl
{
    public StockMovementsView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => ValueColumn.Visibility = DataContext is StockMovementsViewModel { SeesCost: true }
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }
}
