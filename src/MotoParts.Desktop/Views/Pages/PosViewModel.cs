using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Documents;

namespace MotoParts.Desktop.Views.Pages;

/// <summary>One line in the till's cart, as typed by the cashier.</summary>
public sealed partial class PosLine : ObservableObject
{
    public PosLine(Product product)
    {
        ProductId = product.Id;
        Sku = product.Sku;
        Name = product.Name;
        Unit = product.Unit;
        ListPrice = product.SellingPrice;
        _priceText = product.SellingPrice.ToString();
    }

    public long ProductId { get; }

    public string Sku { get; }

    public string Name { get; }

    public string Unit { get; }

    public Money ListPrice { get; }

    [ObservableProperty]
    private string _quantityText = "1";

    [ObservableProperty]
    private string _priceText;

    [ObservableProperty]
    private string _discountText = "0";

    [ObservableProperty]
    private Money _lineTotal;

    [ObservableProperty]
    private string? _warning;

    [ObservableProperty]
    private bool _isInvalid;

    /// <summary>Parses the typed values; false when any of them is not a valid number.</summary>
    public bool TryBuild(out CartLine line)
    {
        line = null!;

        if (!Quantity.TryParse(QuantityText, out var quantity) || !quantity.IsPositive
            || !Money.TryParse(PriceText, out var price) || price.IsNegative
            || !Money.TryParse(string.IsNullOrWhiteSpace(DiscountText) ? "0" : DiscountText, out var discount) || discount.IsNegative)
        {
            IsInvalid = true;
            return false;
        }

        IsInvalid = false;

        // Only a changed price is sent as an override; otherwise the product's
        // current price applies at checkout.
        line = new CartLine(ProductId, quantity, price == ListPrice ? null : price, discount);
        return true;
    }
}

public sealed partial class PosViewModel : PageViewModel
{
    private CartQuote? _quote;
    private bool _requoting;
    private bool _requotePending;

