using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MotoParts.Core.Security;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Desktop.Views.Pages;

namespace MotoParts.Desktop.Views.Shell;

/// <summary>One entry in the sidebar.</summary>
public sealed partial class NavItem : ObservableObject
{
    private readonly Func<PageViewModel> _factory;
    private PageViewModel? _page;

    public NavItem(string group, string title, string glyph, Func<PageViewModel> factory)
    {
        Group = group;
        Title = title;
        Glyph = glyph;
        _factory = factory;
    }

    public string Group { get; }

    public string Title { get; }

    public string Glyph { get; }

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Pages are created on first visit and kept, so a half-filled form survives a detour.</summary>
    public PageViewModel Page => _page ??= _factory();
}

public sealed record NavGroup(string Title, IReadOnlyList<NavItem> Items);

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        var user = AppSession.Current.RequireUser;
        UserName = user.FullName;
        RoleLabel = user.IsOwner ? "المالك" : user.Roles.Contains(RoleName.Accountant) ? "المحاسب" : "البائع";

        using (var scope = AppSession.Current.NewScope())
        {
            ShopName = scope.Settings.GetShopProfile().Name;
        }

        Groups = BuildNavigation(user);
    }

    public string ShopName { get; }

    public string UserName { get; }

    public string RoleLabel { get; }

    public string Today => AppSession.Current.Clock.Today.ToString("yyyy-MM-dd");

    public IReadOnlyList<NavGroup> Groups { get; }

    [ObservableProperty]
    private PageViewModel? _currentPage;

    [ObservableProperty]
    private string _tillStatus = "";

    [ObservableProperty]
    private bool _hasOpenTill;

    public IEnumerable<NavItem> AllItems => Groups.SelectMany(g => g.Items);

    [RelayCommand]
    public async Task NavigateAsync(NavItem item)
    {
        foreach (var other in AllItems)
        {
            other.IsSelected = other == item;
        }

        CurrentPage = item.Page;
        await item.Page.LoadAsync();
        await RefreshTillStatusAsync();
    }

    /// <summary>Jumps to a page by its type, e.g. from a dashboard shortcut.</summary>
    public async Task NavigateToAsync<TPage>() where TPage : PageViewModel
    {
        var item = AllItems.FirstOrDefault(i => i.Page is TPage);

        if (item is not null)
        {
            await NavigateAsync(item);
        }
    }

    public async Task RefreshTillStatusAsync()
    {
        var user = AppSession.Current.User;

        if (user is null)
        {
            return;
        }

        var session = await Task.Run(() =>
        {
            using var scope = AppSession.Current.NewScope();
            return scope.Shifts.OpenSessionFor(user.Id);
        });

        HasOpenTill = session is not null;
        TillStatus = session is null ? "لا توجد وردية مفتوحة" : $"ورديتك مفتوحة منذ {session.OpenedAt:HH:mm}";
    }

    private List<NavGroup> BuildNavigation(Actor user)
    {
        var items = new List<NavItem>();

        void Add(bool allowed, string group, string title, string glyph, Func<PageViewModel> factory)
        {
            if (allowed)
            {
                items.Add(new NavItem(group, title, glyph, factory));
            }
        }

        const string Main = "الرئيسية";
        const string SalesGroup = "المبيعات";
        const string StockGroup = "المخزون والمشتريات";
        const string MoneyGroup = "الحسابات";
        const string StaffGroup = "الموظفون";
        const string AdminGroup = "الإدارة";

        Add(true, Main, "لوحة المعلومات", "", () => new DashboardViewModel(this));
        Add(user.Can(Permission.SaleCreate), Main, "نقطة البيع", "", () => new PosViewModel());
        Add(user.Can(Permission.ShiftManageOwn), Main, "ورديتي والحضور", "", () => new MyShiftViewModel());

        Add(user.CanAny(Permission.SaleViewOwn, Permission.SaleViewAll), SalesGroup, "الفواتير", "", () => new SalesViewModel());
        Add(user.CanAny(Permission.SaleRefund, Permission.SaleViewAll), SalesGroup, "المرتجعات", "", () => new RefundsViewModel());
        Add(user.Can(Permission.CustomerManage), SalesGroup, "العملاء", "", () => new CustomersViewModel());
        Add(user.Can(Permission.CustomerReceive), SalesGroup, "سندات القبض", "", () => new ReceiptsViewModel());

        Add(user.Can(Permission.InventoryView), StockGroup, "الأصناف", "", () => new ProductsViewModel());
        Add(user.Can(Permission.InventoryManageProducts), StockGroup, "الفئات", "", () => new CategoriesViewModel());
        Add(user.Can(Permission.InventoryView), StockGroup, "حركات المخزون", "", () => new StockMovementsViewModel());
        Add(user.Can(Permission.PurchaseView), StockGroup, "المشتريات", "", () => new PurchasesViewModel());
        Add(user.Can(Permission.PurchaseView), StockGroup, "الموردون", "", () => new SuppliersViewModel());
        Add(user.Can(Permission.SupplierPay), StockGroup, "سندات الصرف", "", () => new SupplierPaymentsViewModel());

        Add(user.Can(Permission.ExpenseCreate), MoneyGroup, "المصروفات", "", () => new ExpensesViewModel());
        Add(user.CanAny(Permission.ReportViewOwn, Permission.ReportViewAll, Permission.AccountingView), MoneyGroup, "التقارير", "", () => new ReportsViewModel());
        Add(user.Can(Permission.AccountingView), MoneyGroup, "القيود اليومية", "", () => new JournalViewModel());
        Add(user.Can(Permission.AccountingView), MoneyGroup, "دليل الحسابات", "", () => new AccountsViewModel());
        Add(user.CanAny(Permission.FiscalPeriodManage, Permission.FiscalPeriodLock), MoneyGroup, "الفترات المحاسبية", "", () => new FiscalPeriodsViewModel());

        Add(user.Can(Permission.ShiftViewAll), StaffGroup, "سجل الورديات", "", () => new ShiftSessionsViewModel());
        Add(user.Can(Permission.AttendanceViewAll), StaffGroup, "سجل الحضور", "", () => new AttendanceViewModel());
        Add(user.Can(Permission.PayrollManage), StaffGroup, "الرواتب", "", () => new PayrollViewModel());

        Add(user.Can(Permission.UserManage), AdminGroup, "المستخدمون", "", () => new UsersViewModel());
        Add(user.Can(Permission.AuditView), AdminGroup, "سجل التدقيق", "", () => new AuditLogViewModel());
        Add(user.CanAny(Permission.SettingsManage, Permission.BackupManage), AdminGroup, "الإعدادات والنسخ الاحتياطي", "", () => new SettingsViewModel());

        return items.GroupBy(i => i.Group).Select(g => new NavGroup(g.Key, g.ToList())).ToList();
    }
}
