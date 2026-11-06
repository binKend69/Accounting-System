using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;

namespace MotoParts.Desktop.Views.Forms;

/// <summary>A product's card. Stock and average cost are not edited here: they follow the movements.</summary>
public sealed partial class ProductForm : FormViewModel
{
    private readonly long? _id;

    public ProductForm(long? id) => _id = id;

    public override string Title => _id is null ? "صنف جديد" : "تعديل بيانات الصنف";

    public override double Width => 680;

    public ObservableCollection<FilterOption<long?>> Categories { get; } = [];

    public IReadOnlyList<string> Units { get; } = ["قطعة", "طقم", "زوج", "لتر", "متر", "علبة", "كرتون", "كيلو"];

    [ObservableProperty] private string _sku = "";
    [ObservableProperty] private string _barcode = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private FilterOption<long?>? _category;
    [ObservableProperty] private string _brand = "";
    [ObservableProperty] private string _fitsModels = "";
    [ObservableProperty] private string _unit = "قطعة";
    [ObservableProperty] private string _sellingPriceText = "";
    [ObservableProperty] private string _costPriceText = "0";
    [ObservableProperty] private string _reorderLevelText = "0";
    [ObservableProperty] private bool _isActive = true;

    /// <summary>Once anything has been received, cost is the average of what was paid.</summary>
    [ObservableProperty] private bool _costIsEditable = true;

    [ObservableProperty] private string _stockInfo = "";

    public override async Task LoadAsync()
    {
        var id = _id;

        var result = await TryRunAsync(scope =>
        {
            var categories = scope.Catalog.Categories(activeOnly: true);
            var product = id is { } pid ? scope.Catalog.Get(pid) : null;
            var hasHistory = id is { } hid && scope.Inventory.MovementsFor(hid).Count > 0;
            return (categories, product, hasHistory);
        });

        if (!result.Ok)
        {
            return;
        }

        var (categories, product, hasHistory) = result.Value;

        Categories.Add(new FilterOption<long?>("بدون فئة", null));

        foreach (var category in categories)
        {
            Categories.Add(new FilterOption<long?>(category.Name, category.Id));
        }

        Category = Categories[0];

        if (product is null)
        {
            return;
        }

        Sku = product.Sku;
        Barcode = product.Barcode ?? "";
        Name = product.Name;
        Description = product.Description ?? "";
        Category = Categories.FirstOrDefault(c => c.Value == product.CategoryId) ?? Categories[0];
        Brand = product.Brand ?? "";
        FitsModels = product.FitsModels ?? "";
        Unit = product.Unit;
        SellingPriceText = product.SellingPrice.ToString();
        CostPriceText = product.CostPrice.ToString();
        ReorderLevelText = product.ReorderLevel.ToString();
        IsActive = product.IsActive;
        CostIsEditable = !hasHistory;
        StockInfo = $"المتوفر حالياً {product.StockQty.ToDisplay()} {product.Unit} بقيمة {product.StockValue.ToDisplay()}";
    }

    public override async Task<bool> SaveAsync()
    {
        if (!Money.TryParse(SellingPriceText, out var selling))
        {
            ErrorMessage = "سعر البيع غير صحيح.";
            return false;
        }

        if (!Money.TryParse(string.IsNullOrWhiteSpace(CostPriceText) ? "0" : CostPriceText, out var cost))
        {
            ErrorMessage = "التكلفة غير صحيحة.";
            return false;
        }

        if (!Quantity.TryParse(string.IsNullOrWhiteSpace(ReorderLevelText) ? "0" : ReorderLevelText, out var reorder))
        {
            ErrorMessage = "حد إعادة الطلب غير صحيح.";
            return false;
        }

        if (selling < cost && !cost.IsZero &&
            !Dialogs.Confirm($"سعر البيع ({selling.ToDisplay()}) أقل من التكلفة ({cost.ToDisplay()}). هل تريد الحفظ رغم ذلك؟", "تنبيه", "حفظ", "رجوع"))
        {
            return false;
        }

        var input = new ProductInput(Sku, Barcode, Name, Description, Category?.Value, Brand, FitsModels, Unit, selling, cost, reorder, IsActive);
        var actor = User;
        var id = _id;

        return await RunAsync(scope => id is { } pid ? scope.Catalog.UpdateProduct(pid, input, actor) : scope.Catalog.CreateProduct(input, actor)) is not null;
    }
}

