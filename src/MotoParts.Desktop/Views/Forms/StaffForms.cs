using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MotoParts.Core.Common;
using MotoParts.Core.Security;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;

namespace MotoParts.Desktop.Views.Forms;

/// <summary>A manager's correction of one attendance record, with a reason for the audit trail.</summary>
public sealed partial class AttendanceForm : FormViewModel
{
    private readonly AttendanceRow _row;

    public AttendanceForm(AttendanceRow row)
    {
        _row = row;
        _checkInText = row.CheckInAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
        _checkOutText = row.CheckOutAt?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "";
    }

    public override string Title => $"تصحيح حضور {_row.EmployeeName} يوم {_row.WorkDate:yyyy-MM-dd}";

    [ObservableProperty] private string _checkInText;
    [ObservableProperty] private string _checkOutText;
    [ObservableProperty] private string _reason = "";

    public override async Task<bool> SaveAsync()
    {
        if (!TryTime(CheckInText, out var checkIn) || !TryTime(CheckOutText, out var checkOut))
        {
            ErrorMessage = "اكتب الوقت بصيغة 24 ساعة مثل 08:30، أو اتركه فارغاً.";
            return false;
        }

        var (actor, id, reason) = (User, _row.Id, Reason);
        return await RunAsync(scope => scope.Shifts.CorrectAttendance(actor, id, checkIn, checkOut, reason));
    }

    private bool TryTime(string text, out DateTime? value)
    {
        value = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!TimeOnly.TryParseExact(text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return false;
        }

        value = _row.WorkDate.ToDateTime(time);
        return true;
    }
}

public sealed partial class RoleChoice : ObservableObject
{
    public RoleChoice(string name, string label, bool isChecked)
    {
        Name = name;
        Label = label;
        _isChecked = isChecked;
    }

    public string Name { get; }

    public string Label { get; }

    [ObservableProperty] private bool _isChecked;
}

/// <summary>A staff account: sign-in, roles, and the employment details payroll uses.</summary>
public sealed partial class UserForm : FormViewModel
{
    private readonly long? _id;

    public UserForm(long? id) => _id = id;

    public override string Title => _id is null ? "مستخدم جديد" : "تعديل المستخدم";

    public bool IsNew => _id is null;

    public ObservableCollection<RoleChoice> Roles { get; } = [];

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _fullName = "";
    [ObservableProperty] private string _employeeCode = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private DateTime? _hireDate;
    [ObservableProperty] private string _baseSalaryText = "0";
    [ObservableProperty] private string _password = "";

    public override async Task LoadAsync()
    {
        var id = _id;
        var result = await TryRunAsync(scope => (scope.Users.Roles(), id is { } uid ? scope.Users.Get(uid) : null, id is { } rid ? scope.Users.RolesOf(rid) : []));

        if (!result.Ok)
        {
            return;
        }

        var (roles, user, userRoles) = result.Value;

        foreach (var role in roles)
        {
            Roles.Add(new RoleChoice(role.Name, role.DisplayName, userRoles.Contains(role.Name) || (user is null && role.Name == RoleName.Salesperson)));
        }

        if (user is null)
        {
            return;
        }

        Username = user.Username;
        FullName = user.FullName;
        EmployeeCode = user.EmployeeCode ?? "";
        Phone = user.Phone ?? "";
        HireDate = user.HireDate?.ToDateTime(TimeOnly.MinValue);
        BaseSalaryText = user.BaseSalary.ToString();
    }

    public override async Task<bool> SaveAsync()
    {
        if (!Money.TryParse(string.IsNullOrWhiteSpace(BaseSalaryText) ? "0" : BaseSalaryText, out var salary))
        {
            ErrorMessage = "الراتب الأساسي غير صحيح.";
            return false;
        }

        var roles = Roles.Where(r => r.IsChecked).Select(r => r.Name).ToList();
        var input = new UserInput(Username, FullName, EmployeeCode, Phone,
            HireDate is { } d ? DateOnly.FromDateTime(d) : null, salary, roles);
        var (actor, id, password) = (User, _id, Password);

        return await RunAsync(scope => id is { } uid ? scope.Users.Update(uid, input, actor) : scope.Users.Create(input, password, actor)) is not null;
    }
}
