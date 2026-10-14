using System.Globalization;
using System.Windows;
using MotoParts.Core.Common;
using MotoParts.Core.Seeding;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;

namespace MotoParts.Desktop.Views.Shell;

/// <summary>
/// First run: the shop's details, its tax configuration and the owner account.
/// There is no default password - the owner chooses one here.
/// </summary>
public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ShopName.Focus();
    }

    public bool Completed { get; private set; }

    private async void OnFinish(object sender, RoutedEventArgs e)
    {
        ErrorBox.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(ShopName.Text) || string.IsNullOrWhiteSpace(OwnerName.Text) || string.IsNullOrWhiteSpace(OwnerUsername.Text))
        {
            ShowError("أكمل الحقول المطلوبة (*).");
            return;
        }

        if (OwnerPassword.Password != OwnerPasswordConfirm.Password)
        {
            ShowError("كلمتا المرور غير متطابقتين.");
            return;
        }

        if (!decimal.TryParse(TaxPercent.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var percent) || percent is < 0 or > 100)
        {
            ShowError("نسبة الضريبة يجب أن تكون رقماً بين 0 و 100.");
            return;
        }

        var shop = new ShopProfile(ShopName.Text.Trim(), Address.Text, Phone.Text, TaxNumber.Text, CommercialRegister.Text, "ر.س", null);
        var ownerName = OwnerName.Text.Trim();
        var username = OwnerUsername.Text.Trim();
        var password = OwnerPassword.Password;
        var includeTax = PricesIncludeTax.IsChecked == true;
        var demo = LoadDemo.IsChecked == true;

        FinishButton.IsEnabled = false;

        try
        {
            await Task.Run(() =>
            {
                using var scope = AppSession.Current.NewScope();

                scope.Db.InTransaction(() =>
                {
                    var owner = scope.Users.CreateFirstOwner(username, ownerName, password);
                    var actor = scope.Users.LoadActor(owner.Id);

                    var footer = scope.Settings.GetShopProfile().InvoiceFooter;
                    scope.Settings.SaveShopProfile(shop with { InvoiceFooter = footer }, actor);
                    scope.Settings.SetTaxRate(percent / 100m, actor);
                    scope.Settings.SetPricesIncludeTax(includeTax, actor);
                    scope.Settings.Set(SettingsService.SetupCompleted, "1", actor);

                    if (demo)
                    {
                        new DemoDataSeeder(scope).Seed(actor);
                    }
                });
            });

            Log.Info($"First-run setup completed (demo data: {demo}).");
            Completed = true;
            Close();
        }
        catch (BusinessRuleException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("First-run setup failed", ex);
            ShowError("تعذر حفظ الإعداد. راجع ملف السجل.");
        }
        finally
        {
            FinishButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.Visibility = Visibility.Visible;
    }
}
