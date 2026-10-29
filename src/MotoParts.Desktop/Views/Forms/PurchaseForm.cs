using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;

namespace MotoParts.Desktop.Views.Forms;

public sealed partial class PurchaseLineVm : ObservableObject
{
    public PurchaseLineVm(long productId, string sku, string name, string unit, Quantity quantity, Money unitCost)
    {
        ProductId = productId;
        Sku = sku;
        Name = name;
        Unit = unit;
        _quantityText = quantity.ToString();
        _unitCostText = unitCost.ToString();
    }

    public long ProductId { get; }

    public string Sku { get; }

    public string Name { get; }

    public string Unit { get; }

    [ObservableProperty]
    private string _quantityText;

    [ObservableProperty]
    private string _unitCostText;

    [ObservableProperty]
    private Money _lineTotal;
}

/// <summary>
/// A purchase order: draft it, edit it, receive it. A received or cancelled
/// order opens read-only.
/// </summary>
public sealed partial class PurchaseForm : FormViewModel
{
    private readonly long? _id;

    public PurchaseForm(long? id)
    {
        _id = id;
        Methods = [PaymentMethod.Credit, PaymentMethod.Cash, PaymentMethod.Transfer, PaymentMethod.Card];
        Lines.CollectionChanged += (_, e) =>
        {
            foreach (PurchaseLineVm line in e.NewItems ?? Array.Empty<PurchaseLineVm>())
            {
                line.PropertyChanged += OnLineChanged;
            }

            Recalculate();
        };
        SearchResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResults));
    }

    public override string Title => Number is null ? "أمر شراء جديد" : IsEditable ? $"أمر الشراء {Number}" : $"أمر الشراء {Number} ({Status.ToArabic()})";

    public override string SaveLabel => ReceiveNow ? "حفظ واستلام البضاعة" : "حفظ المسودة";

    public override bool CanSave => IsEditable;

    public override double Width => 980;

    public IReadOnlyList<PaymentMethod> Methods { get; }

    public ObservableCollection<SupplierRow> Suppliers { get; } = [];

    public ObservableCollection<PurchaseLineVm> Lines { get; } = [];

    public ObservableCollection<Product> SearchResults { get; } = [];

    [ObservableProperty] private string? _number;
    [ObservableProperty] private PurchaseStatus _status = PurchaseStatus.Draft;
    [ObservableProperty] private SupplierRow? _supplier;
    [ObservableProperty] private DateTime? _purchaseDate = AppSession.Current.Clock.Today.ToDateTime(TimeOnly.MinValue);
    [ObservableProperty] private string _supplierInvoice = "";
    [ObservableProperty] private PaymentMethod _method = PaymentMethod.Credit;
    [ObservableProperty] private string _taxPercentText = "15";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _productSearch = "";
    [ObservableProperty] private Product? _selectedResult;
    [ObservableProperty] private PurchaseLineVm? _selectedLine;
    [ObservableProperty] private Money _subtotal;
    [ObservableProperty] private Money _tax;
    [ObservableProperty] private Money _total;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveLabel))]
    private bool _receiveNow;

    public bool IsEditable => Status == PurchaseStatus.Draft;

    public bool HasResults => SearchResults.Count > 0;

    public bool CanReceive => Can(Permission.PurchaseReceive);

    public string? ResultMessage { get; private set; }

    public override async Task LoadAsync()
    {
        var id = _id;

        var result = await TryRunAsync(scope =>
        {
            var suppliers = scope.Parties.Suppliers(activeOnly: true);
            var rate = scope.Settings.GetTaxRate();
            var purchase = id is { } pid ? scope.Purchases.Get(pid) : null;
            var items = id is { } iid ? scope.Purchases.Items(iid) : [];
            var products = items.ToDictionary(i => i.Id, i => scope.Catalog.Get(i.ProductId).Unit);
            var supplier = purchase is null ? null : scope.Parties.Suppliers().FirstOrDefault(s => s.Id == purchase.SupplierId);
            return (suppliers, rate, purchase, items, products, supplier);
        });

        if (!result.Ok)
        {
            return;
        }

        var (suppliers, rate, purchase, items, units, existingSupplier) = result.Value;

        foreach (var supplier in suppliers)
        {
            Suppliers.Add(supplier);
        }

        TaxPercentText = (rate * 100m).ToString("0.##", CultureInfo.InvariantCulture);

        if (purchase is null)
        {
            return;
        }

        if (existingSupplier is not null && Suppliers.All(s => s.Id != existingSupplier.Id))
        {
            Suppliers.Add(existingSupplier);
        }

        Number = purchase.PurchaseNumber;
        Status = purchase.Status;
        Supplier = Suppliers.FirstOrDefault(s => s.Id == purchase.SupplierId);
        PurchaseDate = purchase.PurchaseDate.ToDateTime(TimeOnly.MinValue);
        SupplierInvoice = purchase.SupplierInvoiceNumber ?? "";
        Method = purchase.PaymentMethod;
        TaxPercentText = (purchase.TaxRate * 100m).ToString("0.##", CultureInfo.InvariantCulture);
        Notes = purchase.Notes ?? "";

        foreach (var item in items)
        {
            Lines.Add(new PurchaseLineVm(item.ProductId, item.Sku, item.Name, units[item.Id], item.Quantity, item.UnitCost));
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(CanSave));
    }

    [RelayCommand]
    private async Task FindProduct()
    {
        var term = ProductSearch.Trim();

        if (term.Length == 0)
        {
            return;
        }

        var found = await RunAsync(scope =>
        {
            var exact = scope.Catalog.FindByCode(term);
            return exact is not null ? [exact] : scope.Catalog.SearchForSale(term);
        });

        if (found is null)
        {
            return;
        }

        if (found.Count == 1)
        {
            AddLine(found[0]);
            return;
        }

        SearchResults.Clear();

        foreach (var product in found)
        {
            SearchResults.Add(product);
        }

        SelectedResult = SearchResults.FirstOrDefault();
        ErrorMessage = found.Count == 0 ? $"لا يوجد صنف يطابق \"{term}\". أضفه أولاً من شاشة الأصناف." : null;
    }

    [RelayCommand]
    private void PickResult()
    {
        if (SelectedResult is not null)
        {
            AddLine(SelectedResult);
        }
    }

    [RelayCommand]
    private void RemoveLine(PurchaseLineVm? line)
    {
        line ??= SelectedLine;

        if (line is not null && IsEditable)
        {
            Lines.Remove(line);
        }
    }

    public override async Task<bool> SaveAsync()
    {
        if (Supplier is null)
        {
            ErrorMessage = "اختر المورد.";
            return false;
        }

        if (PurchaseDate is null)
        {
            ErrorMessage = "اختر تاريخ الشراء.";
            return false;
        }

        if (!decimal.TryParse(TaxPercentText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var percent) || percent is < 0 or > 100)
        {
            ErrorMessage = "نسبة الضريبة غير صحيحة.";
            return false;
        }

        var lines = new List<PurchaseLineInput>();

        foreach (var line in Lines)
        {
            if (!Quantity.TryParse(line.QuantityText, out var quantity) || !quantity.IsPositive
                || !Money.TryParse(line.UnitCostText, out var cost) || cost.IsNegative)
            {
                ErrorMessage = $"قيمة غير صحيحة في سطر الصنف {line.Sku}.";
                return false;
            }

            lines.Add(new PurchaseLineInput(line.ProductId, quantity, cost));
        }

        var input = new PurchaseInput(Supplier.Id, DateOnly.FromDateTime(PurchaseDate.Value), lines, Method, percent / 100m, SupplierInvoice, Notes);
        var (actor, id, receive) = (User, _id, ReceiveNow);

        if (receive && !Dialogs.Confirm(
                $"حفظ واستلام البضاعة بقيمة {Total.ToDisplay()}؟ تدخل الكميات المخزون ويُسجَّل القيد، ولا يمكن تعديل الأمر بعد ذلك.",
                "استلام البضاعة", "حفظ واستلام", "رجوع"))
        {
            return false;
        }

        var purchase = await RunAsync(scope => scope.Db.InTransaction(() =>
        {
            var saved = id is { } pid ? scope.Purchases.UpdateDraft(actor, pid, input) : scope.Purchases.Draft(actor, input);
            return receive ? scope.Purchases.Receive(actor, saved.Id) : saved;
        }));

        if (purchase is null)
        {
            return false;
        }

        ResultMessage = receive
            ? $"اُستلم أمر الشراء {purchase.PurchaseNumber} ودخلت الكميات المخزون."
            : $"حُفظت مسودة أمر الشراء {purchase.PurchaseNumber}.";
        return true;
    }

    private void AddLine(Product product)
    {
        var existing = Lines.FirstOrDefault(l => l.ProductId == product.Id);

        if (existing is not null && Quantity.TryParse(existing.QuantityText, out var quantity))
        {
            existing.QuantityText = (quantity + Quantity.One).ToString();
        }
        else
        {
            Lines.Add(new PurchaseLineVm(product.Id, product.Sku, product.Name, product.Unit, Quantity.One, product.CostPrice));
        }

        ProductSearch = "";
        SearchResults.Clear();
        ErrorMessage = null;
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PurchaseLineVm.QuantityText) or nameof(PurchaseLineVm.UnitCostText))
        {
            Recalculate();
        }
    }

    partial void OnTaxPercentTextChanged(string value) => Recalculate();

    /// <summary>For display only; the service prices the order itself when it saves.</summary>
    private void Recalculate()
    {
        var subtotal = Money.Zero;

        foreach (var line in Lines)
        {
            line.LineTotal = Quantity.TryParse(line.QuantityText, out var q) && Money.TryParse(line.UnitCostText, out var c)
                ? c * q
                : Money.Zero;
            subtotal += line.LineTotal;
        }

        var rate = decimal.TryParse(TaxPercentText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var percent) ? percent / 100m : 0m;
        Subtotal = subtotal;
        Tax = subtotal * rate;
        Total = Subtotal + Tax;
    }
}
