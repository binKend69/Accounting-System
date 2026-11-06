using System.Windows;
using System.Windows.Input;
using MotoParts.Desktop.Infrastructure;

namespace MotoParts.Desktop.Views.Popups;

/// <summary>The frame every data-entry form is shown in.</summary>
public partial class FormWindow : Window
{
    private readonly FormViewModel _form;

    public FormWindow(FormViewModel form)
    {
        InitializeComponent();
        _form = form;
        DataContext = form;
        Width = form.Width;

        if (!form.CanSave)
        {
            CancelButton.Content = "إغلاق";
        }

        Loaded += async (_, _) =>
        {
            await form.LoadAsync();
            MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        };

        // Ctrl+S saves from anywhere in the form.
        InputBindings.Add(new KeyBinding(new SaveCommand(this), Key.S, ModifierKeys.Control));
    }

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        if (!_form.CanSave || _form.IsBusy)
        {
            return;
        }

        SaveButton.IsEnabled = false;

        try
        {
            if (await _form.SaveAsync())
            {
                DialogResult = true;
            }
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private sealed class SaveCommand(FormWindow window) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public async void Execute(object? parameter) => await window.SaveAsync();
    }
}
