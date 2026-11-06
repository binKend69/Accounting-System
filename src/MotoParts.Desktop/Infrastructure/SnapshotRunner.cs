using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Seeding;
using MotoParts.Desktop.Views.Shell;

namespace MotoParts.Desktop.Infrastructure;

/// <summary>
/// "MotoParts.exe --snapshot &lt;folder&gt;": renders every screen to a PNG, against
/// a throwaway database filled with demo data, without touching the real one.
/// Used to check layouts and to produce the screenshots in the user guide.
/// Windows are placed off-screen, so nothing flashes on the desktop.
/// </summary>
public static class SnapshotRunner
{
    private const int Width = 1366;
    private const int Height = 768;

    public static async Task RunAsync(string outputFolder, string? only)
    {
        Directory.CreateDirectory(outputFolder);
        var data = Path.Combine(outputFolder, "data");

        if (Directory.Exists(data))
        {
            Directory.Delete(data, recursive: true);
        }

        var config = new AppConfig
        {
            DataDirectory = data,
            BackupDirectory = Path.Combine(data, "backups"),
            LogDirectory = Path.Combine(data, "logs"),
        };

        Log.Initialise(config.LogDirectory);
        AppSession.Current = new AppSession(config, new SystemClock());
        new MigrationRunner(AppSession.Current.Database).Migrate();

        // First-run setup, before any user exists.
        if (Wanted(only, "setup"))
        {
            await Render(new SetupWindow(), Path.Combine(outputFolder, "00-setup.png"));
        }

        using (var scope = AppSession.Current.NewScope())
        {
            scope.Seeder.Seed();
            var owner = scope.Users.CreateFirstOwner("owner", "عبدالله المالك", "owner123");
            var actor = scope.Users.LoadActor(owner.Id);
            scope.Settings.SaveShopProfile(new Core.Services.ShopProfile("مؤسسة الدراجة السريعة لقطع الغيار", "الرياض - حي العزيزية",
                "0112345678", "300123456700003", "1010123456", "ر.س", null), actor);
            new DemoDataSeeder(scope).Seed(actor);
        }

        // One printed document too, so a published build proves its PDF engine loads.
        if (Wanted(only, "invoice"))
        {
            using var scope = AppSession.Current.NewScope();
            var saleId = scope.Sales.Sales(scope.Users.LoadActor(scope.Users.All().First(u => u.Username == "owner").Id), new Core.Services.SaleFilter()).First().Id;
            QuestPDF.Fluent.GenerateExtensions.GeneratePdf(new Documents.DocumentFactory(scope).Invoice(saleId), Path.Combine(outputFolder, "invoice.pdf"));
        }

        if (Wanted(only, "login"))
        {
            await Render(new LoginWindow(), Path.Combine(outputFolder, "01-login.png"));
        }

        foreach (var username in new[] { "owner", "sales" })
        {
            using (var scope = AppSession.Current.NewScope())
            {
                AppSession.Current.SignIn(scope.Users.Authenticate(username, username == "owner" ? "owner123" : DemoDataSeeder.DemoPassword)!);
            }

            var window = new MainWindow();
            var viewModel = (MainViewModel)window.DataContext;
            PlaceOffScreen(window);
            window.Show();
            await Idle();

            var index = 0;

            foreach (var item in viewModel.AllItems)
            {
                index++;

                if (username == "sales" && index > 3)
                {
                    break;
                }

                var name = $"{(username == "owner" ? "" : "sales-")}{index + 1:00}-{item.Page.GetType().Name.Replace("ViewModel", "")}";

                if (!Wanted(only, name))
                {
                    continue;
                }

                await viewModel.NavigateAsync(item);
                await Idle();
                await Task.Delay(400);
                await Idle();
                Capture(window, Path.Combine(outputFolder, $"{name}.png"));
            }

            if (username == "owner")
            {
                await RenderForms(outputFolder, only);
            }

            window.Closing += (_, e) => e.Cancel = false;
            window.Hide();
            AppSession.Current.SignOut();
        }
    }

