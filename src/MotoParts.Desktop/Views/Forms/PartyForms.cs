using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Documents;

namespace MotoParts.Desktop.Views.Forms;

/// <summary>A customer's card: contact details and credit limit.</summary>
public sealed partial class CustomerForm : FormViewModel
{
    private readonly long? _id;

    public CustomerForm(long? id) => _id = id;

    public override string Title => _id is null ? "عميل جديد" : "تعديل بيانات العميل";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _taxNumber = "";
    [ObservableProperty] private string _creditLimitText = "0";
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string _notes = "";

    public override async Task LoadAsync()
    {
        if (_id is not { } id)
        {
            return;
        }

        var customer = await RunAsync(scope => scope.Parties.GetCustomer(id));

        if (customer is null)
        {
            return;
        }

        Name = customer.Name;
        Phone = customer.Phone ?? "";
        Email = customer.Email ?? "";
        Address = customer.Address ?? "";
        TaxNumber = customer.TaxNumber ?? "";
        CreditLimitText = customer.CreditLimit.ToString();
        IsActive = customer.IsActive;
        Notes = customer.Notes ?? "";
    }

    public override async Task<bool> SaveAsync()
    {
        if (!Money.TryParse(CreditLimitText, out var limit))
        {
            ErrorMessage = "حد الائتمان غير صحيح. أدخل 0 للعميل النقدي.";
            return false;
        }

        var input = new CustomerInput(Name, Phone, Email, Address, TaxNumber, limit, IsActive, Notes);
        var actor = User;
        var id = _id;

        return await RunAsync(scope => scope.Parties.SaveCustomer(id, input, actor)) is not null;
    }
}

/// <summary>A supplier's card.</summary>
public sealed partial class SupplierForm : FormViewModel
{
    private readonly long? _id;

    public SupplierForm(long? id) => _id = id;

    public override string Title => _id is null ? "مورد جديد" : "تعديل بيانات المورد";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _contactPerson = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _taxNumber = "";
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string _notes = "";

    public override async Task LoadAsync()
    {
        if (_id is not { } id)
        {
            return;
        }

        var supplier = await RunAsync(scope => scope.Parties.GetSupplier(id));

        if (supplier is null)
        {
            return;
        }

        Name = supplier.Name;
        ContactPerson = supplier.ContactPerson ?? "";
        Phone = supplier.Phone ?? "";
        Email = supplier.Email ?? "";
        Address = supplier.Address ?? "";
        TaxNumber = supplier.TaxNumber ?? "";
        IsActive = supplier.IsActive;
        Notes = supplier.Notes ?? "";
    }

    public override async Task<bool> SaveAsync()
    {
        var input = new SupplierInput(Name, ContactPerson, Phone, Email, Address, TaxNumber, IsActive, Notes);
        var actor = User;
        var id = _id;

        return await RunAsync(scope => scope.Parties.SaveSupplier(id, input, actor)) is not null;
    }
}

/// <summary>
/// The fields every money voucher shares: amount, method, whether cash goes
/// through the operator's till drawer, and a reference.
/// </summary>
public abstract partial class VoucherFormBase : FormViewModel
{
    protected VoucherFormBase()
    {
        Methods = [PaymentMethod.Cash, PaymentMethod.Card, PaymentMethod.Transfer];
    }

    public IReadOnlyList<PaymentMethod> Methods { get; }

    [ObservableProperty]
    private string _amountText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseDrawer))]
    private PaymentMethod _method = PaymentMethod.Cash;

    [ObservableProperty]
    private bool _throughDrawer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseDrawer))]
    private bool _hasOpenTill;

    [ObservableProperty]
    private string _reference = "";

    public bool CanUseDrawer => HasOpenTill && Method == PaymentMethod.Cash;

    public abstract string DrawerLabel { get; }

    partial void OnMethodChanged(PaymentMethod value)
    {
        if (value != PaymentMethod.Cash)
        {
            ThroughDrawer = false;
        }
    }

    protected async Task LoadTillAsync()
    {
        var userId = User.Id;
        var (ok, open) = await TryRunAsync(scope => scope.Shifts.OpenSessionFor(userId) is not null);
        HasOpenTill = ok && open;
        ThroughDrawer = CanUseDrawer;
    }

    protected bool TryAmount(out Money amount)
    {
        if (!Money.TryParse(AmountText, out amount) || !amount.IsPositive)
        {
            ErrorMessage = "أدخل مبلغاً صحيحاً أكبر من صفر.";
            return false;
        }

        return true;
    }
}

/// <summary>سند قبض: a customer settling (part of) their account.</summary>
public sealed partial class CustomerReceiptForm : VoucherFormBase
{
    public CustomerReceiptForm(long? customerId)
    {
        _preselectedId = customerId;
    }

    private readonly long? _preselectedId;

    public override string Title => "سند قبض من عميل";

    public override string SaveLabel => "حفظ السند";

    public override string DrawerLabel => "استلمت المبلغ نقداً في درج ورديتي (يُحسب في مطابقة الصندوق)";

    public ObservableCollection<CustomerRow> Customers { get; } = [];

    [ObservableProperty]
    private CustomerRow? _customer;

    [ObservableProperty]
    private string _notes = "";

    public override async Task LoadAsync()
    {
        var rows = await RunAsync(scope => scope.Parties.Customers(activeOnly: false, withBalanceOnly: true));

        foreach (var row in rows ?? [])
        {
            Customers.Add(row);
        }

        Customer = Customers.FirstOrDefault(c => c.Id == _preselectedId);

        if (Customer is not null)
        {
            AmountText = Customer.Balance.ToString();
        }

        await LoadTillAsync();
    }

