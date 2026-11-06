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

/// <summary>Every till session, for the owner: reports, force-close and recount corrections.</summary>
public sealed partial class ShiftSessionsViewModel : ListPageViewModel
{
    public override string Title => "سجل الورديات";

    public ObservableCollection<ShiftSessionRow> Sessions { get; } = [];

    public ObservableCollection<FilterOption<long?>> Employees { get; } = [];

    [ObservableProperty]
    private FilterOption<long?>? _employee;

    [ObservableProperty]
    private bool _differencesOnly;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReportCommand), nameof(ForceCloseCommand), nameof(AmendCommand))]
    private ShiftSessionRow? _selected;

    [ObservableProperty]
    private string _summary = "";

    private bool _loadingEmployees;

    public bool CanOverride => Can(Permission.ShiftForceClose);

    partial void OnEmployeeChanged(FilterOption<long?>? value)
    {
        if (!_loadingEmployees)
        {
            _ = RefreshAsync();
        }
    }

    partial void OnDifferencesOnlyChanged(bool value) => _ = RefreshAsync();

    protected override async Task RefreshAsync()
    {
        var (from, to, userId, diffOnly) = (FromDate, ToDate, Employee?.Value, DifferencesOnly);
        var loadEmployees = Employees.Count == 0;

        var result = await TryRunAsync(scope => (scope.Shifts.Sessions(from, to, userId, diffOnly), loadEmployees ? scope.Users.All() : []));

        if (!result.Ok)
        {
            return;
        }

        if (loadEmployees)
        {
            _loadingEmployees = true;
            Employees.Add(new FilterOption<long?>("كل الموظفين", null));

            foreach (var user in result.Value.Item2)
            {
                Employees.Add(new FilterOption<long?>(user.FullName, user.Id));
            }

            Employee = Employees[0];
            _loadingEmployees = false;
        }

        var selectedId = Selected?.Id;
        Sessions.Clear();

        foreach (var row in result.Value.Item1)
        {
            Sessions.Add(row);
        }

        Selected = Sessions.FirstOrDefault(s => s.Id == selectedId);
        var net = Sessions.Where(s => s.CashDifference is not null).Sum(s => s.CashDifference!.Value);
        Summary = $"{Sessions.Count} وردية   |   صافي الفروقات {net.ToDisplay()}   |   {Sessions.Count(s => s.IsShort)} بعجز   |   {Sessions.Count(s => s.Status == ShiftSessionStatus.Open)} مفتوحة";
    }

    private bool HasSelection() => Selected is not null;

    private bool IsOpenSelected() => Selected is { Status: ShiftSessionStatus.Open };

    private bool IsClosedSelected() => Selected is not null && Selected.Status != ShiftSessionStatus.Open;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Report()
    {
        using var scope = Session.NewScope();
        Pdf.Open(new DocumentFactory(scope).ShiftReport(Selected!.Id), $"وردية-{Selected.Id}");
    }

    [RelayCommand(CanExecute = nameof(IsOpenSelected))]
    private async Task ForceClose()
    {
        var session = Selected!;
        var counted = Dialogs.PromptMoney("إغلاق إجباري",
            $"إغلاق وردية {session.EmployeeName} المفتوحة منذ {session.OpenedAt:yyyy-MM-dd HH:mm}.\nعُدّ النقد الموجود في الدرج وأدخل المبلغ:");

        if (counted is null)
        {
            return;
        }

        var reason = Dialogs.Prompt("سبب الإغلاق الإجباري", "يُسجَّل السبب باسمك في سجل التدقيق:");

        if (reason is null)
        {
            return;
        }

        var actor = User;
        var closed = await RunAsync(scope => scope.Shifts.ForceClose(actor, session.Id, counted.Value, reason));

        if (closed is not null)
        {
            await RefreshAsync();
            SuccessMessage = $"أُغلقت الوردية رقم {closed.Id} إجبارياً بفرق {closed.CashDifference!.Value.ToDisplay()}.";
        }
    }

    [RelayCommand(CanExecute = nameof(IsClosedSelected))]
    private async Task Amend()
    {
        var session = Selected!;
        var counted = Dialogs.PromptMoney("تصحيح عدّ الصندوق",
            $"المتوقع {session.ExpectedCash?.ToDisplay()} والمعدود المسجل {session.ActualClosingCash?.ToDisplay()}.\nأدخل المبلغ الصحيح بعد إعادة العد:",
            session.ActualClosingCash);

        if (counted is null)
        {
            return;
        }

        var reason = Dialogs.Prompt("سبب التصحيح", "يُسجَّل السبب باسمك ويُرحَّل الفرق فقط في قيد جديد:");

        if (reason is null)
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Shifts.AmendClosingCash(actor, session.Id, counted.Value, reason)) is not null)
        {
            await RefreshAsync();
            SuccessMessage = $"صُحّح عدّ الوردية رقم {session.Id}.";
        }
    }
}

