using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Documents;

namespace MotoParts.Desktop.Views.Forms;

/// <summary>Read-only view of one invoice: header, lines and any returns.</summary>
public sealed partial class SaleDetailsForm : FormViewModel
{
    private readonly long _saleId;

    public SaleDetailsForm(long saleId) => _saleId = saleId;

    public override string Title => Sale is null ? "تفاصيل الفاتورة" : $"الفاتورة {Sale.InvoiceNumber}";

    public override bool CanSave => false;

    public override double Width => 860;

    [ObservableProperty]
    private Sale? _sale;

    [ObservableProperty]
    private string _seller = "";

    [ObservableProperty]
    private string _customer = "عميل نقدي";

    public ObservableCollection<SaleItem> Items { get; } = [];

    public ObservableCollection<SaleRefund> Refunds { get; } = [];

    public bool HasRefunds => Refunds.Count > 0;

    public override async Task LoadAsync()
    {
        var result = await TryRunAsync(scope =>
        {
            var sale = scope.Sales.GetSale(_saleId);
            return (
                sale,
                scope.Sales.GetItems(_saleId),
                scope.Sales.RefundsFor(_saleId),
                scope.Users.Get(sale.UserId).FullName,
                sale.CustomerId is { } id ? scope.Parties.GetCustomer(id).Name : null);
        });

        if (!result.Ok)
        {
            return;
        }

        var (sale, items, refunds, seller, customer) = result.Value;
        Sale = sale;
        Seller = seller;
        Customer = customer ?? "عميل نقدي";

        foreach (var item in items)
        {
            Items.Add(item);
        }

        foreach (var refund in refunds)
        {
            Refunds.Add(refund);
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasRefunds));
    }

    public override Task<bool> SaveAsync() => Task.FromResult(true);
}

/// <summary>One invoice line in the refund dialog.</summary>
public sealed partial class RefundLine : ObservableObject
{
    public RefundLine(SaleItem item)
    {
        Item = item;
    }

    public SaleItem Item { get; }

    [ObservableProperty]
    private string _quantityText = "0";

    [ObservableProperty]
    private bool _restock = true;
}

/// <summary>
/// Returns part or all of an invoice. The invoice keeps its figures; this
/// creates a refund note with its own entry and stock movements.
/// </summary>
public sealed partial class RefundForm : FormViewModel
{
    private readonly long _saleId;

    public RefundForm(long saleId) => _saleId = saleId;

    public override string Title => Sale is null ? "استرجاع" : $"استرجاع على الفاتورة {Sale.InvoiceNumber}";

    public override string SaveLabel => "تنفيذ الاسترجاع";

    public override double Width => 900;

    [ObservableProperty]
    private Sale? _sale;

    [ObservableProperty]
    private PaymentMethod _method;

    [ObservableProperty]
    private string _reason = "";

    public ObservableCollection<RefundLine> Lines { get; } = [];

    public ObservableCollection<PaymentMethod> Methods { get; } = [];

    public string? ResultMessage { get; private set; }

    public override async Task LoadAsync()
    {
        var result = await TryRunAsync(scope => (scope.Sales.GetSale(_saleId), scope.Sales.GetItems(_saleId)));

        if (!result.Ok)
        {
            return;
        }

        var (sale, items) = result.Value;
        Sale = sale;

        foreach (var item in items.Where(i => i.RefundableQuantity.IsPositive))
        {
            Lines.Add(new RefundLine(item));
        }

        // A credit invoice is settled against the customer's account; a paid
        // one can come back in cash or to the card/bank.
        PaymentMethod[] methods = sale.PaymentMethod == PaymentMethod.Credit
            ? [PaymentMethod.Credit]
            : [PaymentMethod.Cash, PaymentMethod.Card, PaymentMethod.Transfer];

        foreach (var m in methods)
        {
            Methods.Add(m);
        }

        Method = sale.PaymentMethod;
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>Sets every line to its full remaining quantity.</summary>
    public void SelectAll()
    {
        foreach (var line in Lines)
        {
            line.QuantityText = line.Item.RefundableQuantity.ToString();
        }
    }

    public override async Task<bool> SaveAsync()
    {
        var requests = new List<RefundLineRequest>();

        foreach (var line in Lines)
        {
            if (!Quantity.TryParse(line.QuantityText, out var quantity) || quantity.IsNegative)
            {
                ErrorMessage = $"كمية غير صحيحة للصنف {line.Item.Sku}.";
                return false;
            }

            if (quantity.IsPositive)
            {
                requests.Add(new RefundLineRequest(line.Item.Id, quantity, line.Restock));
            }
        }

        if (requests.Count == 0)
        {
            ErrorMessage = "أدخل كمية الاسترجاع لصنف واحد على الأقل.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Reason))
        {
            ErrorMessage = "اكتب سبب الاسترجاع.";
            return false;
        }

        var actor = User;
        var method = Method;
        var reason = Reason;

        var refund = await RunAsync(scope => scope.Sales.Refund(actor, _saleId, requests, reason, method));

        if (refund is null)
        {
            return false;
        }

        ResultMessage = $"تم الاسترجاع {refund.RefundNumber} بمبلغ {refund.Total.ToDisplay()} ({refund.PaymentMethod.ToArabic()}).";

        if (Dialogs.Confirm($"{ResultMessage}\n\nطباعة إشعار المرتجع؟", "تم الاسترجاع", "طباعة", "لاحقاً"))
        {
            using var scope = Session.NewScope();
            Pdf.Open(new DocumentFactory(scope).Refund(refund.Id), refund.RefundNumber);
        }

        return true;
    }
}
