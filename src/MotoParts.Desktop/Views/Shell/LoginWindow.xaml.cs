using System.Windows;
using System.Windows.Input;
using MotoParts.Core.Common;
using MotoParts.Core.Security;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;

namespace MotoParts.Desktop.Views.Shell;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            using (var scope = AppSession.Current.NewScope())
            {
                ShopName.Text = scope.Settings.GetShopProfile().Name;
            }

            Username.Focus();
        };

        PreviewKeyDown += (_, _) => CapsHint.Visibility = Keyboard.IsKeyToggled(Key.CapsLock) ? Visibility.Visible : Visibility.Collapsed;
    }

    public event EventHandler? SignedIn;

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        var username = Username.Text.Trim();
        var password = Password.Password;

        if (username.Length == 0 || password.Length == 0)
        {
            ShowError("أدخل اسم المستخدم وكلمة المرور.");
            return;
        }

        SignInButton.IsEnabled = false;

        try
        {
            var (actor, mustChange) = await Task.Run(() =>
            {
                using var scope = AppSession.Current.NewScope();
                var signedIn = scope.Users.Authenticate(username, password);
                return (signedIn, signedIn is not null && scope.Users.Get(signedIn.Id).MustChangePassword);
            });

            if (actor is null)
            {
                Password.Clear();
                Password.Focus();
                ShowError("اسم المستخدم أو كلمة المرور غير صحيحة، أو أن الحساب موقوف.");
                return;
            }

            if (mustChange && !ChangePassword(actor, password))
            {
                return;
            }

            Log.Info($"Signed in: {actor.Username}");
            AppSession.Current.SignIn(actor);
            SignedIn?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Sign-in failed", ex);
            ShowError("تعذر الاتصال بقاعدة البيانات. أعد تشغيل البرنامج.");
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    /// <summary>An owner reset this password; the user must choose their own before going on.</summary>
    private static bool ChangePassword(Actor actor, string currentPassword)
    {
        while (true)
        {
            var newPassword = Dialogs.Prompt("تغيير كلمة المرور",
                $"أعاد المالك تعيين كلمة مرورك. اختر كلمة مرور جديدة ({UserService.MinimumPasswordLength} أحرف على الأقل):",
                multiline: false);

            if (newPassword is null)
            {
                return false;
            }

            try
            {
                using var scope = AppSession.Current.NewScope();
                scope.Users.ChangeOwnPassword(actor, currentPassword, newPassword);
                return true;
            }
            catch (BusinessRuleException ex)
            {
                Dialogs.Error(ex.Message);
            }
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.Visibility = Visibility.Visible;
    }
}