/// <summary>Attendance across staff, with audited corrections.</summary>
public sealed partial class AttendanceViewModel : ListPageViewModel
{
    public override string Title => "سجل الحضور";

    public ObservableCollection<AttendanceRow> Records { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CorrectCommand))]
    private AttendanceRow? _selected;

    [ObservableProperty]
    private string _summary = "";

    protected override async Task RefreshAsync()
    {
        var (from, to, search) = (FromDate, ToDate, Search.Trim());
        var rows = await RunAsync(scope => scope.Shifts.Attendance(from, to, null));

        if (rows is null)
        {
            return;
        }

        Records.Clear();

        foreach (var row in rows.Where(r => search.Length == 0 || r.EmployeeName.Contains(search, StringComparison.OrdinalIgnoreCase)))
        {
            Records.Add(row);
        }

        var hours = Records.Where(r => r.Worked is not null).Sum(r => r.Worked!.Value.TotalHours);
        Summary = $"{Records.Count} سجل   |   إجمالي الساعات {hours:0.#}";
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Correct()
    {
        if (FormHost.Show(new AttendanceForm(Selected!)))
        {
            await RefreshAsync();
            SuccessMessage = "صُحّح سجل الحضور.";
        }
    }
}

/// <summary>Monthly payroll: the runs, and the lines of the selected run.</summary>
public sealed partial class PayrollViewModel : PageViewModel
{
    public override string Title => "الرواتب";

    public ObservableCollection<PayrollRun> Runs { get; } = [];

