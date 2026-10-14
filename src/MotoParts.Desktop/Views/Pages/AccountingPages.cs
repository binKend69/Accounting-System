using System.Collections.ObjectModel;
using System.Globalization;
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

public sealed partial class JournalViewModel : ListPageViewModel
{
    public JournalViewModel()
    {
        Sources =
        [
            new("كل المصادر", null),
            .. Enum.GetValues<JournalSource>().Select(s => new FilterOption<JournalSource?>(s.ToArabic(), s)),
        ];
        _source = Sources[0];
    }

    public override string Title => "القيود اليومية";

    public ObservableCollection<JournalEntryRow> Entries { get; } = [];

    public IReadOnlyList<FilterOption<JournalSource?>> Sources { get; }

    [ObservableProperty]
    private FilterOption<JournalSource?> _source;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ViewCommand), nameof(ReverseCommand))]
    private JournalEntryRow? _selected;

    public bool CanPostManual => Can(Permission.AccountingPostManualEntry);

    public bool CanReverse => Can(Permission.AccountingReverseEntry);

    partial void OnSourceChanged(FilterOption<JournalSource?> value) => _ = RefreshAsync();

    protected override async Task RefreshAsync()
    {
        var (from, to, source, search) = (FromDate, ToDate, Source.Value, Search);
        var rows = await RunAsync(scope => scope.Chart.Entries(from, to, source, search));

        if (rows is null)
        {
            return;
        }

        var selectedId = Selected?.Id;
        Entries.Clear();

        foreach (var row in rows)
        {
            Entries.Add(row);
        }

        Selected = Entries.FirstOrDefault(e => e.Id == selectedId);
    }

    private bool HasSelection() => Selected is not null;

    private bool CanReverseSelected() => Selected is { CanBeReversedByHand: true };

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void View() => FormHost.Show(new EntryDetailsForm(Selected!.Id));

    [RelayCommand]
    private async Task NewEntry()
    {
        var form = new ManualEntryForm();

        if (FormHost.Show(form))
        {
            await RefreshAsync();
            SuccessMessage = form.ResultMessage;
        }
    }

    [RelayCommand(CanExecute = nameof(CanReverseSelected))]
    private async Task Reverse()
    {
        var entry = Selected!;
        var memo = Dialogs.Prompt("عكس قيد",
            $"عكس القيد {entry.EntryNumber} ({entry.Memo})؟\nيُسجَّل قيد معاكس بتاريخ اليوم ويبقى الأصل كما هو. اكتب سبب العكس:");

        if (memo is null)
        {
            return;
        }

        var actor = User;
        var today = AppSession.Current.Clock.Today;
        var reversal = await RunAsync(scope => scope.Accounting.ReverseManual(actor, entry.Id, today, $"عكس القيد {entry.EntryNumber}: {memo}"));

        if (reversal is not null)
        {
            await RefreshAsync();
            SuccessMessage = $"سُجّل القيد العكسي {reversal.EntryNumber}.";
        }
    }
}

public sealed partial class AccountsViewModel : PageViewModel
{
    public override string Title => "دليل الحسابات";

