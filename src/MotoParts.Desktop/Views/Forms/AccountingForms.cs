using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;

namespace MotoParts.Desktop.Views.Forms;

/// <summary>One posted journal entry and its lines.</summary>
public sealed partial class EntryDetailsForm : FormViewModel
{
    private readonly long _entryId;

    public EntryDetailsForm(long entryId) => _entryId = entryId;

    public override string Title => Entry is null ? "القيد" : $"القيد {Entry.EntryNumber}";

    public override bool CanSave => false;

    public override double Width => 860;

    [ObservableProperty] private JournalEntry? _entry;

    public ObservableCollection<JournalLineRow> Lines { get; } = [];

    public override async Task LoadAsync()
    {
        var id = _entryId;
        var result = await TryRunAsync(scope => (scope.Accounting.GetEntry(id), scope.Chart.EntryLines(id)));

        if (!result.Ok)
        {
            return;
        }

        Entry = result.Value.Item1;

        foreach (var line in result.Value.Item2)
        {
            Lines.Add(line);
        }

        OnPropertyChanged(nameof(Title));
    }

    public override Task<bool> SaveAsync() => Task.FromResult(true);
}

public sealed partial class ManualLine : ObservableObject
{
    [ObservableProperty] private AccountRow? _account;
    [ObservableProperty] private string _debitText = "";
    [ObservableProperty] private string _creditText = "";
    [ObservableProperty] private string _memo = "";
}

/// <summary>
/// A journal entry typed in by hand: opening balances, bank deposits, accruals.
/// The running difference must reach zero before it can be posted.
/// </summary>
public sealed partial class ManualEntryForm : FormViewModel
{
    public ManualEntryForm()
    {
        Lines.CollectionChanged += (_, e) =>
        {
            foreach (ManualLine line in e.NewItems ?? Array.Empty<ManualLine>())
            {
                line.PropertyChanged += OnLineChanged;
            }

            Recalculate();
        };

        Lines.Add(new ManualLine());
        Lines.Add(new ManualLine());
    }

    public override string Title => "قيد يومية يدوي";

    public override string SaveLabel => "ترحيل القيد";

    public override double Width => 960;

    public ObservableCollection<AccountRow> Accounts { get; } = [];

    public ObservableCollection<ManualLine> Lines { get; } = [];

    [ObservableProperty] private DateTime? _entryDate = AppSession.Current.Clock.Today.ToDateTime(TimeOnly.MinValue);
    [ObservableProperty] private string _memo = "";
    [ObservableProperty] private bool _isOpeningBalance;
    [ObservableProperty] private Money _totalDebit;
    [ObservableProperty] private Money _totalCredit;
    [ObservableProperty] private Money _difference;
    [ObservableProperty] private bool _isBalanced;

    public string? ResultMessage { get; private set; }

    public override async Task LoadAsync()
    {
        var accounts = await RunAsync(scope => scope.Chart.Accounts(activeOnly: true));

        foreach (var account in accounts ?? [])
        {
            Accounts.Add(account);
        }
    }

    [RelayCommand]
    private void AddLine() => Lines.Add(new ManualLine());

    [RelayCommand]
    private void RemoveLine(ManualLine? line)
    {
        if (line is not null && Lines.Count > 2)
        {
            Lines.Remove(line);
            Recalculate();
        }
    }

    public override async Task<bool> SaveAsync()
    {
        if (EntryDate is null)
        {
            ErrorMessage = "اختر تاريخ القيد.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Memo))
        {
            ErrorMessage = "اكتب بيان القيد.";
            return false;
        }

        var lines = new List<LedgerLine>();

        foreach (var line in Lines)
        {
            var hasDebit = Money.TryParse(line.DebitText, out var debit) && debit.IsPositive;
            var hasCredit = Money.TryParse(line.CreditText, out var credit) && credit.IsPositive;

            if (!hasDebit && !hasCredit && line.Account is null)
            {
                continue;
            }

            if (line.Account is null)
            {
                ErrorMessage = "اختر الحساب في كل سطر به مبلغ.";
                return false;
            }

            if (hasDebit == hasCredit)
            {
                ErrorMessage = $"السطر الخاص بالحساب {line.Account.Label} يجب أن يكون مديناً أو دائناً، وليس الاثنين أو لا شيء.";
                return false;
            }

            lines.Add(hasDebit
                ? LedgerLine.Debit(line.Account.Id, debit, NullIfEmpty(line.Memo))
                : LedgerLine.Credit(line.Account.Id, credit, NullIfEmpty(line.Memo)));
        }

        var (actor, date, memo, opening) = (User, DateOnly.FromDateTime(EntryDate.Value), Memo, IsOpeningBalance);
        var entry = await RunAsync(scope => scope.Accounting.PostManual(actor, date, memo, lines, opening));

        if (entry is null)
        {
            return false;
        }

        ResultMessage = $"رُحّل القيد {entry.EntryNumber} بمبلغ {entry.TotalDebit.ToDisplay()}.";
        return true;
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e) => Recalculate();

    private void Recalculate()
    {
        TotalDebit = Lines.Sum(l => Money.TryParse(l.DebitText, out var d) ? d : Money.Zero);
        TotalCredit = Lines.Sum(l => Money.TryParse(l.CreditText, out var c) ? c : Money.Zero);
        Difference = TotalDebit - TotalCredit;
        IsBalanced = Difference.IsZero && TotalDebit.IsPositive;
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>An account in the chart.</summary>
public sealed partial class AccountForm : FormViewModel
{
    private readonly long? _id;

    public AccountForm(long? id)
    {
        _id = id;
        Types = Enum.GetValues<AccountType>().Select(t => new FilterOption<AccountType?>(t.ToArabic(), t)).ToList();
        _type = Types.First(t => t.Value == AccountType.Expense);
    }

    public override string Title => _id is null ? "حساب جديد" : "تعديل الحساب";

    public IReadOnlyList<FilterOption<AccountType?>> Types { get; }

    public ObservableCollection<FilterOption<long?>> Parents { get; } = [];

    [ObservableProperty] private string _code = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private FilterOption<AccountType?> _type;
    [ObservableProperty] private FilterOption<long?>? _parent;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private bool _isSystem;

    public override async Task LoadAsync()
    {
        var id = _id;
        var result = await TryRunAsync(scope => (scope.Chart.Accounts(), id is { } aid ? scope.Chart.Get(aid) : null));

        if (!result.Ok)
        {
            return;
        }

        var (accounts, account) = result.Value;

        Parents.Add(new FilterOption<long?>("بدون حساب أب", null));

        foreach (var row in accounts.Where(a => a.Id != id))
        {
            Parents.Add(new FilterOption<long?>(row.Label, row.Id));
        }

        Parent = Parents[0];

        if (account is null)
        {
            return;
        }

        Code = account.Code;
        Name = account.Name;
        Type = Types.First(t => t.Value == account.Type);
        Parent = Parents.FirstOrDefault(p => p.Value == account.ParentId) ?? Parents[0];
        Description = account.Description ?? "";
        IsActive = account.IsActive;
        IsSystem = account.IsSystem;
    }

    public override async Task<bool> SaveAsync()
    {
        var input = new AccountInput(Code, Name, Type.Value!.Value, Parent?.Value, Description, IsActive);
        var (actor, id) = (User, _id);

        return await RunAsync(scope => id is { } aid ? scope.Chart.Update(aid, input, actor) : scope.Chart.Create(input, actor)) is not null;
    }
}