/// <summary>Stock count or correction, with a mandatory reason; posts the value difference to the books.</summary>
public sealed partial class StockAdjustForm : FormViewModel
{
    private readonly long _productId;

    public StockAdjustForm(long productId) => _productId = productId;

    public override string Title => "جرد وتسوية المخزون";

    public override string SaveLabel => "تسجيل التسوية";

    [ObservableProperty] private Product? _product;
    [ObservableProperty] private bool _byCount = true;
    [ObservableProperty] private string _quantityText = "";
    [ObservableProperty] private string _reason = "";

    public override async Task LoadAsync()
    {
        Product = await RunAsync(scope => scope.Catalog.Get(_productId));

        if (Product is not null)
        {
            QuantityText = Product.StockQty.ToString();
        }
    }

    partial void OnByCountChanged(bool value) =>
        QuantityText = value ? Product?.StockQty.ToString() ?? "" : "";

    public override async Task<bool> SaveAsync()
    {
        if (!Quantity.TryParse(QuantityText, out var quantity))
        {
            ErrorMessage = "أدخل كمية صحيحة.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Reason))
        {
            ErrorMessage = "اكتب سبب التسوية (جرد دوري، تالف، فقد...).";
            return false;
        }

        var (actor, id, reason, byCount) = (User, _productId, Reason, ByCount);

        var (ok, movement) = await TryRunAsync(scope => byCount
            ? scope.Inventory.AdjustToCount(actor, id, quantity, reason)
            : scope.Inventory.Adjust(actor, id, quantity, reason));

        if (!ok)
        {
            return false;
        }

        if (movement is null)
        {
            Dialogs.Info("الكمية المعدودة مطابقة للمسجلة؛ لا حاجة لتسوية.");
        }

        return true;
    }
}

/// <summary>Units already on the shelf when the shop starts using the system.</summary>
public sealed partial class OpeningStockForm : FormViewModel
{
    private readonly long _productId;

    public OpeningStockForm(long productId) => _productId = productId;

    public override string Title => "رصيد افتتاحي للصنف";

    [ObservableProperty] private Product? _product;
    [ObservableProperty] private string _quantityText = "";
    [ObservableProperty] private string _unitCostText = "";

    public override async Task LoadAsync()
    {
        Product = await RunAsync(scope => scope.Catalog.Get(_productId));

        if (Product is not null && Product.CostPrice.IsPositive)
        {
            UnitCostText = Product.CostPrice.ToString();
        }
    }

    public override async Task<bool> SaveAsync()
    {
        if (!Quantity.TryParse(QuantityText, out var quantity) || !quantity.IsPositive)
        {
            ErrorMessage = "أدخل الكمية الموجودة فعلياً.";
            return false;
        }

        if (!Money.TryParse(UnitCostText, out var cost) || cost.IsNegative)
        {
            ErrorMessage = "أدخل تكلفة الوحدة (سعر الشراء).";
            return false;
        }

        var (actor, id) = (User, _productId);
        return await RunAsync(scope => scope.Inventory.RecordOpeningStock(actor, id, quantity, cost)) is not null;
    }
}

/// <summary>A product's full movement history, newest first.</summary>
public sealed partial class ProductMovementsForm : FormViewModel
{
    private readonly long _productId;

    public ProductMovementsForm(long productId) => _productId = productId;

    public override string Title => Product is null ? "حركات الصنف" : $"حركات الصنف {Product.Sku} - {Product.Name}";

    public override bool CanSave => false;

    public override double Width => 980;

    [ObservableProperty] private Product? _product;

    public ObservableCollection<StockMovementRow> Movements { get; } = [];

    public override async Task LoadAsync()
    {
        var id = _productId;
        var result = await TryRunAsync(scope => (scope.Catalog.Get(id), scope.Inventory.Movements(null, null, productId: id)));

        if (!result.Ok)
        {
            return;
        }

        Product = result.Value.Item1;

        foreach (var movement in result.Value.Item2)
        {
            Movements.Add(movement);
        }

        OnPropertyChanged(nameof(Title));
    }

    public override Task<bool> SaveAsync() => Task.FromResult(true);
}