    public PosViewModel()
    {
        Lines.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
            {
                foreach (PosLine line in e.NewItems)
                {
                    line.PropertyChanged += OnLineEdited;
                }
            }

            OnPropertyChanged(nameof(IsCartEmpty));
            _ = RequoteAsync();
        };
    }

    public override string Title => "نقطة البيع";

    /// <summary>Asks the view to put the cursor back in the scanner box.</summary>
    public event EventHandler? FocusScannerRequested;

    public event EventHandler? FocusTenderedRequested;

    public ObservableCollection<PosLine> Lines { get; } = [];

    public ObservableCollection<Product> SearchResults { get; } = [];

    public ObservableCollection<CustomerRow> Customers { get; } = [];

    public IReadOnlyList<PaymentMethod> PaymentMethods { get; } =
        [PaymentMethod.Cash, PaymentMethod.Card, PaymentMethod.Transfer, PaymentMethod.Credit];

    [ObservableProperty]
    private string _code = "";

    [ObservableProperty]
    private Product? _selectedResult;

    [ObservableProperty]
    private PosLine? _selectedLine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCash))]
    [NotifyPropertyChangedFor(nameof(IsCredit))]
    private PaymentMethod _paymentMethod = PaymentMethod.Cash;

    [ObservableProperty]
    private CustomerRow? _customer;

    [ObservableProperty]
    private string _tenderedText = "";

    [ObservableProperty]
    private string _notes = "";

    [ObservableProperty]
    private bool _hasOpenTill;

    // Totals, from the quote.
    [ObservableProperty]
    private Money _subtotal;

    [ObservableProperty]
    private Money _discountTotal;

    [ObservableProperty]
    private Money _taxTotal;

    [ObservableProperty]
    private Money _total;

    [ObservableProperty]
    private string _taxLabel = "الضريبة";

    [ObservableProperty]
    private string? _change;

    [ObservableProperty]
    private string? _warnings;

    // The sale just completed, for printing.
    [ObservableProperty]
    private Sale? _lastSale;

    public bool IsCash => PaymentMethod == PaymentMethod.Cash;

    public bool IsCredit => PaymentMethod == PaymentMethod.Credit;

    public bool HasSearchResults => SearchResults.Count > 0;

    public bool IsCartEmpty => Lines.Count == 0;

    public bool CanCompleteSale => HasOpenTill && Lines.Count > 0 && Lines.All(l => !l.IsInvalid) && !IsBusy;

    public override async Task LoadAsync()
    {
        var userId = User.Id;

        var result = await TryRunAsync(scope =>
        {
            var session = scope.Shifts.OpenSessionFor(userId);
            var customers = scope.Parties.Customers(activeOnly: true);
            return (session, customers);
        });

        if (!result.Ok)
        {
            return;
        }

        HasOpenTill = result.Value.session is not null;

        var selectedId = Customer?.Id;
        Customers.Clear();

        foreach (var customer in result.Value.customers)
        {
            Customers.Add(customer);
        }

        Customer = Customers.FirstOrDefault(c => c.Id == selectedId);
        OnPropertyChanged(nameof(CanCompleteSale));
        FocusScannerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Enter in the scanner box: an exact code adds at once, anything else searches.</summary>
    [RelayCommand]
    private async Task AddByCode()
    {
        var code = Code.Trim();

        if (code.Length == 0)
        {
            if (SelectedResult is not null)
            {
                AddProduct(SelectedResult);
            }

            return;
        }

        var found = await TryRunAsync(scope =>
        {
            var exact = scope.Catalog.FindByCode(code);
            return exact is not null ? [exact] : scope.Catalog.SearchForSale(code);
        });

        if (!found.Ok)
        {
            return;
        }

        var products = found.Value!;

        if (products.Count == 1)
        {
            AddProduct(products[0]);
            return;
        }

        SearchResults.Clear();

        foreach (var product in products)
        {
            SearchResults.Add(product);
        }

        SelectedResult = SearchResults.FirstOrDefault();
        OnPropertyChanged(nameof(HasSearchResults));

        if (products.Count == 0)
        {
            ErrorMessage = $"لا يوجد صنف يطابق \"{code}\".";
            Code = "";
        }
    }

    [RelayCommand]
    private void PickResult(Product? product)
    {
        if (product is not null)
        {
            AddProduct(product);
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        Code = "";
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasSearchResults));
        FocusScannerRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RemoveLine(PosLine? line)
    {
        line ??= SelectedLine;

        if (line is not null)
        {
            line.PropertyChanged -= OnLineEdited;
            Lines.Remove(line);
        }

        FocusScannerRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ClearCart()
    {
        if (Lines.Count > 0 && !Dialogs.Confirm("تفريغ السلة وإلغاء البيع الحالي؟", "تفريغ السلة", "تفريغ", "رجوع", danger: true))
        {
            return;
        }

        ResetSale();
    }

    [RelayCommand]
    private void SetPayment(PaymentMethod method)
    {
        PaymentMethod = method;

        if (method != PaymentMethod.Cash)
        {
            TenderedText = "";
        }

        UpdateChange();

        if (method == PaymentMethod.Cash)
        {
            FocusTenderedRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private async Task CompleteSale()
    {
        if (!CanCompleteSale)
        {
            if (!HasOpenTill)
            {
                ErrorMessage = "لا توجد وردية مفتوحة باسمك. افتح وردية من شاشة «ورديتي والحضور» أولاً.";
            }

            return;
        }

        var cart = new List<CartLine>();

        foreach (var line in Lines)
        {
            if (!line.TryBuild(out var cartLine))
            {
                ErrorMessage = $"قيمة غير صحيحة في سطر الصنف {line.Sku}.";
                return;
            }

            cart.Add(cartLine);
        }

        Money? tendered = null;

        if (IsCash && !string.IsNullOrWhiteSpace(TenderedText))
        {
            if (!Money.TryParse(TenderedText, out var amount))
            {
                ErrorMessage = "المبلغ المدفوع غير صحيح.";
                return;
            }

            tendered = amount;
        }

        var actor = User;
        var method = PaymentMethod;
        var customerId = Customer?.Id;
        var notes = Notes;

        var sale = await RunAsync(scope => scope.Sales.Checkout(actor, cart, method, tendered, customerId, notes));

        if (sale is null)
        {
            return;
        }

        LastSale = sale;
        ResetSale();
        SuccessMessage = sale.PaymentMethod == PaymentMethod.Cash && sale.ChangeDue.IsPositive
            ? $"تمت الفاتورة {sale.InvoiceNumber} — الباقي للعميل: {sale.ChangeDue.ToDisplay()}"
            : $"تمت الفاتورة {sale.InvoiceNumber} بمبلغ {sale.Total.ToDisplay()}";
    }

    [RelayCommand]
    private void PrintReceipt() => Print(InvoiceFormat.Receipt80mm);

    [RelayCommand]
    private void PrintA4() => Print(InvoiceFormat.A4);

    protected override void OnBusinessRuleRefused(BusinessRuleException exception) =>
        FocusScannerRequested?.Invoke(this, EventArgs.Empty);

    partial void OnTenderedTextChanged(string value) => UpdateChange();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(IsBusy))
        {
            OnPropertyChanged(nameof(CanCompleteSale));
        }
    }

    private void Print(InvoiceFormat format)
    {
        if (LastSale is null)
        {
            return;
        }

        var saleId = LastSale.Id;
        var number = LastSale.InvoiceNumber;

        using var scope = Session.NewScope();
        Pdf.Open(new DocumentFactory(scope).Invoice(saleId, format), number);
        FocusScannerRequested?.Invoke(this, EventArgs.Empty);
    }

    private void AddProduct(Product product)
    {
        ErrorMessage = null;
        SuccessMessage = null;
        LastSale = null;

        // Scanning the same part again adds one to its line.
        var existing = Lines.FirstOrDefault(l => l.ProductId == product.Id);

        if (existing is not null && Quantity.TryParse(existing.QuantityText, out var quantity))
        {
            existing.QuantityText = (quantity + Quantity.One).ToString();
            SelectedLine = existing;
        }
        else
        {
            var line = new PosLine(product);
            Lines.Add(line);
            SelectedLine = line;
        }

        Code = "";
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasSearchResults));
        FocusScannerRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ResetSale()
    {
        foreach (var line in Lines)
        {
            line.PropertyChanged -= OnLineEdited;
        }

        Lines.Clear();
        TenderedText = "";
        Notes = "";
        Customer = null;
        PaymentMethod = PaymentMethod.Cash;
        Code = "";
        SearchResults.Clear();
        OnPropertyChanged(nameof(HasSearchResults));
        FocusScannerRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnLineEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PosLine.QuantityText) or nameof(PosLine.PriceText) or nameof(PosLine.DiscountText))
        {
            _ = RequoteAsync();
        }
    }

    /// <summary>Reprices the cart with the same code checkout uses.</summary>
    private async Task RequoteAsync()
    {
        if (_requoting)
        {
            // An edit arrived mid-quote: price again once the current pass ends,
            // so the total on screen always reflects the last keystroke.
            _requotePending = true;
            return;
        }

        _requoting = true;

        try
        {
            var cart = new List<CartLine>();

            foreach (var line in Lines)
            {
                if (line.TryBuild(out var cartLine))
                {
                    cart.Add(cartLine);
                }
            }

            OnPropertyChanged(nameof(CanCompleteSale));

            if (cart.Count == 0)
            {
                _quote = null;
                Subtotal = DiscountTotal = TaxTotal = Total = Money.Zero;
                Warnings = null;
                UpdateChange();
                return;
            }

            CartQuote quote;

            try
            {
                quote = await Task.Run(() =>
                {
                    using var scope = Session.NewScope();
                    return scope.Sales.Quote(cart);
                });
            }
            catch (BusinessRuleException ex)
            {
                Warnings = ex.Message;
                return;
            }

            _quote = quote;
            Subtotal = quote.Subtotal;
            DiscountTotal = quote.DiscountTotal;
            TaxTotal = quote.TaxTotal;
            Total = quote.Total;
            TaxLabel = quote.PricesIncludeTax
                ? $"منها ضريبة {quote.TaxRate * 100m:0.##}٪"
                : $"الضريبة {quote.TaxRate * 100m:0.##}٪";

            foreach (var line in Lines)
            {
                var quoted = quote.Lines.FirstOrDefault(q => q.ProductId == line.ProductId);
                line.LineTotal = quoted?.LineTotal ?? Money.Zero;
                line.Warning = quoted is not null && quoted.Quantity > quoted.Available
                    ? $"المتوفر {quoted.Available.ToDisplay()} فقط"
                    : null;
            }

            Warnings = quote.Warnings.Count > 0 ? "نقص في المخزون: " + string.Join("  |  ", quote.Warnings) : null;
            UpdateChange();
        }
        finally
        {
            _requoting = false;

            if (_requotePending)
            {
                _requotePending = false;
                _ = RequoteAsync();
            }
        }
    }

    private void UpdateChange()
    {
        if (!IsCash || !Money.TryParse(TenderedText, out var tendered))
        {
            Change = null;
            return;
        }

        var difference = tendered - Total;
        Change = difference.IsNegative
            ? $"ناقص {difference.Abs().ToDisplay()}"
            : $"الباقي للعميل: {difference.ToDisplay()}";
    }
}
