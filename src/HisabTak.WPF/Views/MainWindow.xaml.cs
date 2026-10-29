using System.Windows;
using HisabTak.WPF.ViewModels;

namespace HisabTak.WPF.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