    /// <summary>The data-entry dialogs, filled from the demo data.</summary>
    private static async Task RenderForms(string outputFolder, string? only)
    {
        long saleId;
        long customerId;
        long productId;
        long entryId;
        Core.Services.AttendanceRow attendance;

        using (var scope = AppSession.Current.NewScope())
        {
            saleId = scope.Sales.Sales(AppSession.Current.RequireUser, new Core.Services.SaleFilter()).First(s => s.ItemCount > 1).Id;
            customerId = scope.Parties.Customers(withBalanceOnly: true).First().Id;
            productId = scope.Catalog.Products().First().Id;
            entryId = scope.Chart.Entries(null, null, null).First().Id;
            attendance = scope.Shifts.Attendance(null, null, null).First();
        }

        var forms = new (string Name, Func<FormViewModel> Create)[]
        {
            ("form-customer", () => new Views.Forms.CustomerForm(customerId)),
            ("form-refund", () => new Views.Forms.RefundForm(saleId)),
            ("form-sale-details", () => new Views.Forms.SaleDetailsForm(saleId)),
            ("form-receipt", () => new Views.Forms.CustomerReceiptForm(customerId)),
            ("form-expense", () => new Views.Forms.ExpenseForm()),
            ("form-supplier-payment", () => new Views.Forms.SupplierPaymentForm(null)),
            ("form-product", () => new Views.Forms.ProductForm(productId)),
            ("form-stock-adjust", () => new Views.Forms.StockAdjustForm(productId)),
            ("form-movements", () => new Views.Forms.ProductMovementsForm(productId)),
            ("form-purchase", () => new Views.Forms.PurchaseForm(null)),
            ("form-entry", () => new Views.Forms.EntryDetailsForm(entryId)),
            ("form-manual-entry", () => new Views.Forms.ManualEntryForm()),
            ("form-account", () => new Views.Forms.AccountForm(null)),
            ("form-user", () => new Views.Forms.UserForm(null)),
            ("form-attendance", () => new Views.Forms.AttendanceForm(attendance)),
        };

        foreach (var (name, create) in forms)
        {
            if (!Wanted(only, name))
            {
                continue;
            }

            var window = new Views.Popups.FormWindow(create());
            PlaceOffScreen(window);
            window.Show();
            await Idle();
            await Task.Delay(500);
            await Idle();
            Capture(window, Path.Combine(outputFolder, $"{name}.png"));
            window.Hide();
        }

        _ = productId;
    }

    private static bool Wanted(string? only, string name) =>
        string.IsNullOrEmpty(only) || name.Contains(only, StringComparison.OrdinalIgnoreCase);

    private static async Task Render(Window window, string path)
    {
        PlaceOffScreen(window);
        window.Show();
        await Idle();
        await Task.Delay(200);
        Capture(window, path);
        window.Hide();
    }

    private static void PlaceOffScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.WindowState = WindowState.Normal;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = -30000;
        window.Top = 0;

        if (window.SizeToContent == SizeToContent.Manual)
        {
            window.Width = Math.Max(window.Width, window is MainWindow ? Width : window.Width);
            window.Height = Math.Max(window.Height, window is MainWindow ? Height : window.Height);
        }
    }

    private static void Capture(Window window, string path)
    {
        if (window.Content is not FrameworkElement root)
        {
            return;
        }

        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            // The root grid is transparent; paint the window background first.
            context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));

            // WPF draws right-to-left content mirrored and the window flips it
            // back on screen. A capture taken below the window must apply that
            // final flip itself, or text and layout come out reversed.
            if (window.FlowDirection == FlowDirection.RightToLeft)
            {
                context.PushTransform(new ScaleTransform(-1, 1, width / 2.0, 0));
            }

            context.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static Task Idle() =>
        Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;
}