    public ObservableCollection<AccountRow> Accounts { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand), nameof(LedgerCommand))]
    private AccountRow? _selected;

    public bool CanManage => Can(Permission.AccountingManageAccounts);

    public override async Task LoadAsync()
    {
        var rows = await RunAsync(scope => scope.Chart.Accounts());

        if (rows is null)
        {
            return;
        }

        var selectedId = Selected?.Id;
        Accounts.Clear();

        foreach (var row in rows)
        {
            Accounts.Add(row);
        }

        Selected = Accounts.FirstOrDefault(a => a.Id == selectedId);
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand]
    private async Task Add()
    {
        if (FormHost.Show(new AccountForm(null)))
        {
            await LoadAsync();
            SuccessMessage = "أُضيف الحساب.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Edit()
    {
        if (FormHost.Show(new AccountForm(Selected!.Id)))
        {
            await LoadAsync();
            SuccessMessage = "حُفظ الحساب.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Delete()
    {
        var account = Selected!;

        if (!Dialogs.Confirm($"حذف الحساب {account.Label}؟ يُسمح فقط بحذف حساب لم يُستخدم.", "حذف حساب", "حذف", "إلغاء", danger: true))
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Chart.Delete(account.Id, actor)))
        {
            await LoadAsync();
            SuccessMessage = "حُذف الحساب.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Ledger()
    {
        using var scope = Session.NewScope();
        Pdf.Open(new DocumentFactory(scope).GeneralLedger(Selected!.Id, null, null), $"أستاذ-{Selected.Code}");
    }
}

public sealed partial class FiscalPeriodsViewModel : PageViewModel
{
    public override string Title => "الفترات المحاسبية";

    public ObservableCollection<FiscalPeriod> Periods { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LockCommand), nameof(UnlockCommand))]
    private FiscalPeriod? _selected;

    public bool CanLock => Can(Permission.FiscalPeriodLock);

    public bool CanUnlock => Can(Permission.FiscalPeriodUnlock);

    public bool CanManage => Can(Permission.FiscalPeriodManage);

    public override async Task LoadAsync()
    {
        var rows = await RunAsync(scope => scope.FiscalPeriods.All());

        if (rows is null)
        {
            return;
        }

        var selectedId = Selected?.Id;
        Periods.Clear();

        foreach (var row in rows)
        {
            Periods.Add(row);
        }

        Selected = Periods.FirstOrDefault(p => p.Id == selectedId);
    }

    private bool IsOpenSelected() => Selected is { IsLocked: false };

    private bool IsLockedSelected() => Selected is { IsLocked: true };

    [RelayCommand]
    private async Task GenerateYear()
    {
        var text = Dialogs.Prompt("إنشاء فترات سنة", "السنة (مثل 2027): تُنشأ الأشهر الاثنا عشر غير الموجودة.",
            (AppSession.Current.Clock.Today.Year + 1).ToString(CultureInfo.InvariantCulture), multiline: false);

        if (text is null)
        {
            return;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) || year is < 2000 or > 2100)
        {
            ErrorMessage = "سنة غير صحيحة.";
            return;
        }

        var actor = User;
        var (ok, created) = await TryRunAsync(scope => scope.FiscalPeriods.CreateMonthsForYear(year, actor));

        if (ok)
        {
            await LoadAsync();
            SuccessMessage = created == 0 ? $"فترات سنة {year} موجودة من قبل." : $"أُنشئت {created} فترة لسنة {year}.";
        }
    }

    [RelayCommand(CanExecute = nameof(IsOpenSelected))]
    private async Task Lock()
    {
        var period = Selected!;
        var note = Dialogs.Prompt("إقفال الفترة",
            $"إقفال الفترة {period.Name}؟ بعد الإقفال لا يمكن تسجيل أو تعديل أي قيد في تواريخها (بيع، شراء، سندات...).\nملاحظة الإقفال (اختياري):",
            required: false);

        if (note is null)
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.FiscalPeriods.Lock(period.Id, actor, note)) is not null)
        {
            await LoadAsync();
            SuccessMessage = $"أُقفلت الفترة {period.Name}.";
        }
    }

    [RelayCommand(CanExecute = nameof(IsLockedSelected))]
    private async Task Unlock()
    {
        var period = Selected!;
        var reason = Dialogs.Prompt("إعادة فتح الفترة",
            $"إعادة فتح الفترة المقفلة {period.Name} تسمح بتغيير أرقام سابقة، وتُسجَّل باسمك في سجل التدقيق. اكتب السبب:");

        if (reason is null)
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.FiscalPeriods.Unlock(period.Id, actor, reason)) is not null)
        {
            await LoadAsync();
            SuccessMessage = $"أُعيد فتح الفترة {period.Name}.";
        }
    }
}
