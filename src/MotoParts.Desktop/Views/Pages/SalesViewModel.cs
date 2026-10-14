using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Desktop.Views.Forms;
using MotoParts.Documents;

namespace MotoParts.Desktop.Views.Pages;

public sealed partial class SalesViewModel : ListPageViewModel
{
    public SalesViewModel()
    {
        PaymentFilters =
        [
            new("كل طرق الدفع", null),
            .. new[] { PaymentMethod.Cash, PaymentMethod.Card, PaymentMethod.Transfer, PaymentMethod.Credit }
                .Select(m => new FilterOption<PaymentMethod?>(m.ToArabic(), m)),
        ];
        _paymentFilter = PaymentFilters[0];
    }

    public override string Title => "الفواتير";

    public ObservableCollection<SaleRow> Sales { get; } = [];

    public IReadOnlyList<FilterOption<PaymentMethod?>> PaymentFilters { get; }

    public ObservableCollection<FilterOption<long?>> Employees { get; } = [];

    [ObservableProperty]
    private FilterOption<PaymentMethod?> _paymentFilter;

    [ObservableProperty]
    private FilterOption<long?>? _employee;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ViewDetailsCommand), nameof(PrintA4Command), nameof(PrintReceiptCommand), nameof(RefundCommand))]
    private SaleRow? _selected;

    [ObservableProperty]
    private string _summary = "";

    public bool SeesEveryone => Can(Permission.SaleViewAll);

    public bool CanRefund => Can(Permission.SaleRefund);

    partial void OnPaymentFilterChanged(FilterOption<PaymentMethod?> value) => _ = RefreshAsync();

    private bool _suppressRefresh;

    partial void OnEmployeeChanged(FilterOption<long?>? value)
    {
        if (!_suppressRefresh)
        {
            _ = RefreshAsync();
        }
    }

    protected override async Task RefreshAsync()
    {
        var viewer = User;
        var filter = new SaleFilter(FromDate, ToDate, Employee?.Value, null, PaymentFilter.Value, Search);
        var loadEmployees = SeesEveryone && Employees.Count == 0;

        var result = await TryRunAsync(scope =>
        {
            var rows = scope.Sales.Sales(viewer, filter);
            var users = loadEmployees ? scope.Users.All() : [];
            return (rows, users);
        });

        if (!result.Ok)
        {
            return;
        }

        if (loadEmployees)
        {
            Employees.Add(new FilterOption<long?>("كل الموظفين", null));

            foreach (var user in result.Value.users)
            {
                Employees.Add(new FilterOption<long?>(user.FullName, user.Id));
            }

            // Set without triggering a second refresh: this one is already running.
            _suppressRefresh = true;
            Employee = Employees[0];
            _suppressRefresh = false;
        }

        var selectedId = Selected?.Id;
        Sales.Clear();

        foreach (var row in result.Value.rows)
        {
            Sales.Add(row);
        }

        Selected = Sales.FirstOrDefault(s => s.Id == selectedId);

        var net = result.Value.rows.Sum(r => r.Total) - result.Value.rows.Sum(r => r.RefundedTotal);
        Summary = $"{result.Value.rows.Count} فاتورة   |   الإجمالي {result.Value.rows.Sum(r => r.Total).ToDisplay()}   |   بعد المرتجعات {net.ToDisplay()}";
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ViewDetails() => FormHost.Show(new SaleDetailsForm(Selected!.Id));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PrintA4() => Print(InvoiceFormat.A4);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void PrintReceipt() => Print(InvoiceFormat.Receipt80mm);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Refund()
    {
        if (Selected!.Status == SaleStatus.Refunded)
        {
            ErrorMessage = "هذه الفاتورة مسترجعة بالكامل من قبل.";
            return;
        }

        var form = new RefundForm(Selected.Id);

        if (FormHost.Show(form))
        {
            await RefreshAsync();
            SuccessMessage = form.ResultMessage;
        }
    }

    private void Print(InvoiceFormat format)
    {
        using var scope = Session.NewScope();
        Pdf.Open(new DocumentFactory(scope).Invoice(Selected!.Id, format), Selected.InvoiceNumber);
    }
}
