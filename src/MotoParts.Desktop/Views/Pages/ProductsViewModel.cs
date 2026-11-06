using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MotoParts.Core.Common;
using MotoParts.Core.Security;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Desktop.Views.Forms;
using MotoParts.Documents;

namespace MotoParts.Desktop.Views.Pages;

public sealed partial class ProductsViewModel : PageViewModel
{
    public override string Title => "الأصناف";

    public ObservableCollection<ProductRow> Products { get; } = [];

    public ObservableCollection<FilterOption<long?>> Categories { get; } = [];

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private FilterOption<long?>? _category;

    [ObservableProperty]
    private bool _activeOnly = true;

    [ObservableProperty]
    private bool _lowStockOnly;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(AdjustCommand), nameof(OpeningStockCommand), nameof(MovementsCommand), nameof(DeleteCommand))]
    private ProductRow? _selected;

    [ObservableProperty]
    private string _summary = "";

    private bool _loadingCategories;

    public bool CanManage => Can(Permission.InventoryManageProducts);

    public bool CanAdjust => Can(Permission.InventoryAdjustStock);

    public bool SeesCost => Can(Permission.AccountingView) || Can(Permission.InventoryManageProducts);

    partial void OnSearchChanged(string value) => _ = LoadAsync();

    partial void OnCategoryChanged(FilterOption<long?>? value)
    {
        if (!_loadingCategories)
        {
            _ = LoadAsync();
        }
    }

    partial void OnActiveOnlyChanged(bool value) => _ = LoadAsync();

    partial void OnLowStockOnlyChanged(bool value) => _ = LoadAsync();

    public override async Task LoadAsync()
    {
        var (search, categoryId, activeOnly, lowOnly) = (Search, Category?.Value, ActiveOnly, LowStockOnly);
        var loadCategories = Categories.Count == 0;

        var result = await TryRunAsync(scope =>
            (scope.Catalog.Products(search, categoryId, activeOnly, lowOnly), loadCategories ? scope.Catalog.Categories() : []));

        if (!result.Ok)
        {
            return;
        }

        if (loadCategories)
        {
            _loadingCategories = true;
            Categories.Add(new FilterOption<long?>("كل الفئات", null));

            foreach (var category in result.Value.Item2)
            {
                Categories.Add(new FilterOption<long?>(category.Name, category.Id));
            }

            Category = Categories[0];
            _loadingCategories = false;
        }

        var selectedId = Selected?.Id;
        Products.Clear();

        foreach (var product in result.Value.Item1)
        {
            Products.Add(product);
        }

        Selected = Products.FirstOrDefault(p => p.Id == selectedId);
        Summary = $"{Products.Count} صنف" +
                  (SeesCost ? $"   |   قيمة المخزون بالتكلفة {Products.Sum(p => p.StockValue).ToDisplay()}" : "") +
                  $"   |   {Products.Count(p => p.IsLowStock)} تحت حد الطلب";
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand]
    private async Task Add()
    {
        if (FormHost.Show(new ProductForm(null)))
        {
            await LoadAsync();
            SuccessMessage = "أُضيف الصنف. أدخل رصيده الافتتاحي أو استلمه بأمر شراء.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Edit()
    {
        if (!CanManage)
        {
            return;
        }

        if (FormHost.Show(new ProductForm(Selected!.Id)))
        {
            await LoadAsync();
            SuccessMessage = "حُفظت بيانات الصنف.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Adjust()
    {
        if (FormHost.Show(new StockAdjustForm(Selected!.Id)))
        {
            await LoadAsync();
            SuccessMessage = "سُجّلت تسوية المخزون وقيدها المحاسبي.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task OpeningStock()
    {
        if (FormHost.Show(new OpeningStockForm(Selected!.Id)))
        {
            await LoadAsync();
            SuccessMessage = "سُجّل الرصيد الافتتاحي.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Movements() => FormHost.Show(new ProductMovementsForm(Selected!.Id));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Delete()
    {
        var product = Selected!;

        if (!Dialogs.Confirm($"حذف الصنف {product.Sku} - {product.Name}؟\nيُسمح بالحذف فقط لصنف لم تُسجَّل عليه أي حركة؛ غير ذلك أوقفه من شاشة التعديل.",
                "حذف صنف", "حذف", "إلغاء", danger: true))
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Catalog.DeleteProduct(product.Id, actor)))
        {
            await LoadAsync();
            SuccessMessage = "حُذف الصنف.";
        }
    }

    [RelayCommand]
    private void PrintStockList()
    {
        using var scope = Session.NewScope();
        Pdf.Open(new DocumentFactory(scope).StockList(), "جرد-المخزون");
    }

    [RelayCommand]
    private void PrintLowStock()
    {
        using var scope = Session.NewScope();
        Pdf.Open(new DocumentFactory(scope).LowStock(), "إعادة-الطلب");
    }
}

public sealed partial class CategoriesViewModel : PageViewModel
{
    public override string Title => "الفئات";

    public ObservableCollection<CategoryRow> Categories { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(ToggleActiveCommand), nameof(DeleteCommand))]
    private CategoryRow? _selected;

    public override async Task LoadAsync()
    {
        var rows = await RunAsync(scope => scope.Catalog.Categories());

        if (rows is null)
        {
            return;
        }

        var selectedId = Selected?.Id;
        Categories.Clear();

        foreach (var row in rows)
        {
            Categories.Add(row);
        }

        Selected = Categories.FirstOrDefault(c => c.Id == selectedId);
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand]
    private async Task Add()
    {
        var name = Dialogs.Prompt("فئة جديدة", "اسم الفئة:", multiline: false);

        if (name is null)
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Catalog.SaveCategory(null, name, null, true, actor)) is not null)
        {
            await LoadAsync();
            SuccessMessage = $"أُضيفت الفئة {name}.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Rename()
    {
        var category = Selected!;
        var name = Dialogs.Prompt("تعديل الفئة", "اسم الفئة:", category.Name, multiline: false);

        if (name is null)
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Catalog.SaveCategory(category.Id, name, category.Description, category.IsActive, actor)) is not null)
        {
            await LoadAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleActive()
    {
        var category = Selected!;
        var actor = User;

        if (await RunAsync(scope => scope.Catalog.SaveCategory(category.Id, category.Name, category.Description, !category.IsActive, actor)) is not null)
        {
            await LoadAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Delete()
    {
        var category = Selected!;

        if (!Dialogs.Confirm($"حذف الفئة «{category.Name}»؟", "حذف فئة", "حذف", "إلغاء", danger: true))
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Catalog.DeleteCategory(category.Id, actor)))
        {
            await LoadAsync();
        }
    }
}

public sealed partial class StockMovementsViewModel : ListPageViewModel
{
    public StockMovementsViewModel()
    {
        Types =
        [
            new("كل الحركات", null),
            .. Enum.GetValues<Core.Domain.StockMovementType>().Select(t => new FilterOption<Core.Domain.StockMovementType?>(Core.Domain.EnumExtensions.ToArabic(t), t)),
        ];
        _type = Types[0];
    }

    public override string Title => "حركات المخزون";

    public ObservableCollection<StockMovementRow> Movements { get; } = [];

    public IReadOnlyList<FilterOption<Core.Domain.StockMovementType?>> Types { get; }

    [ObservableProperty]
    private FilterOption<Core.Domain.StockMovementType?> _type;

    public bool SeesCost => Can(Permission.AccountingView) || Can(Permission.InventoryManageProducts);

    partial void OnTypeChanged(FilterOption<Core.Domain.StockMovementType?> value) => _ = RefreshAsync();

    protected override async Task RefreshAsync()
    {
        var (from, to, type, search) = (FromDate, ToDate, Type.Value, Search);
        var rows = await RunAsync(scope => scope.Inventory.Movements(from, to, type, search));

        if (rows is null)
        {
            return;
        }

        Movements.Clear();

        foreach (var row in rows)
        {
            Movements.Add(row);
        }
    }
}
