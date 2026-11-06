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

public sealed partial class SuppliersViewModel : PageViewModel
{
    public override string Title => "الموردون";

    public ObservableCollection<SupplierRow> Suppliers { get; } = [];

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(StatementCommand), nameof(PayCommand), nameof(OpeningBalanceCommand))]
    private SupplierRow? _selected;

    [ObservableProperty]
    private string _summary = "";

    public bool CanManage => Can(Permission.PurchaseManage);

    public bool CanPay => Can(Permission.SupplierPay);

    public bool CanSetOpening => Can(Permission.AccountingPostManualEntry);

    partial void OnSearchChanged(string value) => _ = LoadAsync();

    public override async Task LoadAsync()
    {
        var search = Search;
        var rows = await RunAsync(scope => scope.Parties.Suppliers(search));

        if (rows is null)
        {
            return;
        }

        var selectedId = Selected?.Id;
        Suppliers.Clear();

        foreach (var row in rows)
        {
            Suppliers.Add(row);
        }

        Selected = Suppliers.FirstOrDefault(s => s.Id == selectedId);
        Summary = $"{rows.Count} مورد   |   إجمالي المستحق لهم {rows.Sum(r => r.Balance).ToDisplay()}";
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand]
    private async Task Add()
    {
        if (FormHost.Show(new SupplierForm(null)))
        {
            await LoadAsync();
            SuccessMessage = "أُضيف المورد.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Edit()
    {
        if (FormHost.Show(new SupplierForm(Selected!.Id)))
        {
            await LoadAsync();
            SuccessMessage = "حُفظت بيانات المورد.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task OpeningBalance()
    {
        var supplier = Selected!;

        if (!supplier.OpeningBalance.IsZero)
        {
            ErrorMessage = "تم إدخال الرصيد الافتتاحي لهذا المورد من قبل.";
            return;
        }

        var amount = Dialogs.PromptMoney("رصيد افتتاحي",
            $"المبلغ المستحق للمورد {supplier.Name} من قبل بدء استخدام النظام. يُسجَّل في الدفاتر مرة واحدة ولا يُعدَّل بعدها.");

        if (amount is null)
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Parties.SetSupplierOpeningBalance(supplier.Id, amount.Value, actor)) is not null)
        {
            await LoadAsync();
            SuccessMessage = $"سُجّل رصيد افتتاحي {amount.Value.ToDisplay()} للمورد {supplier.Name}.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Statement()
    {
        using var scope = Session.NewScope();
        Pdf.Open(new DocumentFactory(scope).SupplierStatement(Selected!.Id, null, null), $"كشف-{Selected.Name}");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Pay()
    {
        if (FormHost.Show(new SupplierPaymentForm(Selected!.Id)))
        {
            await LoadAsync();
            SuccessMessage = "سُجّل سند الصرف.";
        }
    }
}