    partial void OnCustomerChanged(CustomerRow? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(AmountText))
        {
            AmountText = value.Balance.ToString();
        }
    }

    public override async Task<bool> SaveAsync()
    {
        if (Customer is null)
        {
            ErrorMessage = "اختر العميل.";
            return false;
        }

        if (!TryAmount(out var amount))
        {
            return false;
        }

        var (actor, customerId, method, drawer, reference, notes) = (User, Customer.Id, Method, ThroughDrawer, Reference, Notes);
        var receipt = await RunAsync(scope => scope.Vouchers.ReceiveFromCustomer(actor, customerId, amount, method, drawer, reference, notes));

        if (receipt is null)
        {
            return false;
        }

        if (Dialogs.Confirm($"سُجّل سند القبض {receipt.ReceiptNumber} بمبلغ {receipt.Amount.ToDisplay()}.\nطباعة السند؟", "تم", "طباعة", "لاحقاً"))
        {
            using var scope = Session.NewScope();
            Pdf.Open(new DocumentFactory(scope).CustomerReceipt(receipt.Id), receipt.ReceiptNumber);
        }

        return true;
    }
}

/// <summary>سند صرف: paying a supplier.</summary>
public sealed partial class SupplierPaymentForm : VoucherFormBase
{
    private readonly long? _preselectedId;

    public SupplierPaymentForm(long? supplierId)
    {
        _preselectedId = supplierId;
        Method = PaymentMethod.Transfer;
    }

    public override string Title => "سند صرف لمورد";

    public override string SaveLabel => "حفظ السند";

    public override string DrawerLabel => "دفعت المبلغ نقداً من درج ورديتي (يُحسب في مطابقة الصندوق)";

    public ObservableCollection<SupplierRow> Suppliers { get; } = [];

    [ObservableProperty]
    private SupplierRow? _supplier;

    [ObservableProperty]
    private string _notes = "";

    public override async Task LoadAsync()
    {
        var rows = await RunAsync(scope => scope.Parties.Suppliers());

        foreach (var row in (rows ?? []).Where(r => r.Balance.IsPositive))
        {
            Suppliers.Add(row);
        }

        Supplier = Suppliers.FirstOrDefault(s => s.Id == _preselectedId);

        if (Supplier is not null)
        {
            AmountText = Supplier.Balance.ToString();
        }

        await LoadTillAsync();
        ThroughDrawer = false;
    }

    partial void OnSupplierChanged(SupplierRow? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(AmountText))
        {
            AmountText = value.Balance.ToString();
        }
    }

    public override async Task<bool> SaveAsync()
    {
        if (Supplier is null)
        {
            ErrorMessage = "اختر المورد.";
            return false;
        }

        if (!TryAmount(out var amount))
        {
            return false;
        }

        var (actor, supplierId, method, drawer, reference, notes) = (User, Supplier.Id, Method, ThroughDrawer, Reference, Notes);
        var payment = await RunAsync(scope => scope.Vouchers.PaySupplier(actor, supplierId, amount, method, drawer, reference, notes));

        if (payment is null)
        {
            return false;
        }

        if (Dialogs.Confirm($"سُجّل سند الصرف {payment.PaymentNumber} بمبلغ {payment.Amount.ToDisplay()}.\nطباعة السند؟", "تم", "طباعة", "لاحقاً"))
        {
            using var scope = Session.NewScope();
            Pdf.Open(new DocumentFactory(scope).SupplierPayment(payment.Id), payment.PaymentNumber);
        }

        return true;
    }
}

/// <summary>سند مصروف: rent, electricity, transport...</summary>
public sealed partial class ExpenseForm : VoucherFormBase
{
    public override string Title => "سند مصروف";

    public override string SaveLabel => "حفظ السند";

    public override string DrawerLabel => "دفعت المبلغ نقداً من درج ورديتي (يُحسب في مطابقة الصندوق)";

    public ObservableCollection<Account> Accounts { get; } = [];

    [ObservableProperty]
    private Account? _account;

    [ObservableProperty]
    private string _description = "";

    [ObservableProperty]
    private string _payee = "";

    public override async Task LoadAsync()
    {
        var accounts = await RunAsync(scope => scope.Vouchers.ExpenseAccounts());

        foreach (var account in accounts ?? [])
        {
            Accounts.Add(account);
        }

        await LoadTillAsync();
    }

    public override async Task<bool> SaveAsync()
    {
        if (Account is null)
        {
            ErrorMessage = "اختر نوع المصروف.";
            return false;
        }

        if (!TryAmount(out var amount))
        {
            return false;
        }

        var (actor, accountId, method, drawer, description, payee) = (User, Account.Id, Method, ThroughDrawer, Description, Payee);
        var voucher = await RunAsync(scope => scope.Vouchers.RecordExpense(actor, accountId, amount, method, drawer, description, payee));

        if (voucher is null)
        {
            return false;
        }

        if (Dialogs.Confirm($"سُجّل سند المصروف {voucher.VoucherNumber} بمبلغ {voucher.Amount.ToDisplay()}.\nطباعة السند؟", "تم", "طباعة", "لاحقاً"))
        {
            using var scope = Session.NewScope();
            Pdf.Open(new DocumentFactory(scope).Expense(voucher.Id), voucher.VoucherNumber);
        }

        return true;
    }
}
