using System.Windows;
using System.Windows.Input;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Desktop.Views.Pages;

namespace MotoParts.Desktop.Views.Shell;

public partial class MainWindow : Window
{
    private bool _signingOut;

    public MainWindow()
    {
        InitializeComponent();

        var viewModel = new MainViewModel();
        DataContext = viewModel;

        Loaded += async (_, _) =>
        {
            // Cashiers land on the till; everyone else on the dashboard.
            var start = viewModel.AllItems.FirstOrDefault(i => i.Page is PosViewModel)
                ?? viewModel.AllItems.First();

            if (!AppSession.Current.RequireUser.IsOwner && start.Page is PosViewModel)
            {
                await viewModel.NavigateAsync(start);
            }
            else
            {
                await viewModel.NavigateAsync(viewModel.AllItems.First());
            }
        };

        // F2 jumps to the till from anywhere.
        InputBindings.Add(new KeyBinding(new AsyncKeyCommand(() => viewModel.NavigateToAsync<PosViewModel>()), Key.F2, ModifierKeys.None));

        Closing += (_, e) =>
        {
            if (_signingOut)
            {
                return;
            }

            if (!Dialogs.Confirm("هل تريد إغلاق البرنامج؟", "إغلاق", "إغلاق البرنامج", "بقاء"))
            {
                e.Cancel = true;
            }
        };

        Closed += (_, _) =>
        {
            if (!_signingOut)
            {
                Application.Current.Shutdown();
            }
        };
    }

    public event EventHandler? SignedOut;

    private void OnSignOut(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm("تسجيل الخروج من البرنامج؟", "تسجيل الخروج", "تسجيل الخروج", "إلغاء"))
        {
            return;
        }

        Log.Info($"Signed out: {AppSession.Current.User?.Username}");
        _signingOut = true;
        AppSession.Current.SignOut();
        SignedOut?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private sealed class AsyncKeyCommand(Func<Task> run) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public async void Execute(object? parameter) => await run();
    }
}