    public ObservableCollection<PayrollLineVm> Lines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDraft))]
    [NotifyCanExecuteChangedFor(nameof(SaveLinesCommand), nameof(PostCommand), nameof(PayCommand), nameof(PrintCommand), nameof(DeleteCommand))]
    private PayrollRun? _selected;

    public bool IsDraft => Selected?.Status == PayrollStatus.Draft;

    partial void OnSelectedChanged(PayrollRun? value) => _ = LoadLinesAsync();

    public override async Task LoadAsync()
    {
        var runs = await RunAsync(scope => scope.Payroll.Runs());

        if (runs is null)
        {
            return;
        }

        var selectedId = Selected?.Id;
        Runs.Clear();

        foreach (var run in runs)
        {
            Runs.Add(run);
        }

        Selected = Runs.FirstOrDefault(r => r.Id == selectedId) ?? Runs.FirstOrDefault();
    }

    private async Task LoadLinesAsync()
    {
        Lines.Clear();

        if (Selected is null)
        {
            return;
        }

        var runId = Selected.Id;
        var lines = await RunAsync(scope => scope.Payroll.Lines(runId));

        foreach (var line in lines ?? [])
        {
            Lines.Add(new PayrollLineVm(line));
        }
    }

    private bool HasSelection() => Selected is not null;

    private bool IsDraftSelected() => Selected is { Status: PayrollStatus.Draft };

    private bool IsPostedSelected() => Selected is { Status: PayrollStatus.Posted };

    [RelayCommand]
    private async Task NewRun()
    {
        var today = AppSession.Current.Clock.Today;
        var text = Dialogs.Prompt("مسير رواتب جديد", "الشهر بصيغة سنة-شهر (مثل 2026-06):",
            $"{today.Year:0000}-{today.Month:00}", multiline: false);

        if (text is null)
        {
            return;
        }

        var parts = text.Trim().Split('-');

        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var month))
        {
            ErrorMessage = "اكتب الشهر بصيغة 2026-06.";
            return;
        }

        var actor = User;
        var run = await RunAsync(scope => scope.Payroll.Draft(actor, year, month));

        if (run is not null)
        {
            await LoadAsync();
            Selected = Runs.FirstOrDefault(r => r.Id == run.Id);
            SuccessMessage = $"أُنشئ مسير رواتب {run.PeriodLabel}. أضف الإضافات والخصومات ثم احفظ واعتمد.";
        }
    }

    [RelayCommand(CanExecute = nameof(IsDraftSelected))]
    private async Task SaveLines()
    {
        var actor = User;
        var changes = new List<(long Id, Money Additions, Money Deductions, string? Notes)>();

        foreach (var line in Lines)
        {
            if (!Money.TryParse(string.IsNullOrWhiteSpace(line.AdditionsText) ? "0" : line.AdditionsText, out var add)
                || !Money.TryParse(string.IsNullOrWhiteSpace(line.DeductionsText) ? "0" : line.DeductionsText, out var ded))
            {
                ErrorMessage = $"قيمة غير صحيحة في سطر {line.Line.EmployeeName}.";
                return;
            }

            changes.Add((line.Line.Id, add, ded, line.Notes));
        }

        if (await RunAsync(scope => scope.Db.InTransaction(() =>
            {
                foreach (var (id, add, ded, notes) in changes)
                {
                    scope.Payroll.UpdateLine(actor, id, add, ded, notes);
                }
            })))
        {
            await LoadAsync();
            SuccessMessage = "حُفظت التعديلات.";
        }
    }

    [RelayCommand(CanExecute = nameof(IsDraftSelected))]
    private async Task Post()
    {
        var run = Selected!;

        if (!Dialogs.Confirm($"اعتماد مسير رواتب {run.PeriodLabel} بصافي {run.TotalNet.ToDisplay()}؟\nيُسجَّل مصروف الرواتب ولا يمكن تعديل المسير بعد الاعتماد.",
                "اعتماد المسير", "اعتماد", "رجوع"))
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Payroll.Post(actor, run.Id)) is not null)
        {
            await LoadAsync();
            SuccessMessage = $"اُعتمد مسير رواتب {run.PeriodLabel}.";
        }
    }

    [RelayCommand(CanExecute = nameof(IsPostedSelected))]
    private async Task Pay()
    {
        var run = Selected!;
        var transfer = Dialogs.Confirm($"صرف رواتب {run.PeriodLabel} بمبلغ {run.TotalNet.ToDisplay()}. كيف صُرفت؟",
            "صرف الرواتب", "تحويل بنكي", "نقداً");
        var method = transfer ? PaymentMethod.Transfer : PaymentMethod.Cash;

        if (!Dialogs.Confirm($"تأكيد صرف {run.TotalNet.ToDisplay()} {method.ToArabic()}؟", "تأكيد", "صرف", "إلغاء"))
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Payroll.Pay(actor, run.Id, method)) is not null)
        {
            await LoadAsync();
            SuccessMessage = $"صُرفت رواتب {run.PeriodLabel}.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Print()
    {
        using var scope = Session.NewScope();
        Pdf.Open(new DocumentFactory(scope).PayrollSheet(Selected!.Id), $"رواتب-{Selected.PeriodLabel}");
    }

    [RelayCommand(CanExecute = nameof(IsDraftSelected))]
    private async Task Delete()
    {
        var run = Selected!;

        if (!Dialogs.Confirm($"حذف مسودة مسير {run.PeriodLabel}؟", "حذف المسودة", "حذف", "إلغاء", danger: true))
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Payroll.DeleteDraft(actor, run.Id)))
        {
            Selected = null;
            await LoadAsync();
        }
    }
}

public sealed partial class PayrollLineVm : ObservableObject
{
    public PayrollLineVm(PayrollLine line)
    {
        Line = line;
        _additionsText = line.Additions.ToString();
        _deductionsText = line.Deductions.ToString();
        _notes = line.Notes;
    }

    public PayrollLine Line { get; }

    [ObservableProperty] private string _additionsText;
    [ObservableProperty] private string _deductionsText;
    [ObservableProperty] private string? _notes;
}

