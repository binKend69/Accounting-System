using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuestPDF.Infrastructure;
using MotoParts.Core;
using MotoParts.Core.Common;
using MotoParts.Core.Security;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Desktop.Views.Forms;
using MotoParts.Documents;

namespace MotoParts.Desktop.Views.Pages;

/// <summary>
/// A list of money vouchers: new, print, void. Receipts, supplier payments and
/// expenses differ only in what they list, which form creates one and how a
/// void is recorded.
/// </summary>
public abstract partial class VoucherListViewModel : ListPageViewModel
{
    public ObservableCollection<VoucherRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand), nameof(VoidCommand))]
    private VoucherRow? _selected;

    [ObservableProperty]
    private string _summary = "";

    public abstract string NewLabel { get; }

    public abstract string PartyHeader { get; }

    public bool CanVoid => Can(Permission.VoucherVoid);

    protected abstract IReadOnlyList<VoucherRow> Query(ServiceScope scope, DateOnly? from, DateOnly? to);

    protected abstract FormViewModel NewForm();

    protected abstract IDocument Document(DocumentFactory documents, long id);

    protected abstract void VoidVoucher(ServiceScope scope, Actor actor, long id, string reason);

    protected override async Task RefreshAsync()
    {
        var (from, to) = (FromDate, ToDate);
        var search = Search.Trim();
        var rows = await RunAsync(scope => Query(scope, from, to));

        if (rows is null)
        {
            return;
        }

        Rows.Clear();

        foreach (var row in rows.Where(r => search.Length == 0
                     || r.Number.Contains(search, StringComparison.OrdinalIgnoreCase)
                     || r.PartyName.Contains(search, StringComparison.OrdinalIgnoreCase)
                     || (r.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            Rows.Add(row);
        }

        var live = Rows.Where(r => !r.IsVoided).ToList();
        Summary = $"{live.Count} سند   |   الإجمالي {live.Sum(r => r.Amount).ToDisplay()}" +
                  (Rows.Count > live.Count ? $"   |   {Rows.Count - live.Count} ملغى" : "");
    }

    [RelayCommand]
    private async Task New()
    {
        if (FormHost.Show(NewForm()))
        {
            await RefreshAsync();
            SuccessMessage = "سُجّل السند.";
        }
    }

    private bool HasSelection() => Selected is not null;

    private bool CanVoidSelected() => Selected is { IsVoided: false };

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Print()
    {
        using var scope = Session.NewScope();
        Pdf.Open(Document(new DocumentFactory(scope), Selected!.Id), Selected.Number);
    }

    [RelayCommand(CanExecute = nameof(CanVoidSelected))]
    private async Task Void()
    {
        var voucher = Selected!;
        var reason = Dialogs.Prompt("إلغاء السند",
            $"إلغاء السند {voucher.Number} بمبلغ {voucher.Amount.ToDisplay()}؟\nيُسجَّل قيد عكسي ويبقى السند ظاهراً كملغى. اكتب سبب الإلغاء:");

        if (reason is null)
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => VoidVoucher(scope, actor, voucher.Id, reason)))
        {
            await RefreshAsync();
            SuccessMessage = $"أُلغي السند {voucher.Number}.";
        }
    }
}

public sealed class ReceiptsViewModel : VoucherListViewModel
{
    public override string Title => "سندات القبض";

    public override string NewLabel => "سند قبض جديد";

    public override string PartyHeader => "العميل";

    protected override IReadOnlyList<VoucherRow> Query(ServiceScope scope, DateOnly? from, DateOnly? to) =>
        scope.Vouchers.CustomerReceipts(from, to);

    protected override FormViewModel NewForm() => new CustomerReceiptForm(null);

    protected override IDocument Document(DocumentFactory documents, long id) => documents.CustomerReceipt(id);

    protected override void VoidVoucher(ServiceScope scope, Actor actor, long id, string reason) =>
        scope.Vouchers.VoidCustomerReceipt(actor, id, reason);
}

public sealed class SupplierPaymentsViewModel : VoucherListViewModel
{
    public override string Title => "سندات الصرف للموردين";

    public override string NewLabel => "سند صرف جديد";

    public override string PartyHeader => "المورد";

    protected override IReadOnlyList<VoucherRow> Query(ServiceScope scope, DateOnly? from, DateOnly? to) =>
        scope.Vouchers.SupplierPayments(from, to);

    protected override FormViewModel NewForm() => new SupplierPaymentForm(null);

    protected override IDocument Document(DocumentFactory documents, long id) => documents.SupplierPayment(id);

    protected override void VoidVoucher(ServiceScope scope, Actor actor, long id, string reason) =>
        scope.Vouchers.VoidSupplierPayment(actor, id, reason);
}

public sealed class ExpensesViewModel : VoucherListViewModel
{
    public override string Title => "المصروفات";

    public override string NewLabel => "سند مصروف جديد";

    public override string PartyHeader => "نوع المصروف";

    protected override IReadOnlyList<VoucherRow> Query(ServiceScope scope, DateOnly? from, DateOnly? to) =>
        scope.Vouchers.Expenses(from, to);

    protected override FormViewModel NewForm() => new ExpenseForm();

    protected override IDocument Document(DocumentFactory documents, long id) => documents.Expense(id);

    protected override void VoidVoucher(ServiceScope scope, Actor actor, long id, string reason) =>
        scope.Vouchers.VoidExpense(actor, id, reason);
}
