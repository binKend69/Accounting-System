using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MotoParts.Core.Security;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Desktop.Views.Shell;

namespace MotoParts.Desktop.Views.Pages;

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly MainViewModel _shell;

    public DashboardViewModel(MainViewModel shell)
    {
        _shell = shell;
    }

    public override string Title => "لوحة المعلومات";

    [ObservableProperty]
    private Dashboard? _data;

    [ObservableProperty]
    private bool _integrityOk = true;

    [ObservableProperty]
    private string _integrityText = "";

    public ObservableCollection<ProductRow> LowStock { get; } = [];

    [ObservableProperty]
    private bool _hasLowStock;

    public bool SeesAll => Can(Permission.ReportViewAll);

    public bool CanSell => Can(Permission.SaleCreate);

    public bool CanSeeIntegrity => Can(Permission.AccountingView);

    public string Greeting
    {
        get
        {
            var hour = AppSession.Current.Clock.Now.Hour;
            var part = hour < 12 ? "صباح الخير" : "مساء الخير";
            return $"{part}، {User.FullName}";
        }
    }

    public override async Task LoadAsync()
    {
        var actor = User;
        var canSeeStock = Can(Permission.InventoryView);
        var checkIntegrity = CanSeeIntegrity;

        var result = await TryRunAsync(scope =>
        {
            var dashboard = scope.Reports.Dashboard(actor);
            var low = canSeeStock ? scope.Reports.LowStock().Take(8).ToList() : [];
            var failed = checkIntegrity ? scope.Reports.IntegrityChecks().Where(c => !c.Passed).ToList() : [];
            return (dashboard, low, failed);
        });

        if (!result.Ok)
        {
            return;
        }

        var (data, lowStock, failedChecks) = result.Value;
        Data = data;

        LowStock.Clear();

        foreach (var product in lowStock)
        {
            LowStock.Add(product);
        }

        HasLowStock = LowStock.Count > 0;
        IntegrityOk = failedChecks.Count == 0;
        IntegrityText = IntegrityOk
            ? "الدفاتر متوازنة ومتطابقة مع المخزون والعملاء والموردين."
            : $"يوجد خلل في {failedChecks.Count} من فحوصات السلامة: {string.Join("، ", failedChecks.Select(c => c.Name))}";
    }

    [RelayCommand]
    private Task GoToPos() => _shell.NavigateToAsync<PosViewModel>();

    [RelayCommand]
    private Task GoToShift() => _shell.NavigateToAsync<MyShiftViewModel>();

    [RelayCommand]
    private Task GoToProducts() => _shell.NavigateToAsync<ProductsViewModel>();

    [RelayCommand]
    private Task Refresh() => LoadAsync();
}