/// <summary>Staff accounts and roles.</summary>
public sealed partial class UsersViewModel : PageViewModel
{
    public override string Title => "المستخدمون";

    public ObservableCollection<UserRowVm> Users { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(ResetPasswordCommand), nameof(ToggleActiveCommand))]
    private UserRowVm? _selected;

    public override async Task LoadAsync()
    {
        var rows = await RunAsync(scope =>
            scope.Users.All().Select(u => new UserRowVm(u, string.Join("، ", scope.Users.RolesOf(u.Id).Select(RoleLabel)))).ToList());

        if (rows is null)
        {
            return;
        }

        var selectedId = Selected?.User.Id;
        Users.Clear();

        foreach (var row in rows)
        {
            Users.Add(row);
        }

        Selected = Users.FirstOrDefault(u => u.User.Id == selectedId);
    }

    public static string RoleLabel(string role) => role switch
    {
        RoleName.Owner => "المالك",
        RoleName.Accountant => "المحاسب",
        RoleName.Salesperson => "البائع",
        _ => role,
    };

    private bool HasSelection() => Selected is not null;

    [RelayCommand]
    private async Task Add()
    {
        if (FormHost.Show(new UserForm(null)))
        {
            await LoadAsync();
            SuccessMessage = "أُضيف المستخدم.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Edit()
    {
        if (FormHost.Show(new UserForm(Selected!.User.Id)))
        {
            await LoadAsync();
            Session.RefreshUser();
            SuccessMessage = "حُفظت بيانات المستخدم.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ResetPassword()
    {
        var user = Selected!.User;
        var password = Dialogs.Prompt("إعادة تعيين كلمة المرور",
            $"كلمة مرور مؤقتة لـ {user.FullName} ({UserService.MinimumPasswordLength} أحرف على الأقل). سيُطلب منه تغييرها عند الدخول:",
            multiline: false);

        if (password is null)
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Users.ResetPassword(user.Id, password, actor)))
        {
            SuccessMessage = $"أُعيد تعيين كلمة مرور {user.FullName}.";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleActive()
    {
        var user = Selected!.User;
        var activate = !user.IsActive;

        if (!activate && !Dialogs.Confirm($"إيقاف حساب {user.FullName}؟ لن يتمكن من الدخول، وتبقى سجلاته كما هي.", "إيقاف حساب", "إيقاف", "إلغاء", danger: true))
        {
            return;
        }

        var actor = User;

        if (await RunAsync(scope => scope.Users.SetActive(user.Id, activate, actor)))
        {
            await LoadAsync();
            SuccessMessage = activate ? $"فُعّل حساب {user.FullName}." : $"أُوقف حساب {user.FullName}.";
        }
    }
}

public sealed record UserRowVm(User User, string RolesText);

/// <summary>The audit trail, read-only.</summary>
public sealed partial class AuditLogViewModel : ListPageViewModel
{
    public AuditLogViewModel()
    {
        Actions =
        [
            new("كل العمليات", null),
            .. Enum.GetValues<AuditAction>().Select(a => new FilterOption<AuditAction?>(a.ToArabic(), a)),
        ];
        _action = Actions[0];
    }

    public override string Title => "سجل التدقيق";

    public ObservableCollection<AuditLog> Entries { get; } = [];

    public IReadOnlyList<FilterOption<AuditAction?>> Actions { get; }

    [ObservableProperty]
    private FilterOption<AuditAction?> _action;

    [ObservableProperty]
    private AuditLog? _selected;

    partial void OnActionChanged(FilterOption<AuditAction?> value) => _ = RefreshAsync();

    protected override async Task RefreshAsync()
    {
        var (from, to, action, search) = (FromDate, ToDate, Action.Value, Search.Trim());
        var rows = await RunAsync(scope => scope.Audit.Search(from, to, action));

        if (rows is null)
        {
            return;
        }

        Entries.Clear();

        foreach (var row in rows.Where(r => search.Length == 0
                     || (r.UserName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                     || r.AuditableType.Contains(search, StringComparison.OrdinalIgnoreCase)
                     || (r.Reason?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                     || (r.NewValues?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            Entries.Add(row);
        }
    }
}
