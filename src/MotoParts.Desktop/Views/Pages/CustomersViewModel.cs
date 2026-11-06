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

public sealed partial class CustomersViewModel : PageViewModel
{
    public override string Title => "العملاء";

    public ObservableCollection<CustomerRow> Customers { get; } = [];

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private bool _withBalanceOnly;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(StatementCommand), nameof(ReceiveCommand), nameof(OpeningBalanceCommand))]
    private CustomerRow? _selected;

    [ObservableProperty]
    private string _summary = "";

    public bool CanReceive => Can(Permission.CustomerReceive);

    public bool CanSetOpening => Can(Permission.AccountingPostManualEntry);

    partial void OnSearchChanged(string value) => _ = LoadAsync();

    partial void OnWithBalanceOnlyChanged(bool value) => _ = LoadAsync();

    public override async Task LoadAsync()
    {
        var (search, balanceOnly) = (Search, WithBalanceOnly);
        var rows = await RunAsync(scope => scope.Parties.Customers(search, withBalanceOnly: balanceOnly));

        if (rows is null)
        {
            return;
        }

        var selectedId = Selected?.Id;
        Customers.Clear();

        foreach (var row in rows)
        {
            Customers.Add(row);
        }

        Selected = Customers.FirstOrDefault(c => c.Id == selectedId);
        Summary = $"{rows.Count} عميل   |   إجمالي المستحق {rows.Sum(r => r.Balance).ToDisplay()}";
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand]
    private async Task Add()
    {
        if (FormHost.Show(new CustomerForm(null)))
        {
            await LoadAsync();
            SuccessMessage = "أُضيف العميل.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Edit()
    {
        if (FormHost.Show(new CustomerForm(Selected!.Id)))
        {
            await LoadAsync();
            SuccessMessage = "حُفظت بيانات العميل.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task OpeningBalance()
    {
        var customer = Selected!;

        if (!customer.OpeningBalance.IsZero)
        {
            ErrorMessage = "تم إدخال الرصيد الافتتاحي لهذا العميل من قبل.";
            return;
        }

        var amount = Dialogs.PromptMoney("رصيد افتتاحي",
            $"المبلغ المستحق على {customer.Name} من قبل بدء استخدام النظام. يُسجَّل في الدفاتر مرة واحدة ولا يُعدَّل بعدها.");

        if (amount is null)
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Parties.SetCustomerOpeningBalance(customer.Id, amount.Value, actor)) is not null)
        {
            await LoadAsync();
            SuccessMessage = $"سُجّل رصيد افتتاحي {amount.Value.ToDisplay()} للعميل {customer.Name}.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Statement()
    {
        using var scope = Session.NewScope();
        Pdf.Open(new DocumentFactory(scope).CustomerStatement(Selected!.Id, null, null), $"كشف-{Selected.Name}");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Receive()
    {
        if (FormHost.Show(new CustomerReceiptForm(Selected!.Id)))
        {
            await LoadAsync();
            SuccessMessage = "سُجّل سند القبض.";
        }
    }
}
