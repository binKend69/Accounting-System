using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace MotoParts.Desktop.Views.Pages;

/// <summary>
/// The till is driven from the keyboard: a barcode scanner types into the
/// scanner box and presses Enter, and the function keys do the rest.
/// </summary>
public partial class PosView : UserControl
{
    public PosView()
    {
        InitializeComponent();

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is PosViewModel old)
            {
                old.FocusScannerRequested -= FocusScanner;
                old.FocusTenderedRequested -= FocusTendered;
            }

            if (e.NewValue is PosViewModel vm)
            {
                vm.FocusScannerRequested += FocusScanner;
                vm.FocusTenderedRequested += FocusTendered;
            }
        };

        Loaded += (_, _) => FocusScanner(this, EventArgs.Empty);
        PreviewKeyDown += OnPreviewKeyDown;
        Scanner.PreviewKeyDown += OnScannerKeyDown;
    }

    private PosViewModel? ViewModel => DataContext as PosViewModel;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F12:
                ViewModel.CompleteSaleCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F4:
                FocusTendered(this, EventArgs.Empty);
                e.Handled = true;
                break;
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Control:
                ViewModel.CompleteSaleCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnScannerKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                ViewModel.AddByCodeCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                ViewModel.ClearSearchCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down when ViewModel.HasSearchResults:
                Results.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            ViewModel.PickResultCommand.Execute(ViewModel.SelectedResult);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) =>
        ViewModel?.PickResultCommand.Execute(ViewModel.SelectedResult);

    private void OnCartKeyDown(object sender, KeyEventArgs e)
    {
        // Delete removes the selected line - unless a cell is being edited.
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox && ViewModel is not null)
        {
            ViewModel.RemoveLineCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnClearCustomer(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.Customer = null;
        }
    }

    private void FocusScanner(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Scanner.Focus();
            Keyboard.Focus(Scanner);
        });

    private void FocusTendered(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Tendered.IsVisible)
            {
                Tendered.Focus();
                Tendered.SelectAll();
            }
        });
}

/// <summary>True when the two bound values are equal; used to check the selected payment method.</summary>
public sealed class EqualityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length == 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
