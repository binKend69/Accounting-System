using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Desktop.Views.Forms;

namespace MotoParts.Desktop.Views.Pages;

public sealed partial class PurchasesViewModel : ListPageViewModel
{
    public PurchasesViewModel()
    {
        Statuses =
        [
            new("كل الحالات", null),
            new(PurchaseStatus.Draft.ToArabic(), PurchaseStatus.Draft),
            new(PurchaseStatus.Received.ToArabic(), PurchaseStatus.Received),
            new(PurchaseStatus.Cancelled.ToArabic(), PurchaseStatus.Cancelled),
        ];
        _status = Statuses[0];
    }

    public override string Title => "المشتريات";

    public ObservableCollection<PurchaseRow> Purchases { get; } = [];

    public IReadOnlyList<FilterOption<PurchaseStatus?>> Statuses { get; }

    [ObservableProperty]
    private FilterOption<PurchaseStatus?> _status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(ReceiveCommand), nameof(CancelCommand))]
    private PurchaseRow? _selected;

    [ObservableProperty]
    private string _summary = "";

    public bool CanManage => Can(Permission.PurchaseManage);

    public bool CanReceive => Can(Permission.PurchaseReceive);

    partial void OnStatusChanged(FilterOption<PurchaseStatus?> value) => _ = RefreshAsync();

    protected override async Task RefreshAsync()
    {
        var (from, to, status, search) = (FromDate, ToDate, Status.Value, Search.Trim());
        var rows = await RunAsync(scope => scope.Purchases.Purchases(from, to, null, status));

        if (rows is null)
        {
            return;
        }

        var selectedId = Selected?.Id;
        Purchases.Clear();

        foreach (var row in rows.Where(r => search.Length == 0
                     || r.PurchaseNumber.Contains(search, StringComparison.OrdinalIgnoreCase)
                     || r.SupplierName.Contains(search, StringComparison.OrdinalIgnoreCase)
                     || (r.SupplierInvoiceNumber?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            Purchases.Add(row);
        }

        Selected = Purchases.FirstOrDefault(p => p.Id == selectedId);
        var received = Purchases.Where(p => p.Status == PurchaseStatus.Received).ToList();
        Summary = $"{Purchases.Count} أمر   |   المستلم {received.Sum(p => p.Total).ToDisplay()}   |   {Purchases.Count(p => p.Status == PurchaseStatus.Draft)} مسودة";
    }

    private bool HasSelection() => Selected is not null;

    private bool IsDraftSelected() => Selected is { Status: PurchaseStatus.Draft };

    [RelayCommand]
    private async Task New()
    {
        var form = new PurchaseForm(null);

        if (FormHost.Show(form))
        {
            await RefreshAsync();
            SuccessMessage = form.ResultMessage;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Open()
    {
        var form = new PurchaseForm(Selected!.Id);

        if (FormHost.Show(form))
        {
            await RefreshAsync();
            SuccessMessage = form.ResultMessage;
        }
    }

    [RelayCommand(CanExecute = nameof(IsDraftSelected))]
    private async Task Receive()
    {
        var purchase = Selected!;

        if (!Dialogs.Confirm(
                $"استلام أمر الشراء {purchase.PurchaseNumber} من {purchase.SupplierName} بقيمة {purchase.Total.ToDisplay()}؟\n" +
                "تدخل الكميات المخزون ويُسجَّل القيد المحاسبي، ولا يمكن تعديل الأمر بعد ذلك.",
                "استلام البضاعة", "استلام", "إلغاء"))
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Purchases.Receive(actor, purchase.Id)) is not null)
        {
            await RefreshAsync();
            SuccessMessage = $"اُستلم أمر الشراء {purchase.PurchaseNumber} ودخلت الكميات المخزون.";
        }
    }

    [RelayCommand(CanExecute = nameof(IsDraftSelected))]
    private async Task Cancel()
    {
        var purchase = Selected!;
        var reason = Dialogs.Prompt("إلغاء أمر الشراء", $"إلغاء المسودة {purchase.PurchaseNumber}؟ يبقى رقمها مسجلاً كملغى. اكتب السبب:");

        if (reason is null)
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Purchases.Cancel(actor, purchase.Id, reason)) is not null)
        {
            await RefreshAsync();
            SuccessMessage = $"أُلغي أمر الشراء {purchase.PurchaseNumber}.";
        }
    }
}
