using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Documents;

namespace MotoParts.Desktop.Views.Pages;

public sealed partial class MyShiftViewModel : PageViewModel
{
    public override string Title => "ورديتي والحضور";

    public ObservableCollection<Shift> ShiftTemplates { get; } = [];

    public ObservableCollection<ShiftSessionRow> RecentSessions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenSession))]
    private ShiftSession? _session;

    [ObservableProperty]
    private DrawerTotals? _totals;

    [ObservableProperty]
    private Shift? _selectedShift;

    [ObservableProperty]
    private string _openingCashText = "";

    [ObservableProperty]
    private string _countedCashText = "";

    [ObservableProperty]
    private string _notes = "";

    [ObservableProperty]
    private Attendance? _attendance;

    [ObservableProperty]
    private ShiftSessionRow? _selectedSession;

    public bool HasOpenSession => Session is not null;

    public bool CanCheckIn => Attendance?.CheckInAt is null;

    public bool CanCheckOut => Attendance?.CheckInAt is not null && Attendance.CheckOutAt is null;

    public string AttendanceText => Attendance switch
    {
        null or { CheckInAt: null } => "لم تسجل حضورك اليوم.",
        { CheckOutAt: null } a => $"سجلت حضورك اليوم الساعة {a.CheckInAt:HH:mm}.",
        var a => $"حضور {a.CheckInAt:HH:mm} — انصراف {a.CheckOutAt:HH:mm} ({a.Worked:hh\\:mm} ساعة).",
    };

    public override async Task LoadAsync()
    {
        var userId = User.Id;

        var result = await TryRunAsync(scope =>
        {
            var session = scope.Shifts.OpenSessionFor(userId);
            var totals = session is null ? null : scope.Shifts.DrawerTotals(session.Id);
            var templates = scope.Shifts.Shifts(activeOnly: true);
            var recent = scope.Shifts.Sessions(null, null, userId).Take(15).ToList();
            var attendance = scope.Shifts.TodayFor(userId);
            return (session, totals, templates, recent, attendance);
        });

        if (!result.Ok)
        {
            return;
        }

        var (session, totals, templates, recent, attendance) = result.Value;

        Session = session;
        Totals = totals;
        Attendance = attendance;

        ShiftTemplates.Clear();

        foreach (var template in templates)
        {
            ShiftTemplates.Add(template);
        }

        // Suggest the template whose hours cover now.
        var now = TimeOnly.FromDateTime(AppSession.Current.Clock.Now);
        SelectedShift ??= ShiftTemplates.FirstOrDefault(s => s.StartTime <= now && now < s.EndTime) ?? ShiftTemplates.FirstOrDefault();

        RecentSessions.Clear();

        foreach (var row in recent)
        {
            RecentSessions.Add(row);
        }

        OnPropertyChanged(nameof(CanCheckIn));
        OnPropertyChanged(nameof(CanCheckOut));
        OnPropertyChanged(nameof(AttendanceText));
    }

    [RelayCommand]
    private async Task OpenTill()
    {
        if (SelectedShift is null)
        {
            ErrorMessage = "اختر الوردية.";
            return;
        }

        if (!Money.TryParse(OpeningCashText, out var openingCash))
        {
            ErrorMessage = "أدخل مبلغ العهدة الافتتاحية المعدود في الدرج (صفر إن كان فارغاً).";
            return;
        }

        var actor = User;
        var shiftId = SelectedShift.Id;
        var notes = Notes;

        if (await RunAsync(scope => scope.Shifts.Open(actor, shiftId, openingCash, notes)) is not null)
        {
            OpeningCashText = "";
            Notes = "";
            await LoadAsync();
            SuccessMessage = "فُتحت الوردية. يمكنك البيع الآن.";
        }
    }

    [RelayCommand]
    private async Task CloseTill()
    {
        if (Session is null)
        {
            return;
        }

        if (!Money.TryParse(CountedCashText, out var counted))
        {
            ErrorMessage = "عُدّ النقد في الدرج وأدخل المبلغ الفعلي.";
            return;
        }

        var expected = Totals?.Expected ?? Money.Zero;
        var difference = counted - expected;

        var message = difference.IsZero
            ? $"المبلغ المعدود {counted.ToDisplay()} مطابق للمتوقع. إغلاق الوردية؟"
            : $"المتوقع في الدرج {expected.ToDisplay()} والمعدود {counted.ToDisplay()}.\n" +
              $"الفرق {(difference.IsNegative ? "عجز" : "زيادة")} بمبلغ {difference.Abs().ToDisplay()} وسيُسجَّل باسمك.\n\nهل أعدت العدّ وتريد الإغلاق؟";

        if (!Dialogs.Confirm(message, "إغلاق الوردية", "إغلاق الوردية", "رجوع", danger: !difference.IsZero))
        {
            return;
        }

        var actor = User;
        var sessionId = Session.Id;
        var notes = Notes;

        var closed = await RunAsync(scope => scope.Shifts.Close(actor, sessionId, counted, notes));

        if (closed is null)
        {
            return;
        }

        CountedCashText = "";
        Notes = "";
        await LoadAsync();

        SuccessMessage = closed.CashDifference is { IsZero: true }
            ? "أُغلقت الوردية والصندوق مطابق."
            : $"أُغلقت الوردية بفرق {closed.CashDifference!.Value.ToDisplay()}.";

        if (Dialogs.Confirm("طباعة تقرير إغلاق الوردية الآن؟", "تقرير الوردية", "طباعة", "لاحقاً"))
        {
            PrintSession(sessionId);
        }
    }

    [RelayCommand]
    private void PrintSelected()
    {
        if (SelectedSession is not null)
        {
            PrintSession(SelectedSession.Id);
        }
    }

    [RelayCommand]
    private void PrintCurrent()
    {
        if (Session is not null)
        {
            PrintSession(Session.Id);
        }
    }

    [RelayCommand]
    private async Task CheckIn()
    {
        var actor = User;

        if (await RunAsync(scope => scope.Shifts.CheckIn(actor)) is not null)
        {
            await LoadAsync();
            SuccessMessage = "سُجّل حضورك.";
        }
    }

    [RelayCommand]
    private async Task CheckOut()
    {
        var actor = User;

        if (await RunAsync(scope => scope.Shifts.CheckOut(actor)) is not null)
        {
            await LoadAsync();
            SuccessMessage = "سُجّل انصرافك.";
        }
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private static void PrintSession(long sessionId)
    {
        using var scope = AppSession.Current.NewScope();
        Pdf.Open(new DocumentFactory(scope).ShiftReport(sessionId), $"وردية-{sessionId}");
    }
}
