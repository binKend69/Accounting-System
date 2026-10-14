using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using QuestPDF.Infrastructure;
using MotoParts.Core.Common;
using MotoParts.Core.Security;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Documents;

namespace MotoParts.Desktop.Views.Pages;

/// <summary>Shop details, tax, backups and the signed-in user's password.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    public override string Title => "الإعدادات والنسخ الاحتياطي";

    public ObservableCollection<BackupFile> Backups { get; } = [];

    public ObservableCollection<IntegrityCheck> Checks { get; } = [];

    [ObservableProperty] private string _shopName = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _taxNumber = "";
    [ObservableProperty] private string _commercialRegister = "";
    [ObservableProperty] private string _currencySymbol = "ر.س";
    [ObservableProperty] private string _invoiceFooter = "";
    [ObservableProperty] private string _taxPercentText = "15";
    [ObservableProperty] private bool _pricesIncludeTax;
    [ObservableProperty] private string _backupFolder = "";
    [ObservableProperty] private string _keepCountText = "30";
    [ObservableProperty] private BackupFile? _selectedBackup;

    public bool CanManageSettings => Can(Permission.SettingsManage);

    public bool CanManageBackups => Can(Permission.BackupManage);

    public string DatabasePath => Session.Config.DatabasePath;

    public string LogPath => Session.Config.LogDirectory;

    public override async Task LoadAsync()
    {
        var folder = App.BackupFolder();

        var result = await TryRunAsync(scope =>
            (scope.Settings.GetShopProfile(), scope.Settings.GetTaxRate(), scope.Settings.GetPricesIncludeTax(),
             scope.Settings.Get(SettingsService.BackupKeepCount, "30"), scope.Backups.List(folder)));

        if (!result.Ok)
        {
            return;
        }

        var (shop, rate, inclusive, keep, backups) = result.Value;
        ShopName = shop.Name;
        Address = shop.Address ?? "";
        Phone = shop.Phone ?? "";
        TaxNumber = shop.TaxNumber ?? "";
        CommercialRegister = shop.CommercialRegister ?? "";
        CurrencySymbol = shop.CurrencySymbol;
        InvoiceFooter = shop.InvoiceFooter ?? "";
        TaxPercentText = (rate * 100m).ToString("0.##", CultureInfo.InvariantCulture);
        PricesIncludeTax = inclusive;
        BackupFolder = folder;
        KeepCountText = keep;

        Backups.Clear();

        foreach (var backup in backups)
        {
            Backups.Add(backup);
        }
    }

    [RelayCommand]
    private async Task SaveShop()
    {
        if (!decimal.TryParse(TaxPercentText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var percent) || percent is < 0 or > 100)
        {
            ErrorMessage = "نسبة الضريبة يجب أن تكون بين 0 و 100.";
            return;
        }

        var profile = new ShopProfile(ShopName, Address, Phone, TaxNumber, CommercialRegister, CurrencySymbol, InvoiceFooter);
        var (actor, inclusive) = (User, PricesIncludeTax);

        await RunAsync(scope => scope.Db.InTransaction(() =>
        {
            scope.Settings.SaveShopProfile(profile, actor);
            scope.Settings.SetTaxRate(percent / 100m, actor);
            scope.Settings.SetPricesIncludeTax(inclusive, actor);
        }), "حُفظت الإعدادات. تُطبَّق على الفواتير الجديدة فقط؛ الفواتير السابقة تحتفظ بنسبتها.");
    }

    [RelayCommand]
    private void BrowseBackupFolder()
    {
        var dialog = new OpenFolderDialog { Title = "اختر مجلد النسخ الاحتياطية", InitialDirectory = BackupFolder };

        if (dialog.ShowDialog() == true)
        {
            BackupFolder = dialog.FolderName;
        }
    }

    [RelayCommand]
    private async Task SaveBackupSettings()
    {
        if (!int.TryParse(KeepCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var keep) || keep < 3)
        {
            ErrorMessage = "عدد النسخ المحفوظة يجب أن يكون 3 أو أكثر.";
            return;
        }

        var (actor, folder) = (User, BackupFolder.Trim());

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorMessage = "تعذر استخدام هذا المجلد: " + ex.Message;
            return;
        }

        if (await RunAsync(scope =>
            {
                actor.Require(Permission.BackupManage);
                scope.Settings.Set(SettingsService.BackupFolder, folder, actor);
                scope.Settings.Set(SettingsService.BackupKeepCount, keep.ToString(CultureInfo.InvariantCulture), actor);
            }))
        {
            await LoadAsync();
            SuccessMessage = "حُفظت إعدادات النسخ الاحتياطي.";
        }
    }

    [RelayCommand]
    private async Task BackupNow()
    {
        var actor = User;
        var folder = App.BackupFolder();

        var path = await RunAsync(scope =>
        {
            actor.Require(Permission.BackupManage);
            var file = scope.Backups.Create(folder, "manual");
            scope.Audit.Record("Backup", null, Core.Domain.AuditAction.BackupCreated, actor, newValues: new { file });
            return file;
        });

        if (path is not null)
        {
            await LoadAsync();
            SuccessMessage = $"أُنشئت نسخة احتياطية: {Path.GetFileName(path)}";
        }
    }

    [RelayCommand]
    private void Restore()
    {
        string? path = SelectedBackup?.Path;

        if (path is null)
        {
            var dialog = new OpenFileDialog { Filter = "نسخة احتياطية|motoparts-*.db|كل الملفات|*.*", InitialDirectory = BackupFolder };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            path = dialog.FileName;
        }

        var problem = BackupService.Validate(path);

        if (problem is not null)
        {
            Dialogs.Error(problem);
            return;
        }

        if (!Dialogs.Confirm(
                $"استعادة النسخة:\n{Path.GetFileName(path)}\n\nستُستبدل كل البيانات الحالية بمحتوى هذه النسخة. تُحفظ نسخة من البيانات الحالية أولاً، ثم يُعاد تشغيل البرنامج.",
                "استعادة نسخة احتياطية", "استعادة وإعادة التشغيل", "إلغاء", danger: true))
        {
            return;
        }

        try
        {
            using (var scope = Session.NewScope())
            {
                scope.Audit.Record("Backup", null, Core.Domain.AuditAction.BackupRestored, User, newValues: new { file = Path.GetFileName(path) });
            }

            using (var scope = Session.NewScope())
            {
                scope.Backups.Restore(path, App.BackupFolder());
            }

            Log.Info($"Database restored from {path}");
            App.Restart();
        }
        catch (Exception ex)
        {
            Log.Error("Restore failed", ex);
            Dialogs.Error("تعذرت الاستعادة: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task RunChecks()
    {
        var checks = await RunAsync(scope => scope.Reports.IntegrityChecks());

        Checks.Clear();

        foreach (var check in checks ?? [])
        {
            Checks.Add(check);
        }

        if (checks is not null)
        {
            SuccessMessage = checks.All(c => c.Passed) ? "جميع فحوصات السلامة سليمة." : null;
            ErrorMessage = checks.All(c => c.Passed) ? null : "يوجد خلل في فحص أو أكثر. راجع الجدول وتواصل مع الدعم الفني.";
        }
    }

    [RelayCommand]
    private async Task ChangePassword()
    {
        var current = Dialogs.Prompt("تغيير كلمة المرور", "كلمة المرور الحالية:", multiline: false);

        if (current is null)
        {
            return;
        }

        var next = Dialogs.Prompt("تغيير كلمة المرور", $"كلمة المرور الجديدة ({UserService.MinimumPasswordLength} أحرف على الأقل):", multiline: false);

        if (next is null)
        {
            return;
        }

        var actor = User;
        await RunAsync(scope => scope.Users.ChangeOwnPassword(actor, current, next), "غُيّرت كلمة المرور.");
    }
}

/// <summary>A report in the reports centre.</summary>
public sealed record ReportCard(string Key, string Title, string Description, bool NeedsRange, bool NeedsAccount = false,
    bool NeedsEmployee = false, bool NeedsCustomer = false, bool NeedsSupplier = false);

public sealed partial class ReportsViewModel : ListPageViewModel
{
    public override string Title => "التقارير";

    public ObservableCollection<ReportCard> Reports { get; } = [];

    public ObservableCollection<FilterOption<long?>> Accounts { get; } = [];

    public ObservableCollection<FilterOption<long?>> Employees { get; } = [];

    public ObservableCollection<FilterOption<long?>> Customers { get; } = [];

    public ObservableCollection<FilterOption<long?>> Suppliers { get; } = [];

    [ObservableProperty] private ReportCard? _selected;
    [ObservableProperty] private FilterOption<long?>? _account;
    [ObservableProperty] private FilterOption<long?>? _employee;
    [ObservableProperty] private FilterOption<long?>? _customer;
    [ObservableProperty] private FilterOption<long?>? _supplier;

    protected override async Task RefreshAsync()
    {
        if (Reports.Count > 0)
        {
            return;
        }

        var user = User;
        bool accounting = user.Can(Permission.AccountingView), all = user.Can(Permission.ReportViewAll);
        bool own = user.Can(Permission.ReportViewOwn), stock = user.Can(Permission.InventoryView);

        void Add(bool allowed, ReportCard card)
        {
            if (allowed)
            {
                Reports.Add(card);
            }
        }

        Add(all || own, new("cash", "مطابقة النقدية للورديات", "المتوقع والمعدود والفرق لكل وردية.", true, NeedsEmployee: all));
        Add(all, new("sales-product", "المبيعات حسب الصنف", "الكميات والقيمة والتكلفة والربح لكل صنف بعد المرتجعات.", true));
        Add(all, new("sales-employee", "المبيعات حسب الموظف", "الفواتير والمرتجعات والربح وفروقات الصندوق لكل موظف.", true));
        Add(accounting, new("statements", "قائمة الدخل والمركز المالي", "الإيرادات والمصروفات وصافي الربح، والأصول والخصوم وحقوق الملكية.", true));
        Add(accounting, new("trial", "ميزان المراجعة", "أرصدة كل الحسابات، والتحقق من توازن الدفاتر.", true));
        Add(accounting, new("ledger", "دفتر الأستاذ", "حركات حساب واحد برصيد جارٍ.", true, NeedsAccount: true));
        Add(accounting, new("vat", "ملخص ضريبة القيمة المضافة", "ضريبة المبيعات والمشتريات وصافي المستحق لإقرار الضريبة.", true));
        Add(user.Can(Permission.CustomerManage), new("customer", "كشف حساب عميل", "الفواتير الآجلة والمرتجعات والسندات برصيد جارٍ.", true, NeedsCustomer: true));
        Add(user.Can(Permission.PurchaseView), new("supplier", "كشف حساب مورد", "المشتريات الآجلة والمدفوعات برصيد جارٍ.", true, NeedsSupplier: true));
        Add(stock, new("low-stock", "قائمة إعادة الطلب", "الأصناف التي بلغت حد الطلب، الأشد حاجة أولاً.", false));
        Add(stock && (accounting || user.Can(Permission.InventoryManageProducts)), new("stock", "ورقة جرد المخزون", "الكميات والقيم بالتكلفة مع عمود للعدّ الفعلي.", false));
        Add(accounting, new("integrity", "فحص سلامة البيانات", "يثبت تطابق الدفاتر مع المخزون والعملاء والموردين.", false));

        Selected = Reports.FirstOrDefault();

        var result = await TryRunAsync(scope =>
            (accounting ? scope.Chart.Accounts() : [], all ? scope.Users.All() : [],
             user.Can(Permission.CustomerManage) ? scope.Parties.Customers() : [],
             user.Can(Permission.PurchaseView) ? scope.Parties.Suppliers() : []));

        if (!result.Ok)
        {
            return;
        }

        foreach (var a in result.Value.Item1)
        {
            Accounts.Add(new FilterOption<long?>(a.Label, a.Id));
        }

        Employees.Add(new FilterOption<long?>("كل الموظفين", null));

        foreach (var u in result.Value.Item2)
        {
            Employees.Add(new FilterOption<long?>(u.FullName, u.Id));
        }

        foreach (var c in result.Value.Item3)
        {
            Customers.Add(new FilterOption<long?>(c.Name, c.Id));
        }

        foreach (var s in result.Value.Item4)
        {
            Suppliers.Add(new FilterOption<long?>(s.Name, s.Id));
        }

        Account = Accounts.FirstOrDefault();
        Employee = Employees.FirstOrDefault();
        Customer = Customers.FirstOrDefault();
        Supplier = Suppliers.FirstOrDefault();
    }

    [RelayCommand]
    private void Show()
    {
        var report = Selected;

        if (report is null)
        {
            return;
        }

        var (from, to) = (FromDate, ToDate);
        var month = AppSession.Current.Clock.Today;
        var rangeFrom = from ?? new DateOnly(month.Year, month.Month, 1);
        var rangeTo = to ?? month;

        if (report.NeedsAccount && Account?.Value is null || report.NeedsCustomer && Customer?.Value is null || report.NeedsSupplier && Supplier?.Value is null)
        {
            ErrorMessage = "اختر الحساب أو العميل أو المورد المطلوب.";
            return;
        }

        try
        {
            using var scope = Session.NewScope();
            var documents = new DocumentFactory(scope);

            IDocument document = report.Key switch
            {
                "cash" => documents.CashReconciliation(User, from, to, Employee?.Value),
                "sales-product" => documents.SalesByProduct(rangeFrom, rangeTo),
                "sales-employee" => documents.SalesByEmployee(rangeFrom, rangeTo),
                "statements" => documents.FinancialStatements(from, to),
                "trial" => documents.TrialBalance(from, to),
                "ledger" => documents.GeneralLedger(Account!.Value!.Value, from, to),
                "vat" => documents.Vat(rangeFrom, rangeTo),
                "customer" => documents.CustomerStatement(Customer!.Value!.Value, from, to),
                "supplier" => documents.SupplierStatement(Supplier!.Value!.Value, from, to),
                "low-stock" => documents.LowStock(),
                "stock" => documents.StockList(),
                "integrity" => documents.IntegrityReport(),
                _ => throw new InvalidOperationException(report.Key),
            };

            Pdf.Open(document, report.Title);
            ErrorMessage = null;
        }
        catch (BusinessRuleException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}

/// <summary>A tick or a cross for an integrity check.</summary>
public sealed class PassGlyphConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
