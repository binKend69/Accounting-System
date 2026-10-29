using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// Staff accounts: sign-in, roles, and employment details used by payroll.
///
/// Users are deactivated, never deleted, so the sales and till sessions they
/// recorded keep a valid actor.
/// </summary>
public sealed class UserService
{
    public const int MinimumPasswordLength = 6;

    private const int BcryptWorkFactor = 11;

    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly AuditService _audit;

    public UserService(DbSession db, IClock clock, AuditService audit)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
    }

    /// <summary>
    /// Checks a username and password. Returns the actor on success; null on
    /// any failure, without revealing whether it was the name or the password.
    /// </summary>
    public Actor? Authenticate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        var user = _db.QueryFirstOrDefault<User>(
            "SELECT * FROM users WHERE username = @username", new { username = username.Trim() });

        var valid = user is not null && user.IsActive && BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);

        _db.InTransaction(() =>
        {
            if (!valid)
            {
                _audit.Record(nameof(User), user?.Id, AuditAction.LoginFailed, actor: null,
                    newValues: new { Username = username.Trim() });
                return;
            }

            _db.Execute("UPDATE users SET last_login_at = @now WHERE id = @id", new { id = user!.Id, now = _clock.Now });
            _audit.Record(nameof(User), user.Id, AuditAction.LoggedIn, LoadActor(user.Id));
        });

        return valid ? LoadActor(user!.Id) : null;
    }

    public Actor LoadActor(long userId)
    {
        var user = Get(userId);

        var roles = _db.Query<string>(
            "SELECT r.name FROM roles r JOIN user_roles ur ON ur.role_id = r.id WHERE ur.user_id = @userId",
            new { userId }).ToList();

        var permissions = _db.Query<string>(
            """
            SELECT DISTINCT p.name
            FROM permissions p
            JOIN role_permissions rp ON rp.permission_id = p.id
            JOIN user_roles ur ON ur.role_id = rp.role_id
            WHERE ur.user_id = @userId
            """,
            new { userId }).ToList();

        return new Actor(user.Id, user.Username, user.FullName, roles, permissions);
    }

    public User Get(long id) =>
        _db.QueryFirstOrDefault<User>("SELECT * FROM users WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("المستخدم غير موجود.");

    public IReadOnlyList<User> All(bool activeOnly = false) =>
        _db.Query<User>(
            "SELECT * FROM users WHERE (@activeOnly = 0 OR is_active = 1) ORDER BY full_name",
            new { activeOnly }).ToList();

    public IReadOnlyList<Role> Roles() => _db.Query<Role>("SELECT * FROM roles ORDER BY id").ToList();

    public IReadOnlyList<string> RolesOf(long userId) =>
        _db.Query<string>(
            "SELECT r.name FROM roles r JOIN user_roles ur ON ur.role_id = r.id WHERE ur.user_id = @userId",
            new { userId }).ToList();

    public bool AnyUserExists() => _db.ExecuteScalar<long>("SELECT COUNT(*) FROM users") > 0;

    /// <summary>
    /// Creates the very first account, the owner, during first-run setup. Refused
    /// once any user exists, so it cannot be used to slip in a second owner.
    /// </summary>
    public User CreateFirstOwner(string username, string fullName, string password)
    {
        return _db.InTransaction(() =>
        {
            if (AnyUserExists())
            {
                throw new BusinessRuleException("تم إنشاء حساب المالك من قبل.");
            }

            return Insert(new UserInput(username, fullName, null, null, null, Money.Zero, [RoleName.Owner]), password, actor: null);
        });
    }

    public User Create(UserInput input, string password, Actor actor)
    {
        actor.Require(Permission.UserManage, "إدارة المستخدمين من صلاحيات المالك فقط.");

        return _db.InTransaction(() =>
        {
            var user = Insert(input, password, actor);

            // The owner chose this password, so it is only temporary: the
            // employee replaces it with their own at first sign-in.
            _db.Execute("UPDATE users SET must_change_password = 1 WHERE id = @id", new { id = user.Id });
            return Get(user.Id);
        });
    }

    public User Update(long userId, UserInput input, Actor actor)
    {
        actor.Require(Permission.UserManage, "إدارة المستخدمين من صلاحيات المالك فقط.");
        Validate(input);

        var before = Get(userId);
        var beforeRoles = RolesOf(userId);

        // An owner removing their own owner role could lock everyone out of
        // administration.
        if (userId == actor.Id && !input.Roles.Contains(RoleName.Owner) && beforeRoles.Contains(RoleName.Owner))
        {
            throw new BusinessRuleException("لا يمكنك إزالة صلاحية المالك عن حسابك أنت.");
        }

        return _db.InTransaction(() =>
        {
            _db.Execute(
                """
                UPDATE users SET username = @Username, full_name = @FullName, employee_code = @EmployeeCode,
                    phone = @Phone, hire_date = @HireDate, base_salary = @BaseSalary, updated_at = @now
                WHERE id = @userId
                """,
                new
                {
                    userId,
                    Username = input.Username.Trim(),
                    FullName = input.FullName.Trim(),
                    EmployeeCode = Clean(input.EmployeeCode),
                    Phone = Clean(input.Phone),
                    input.HireDate,
                    input.BaseSalary,
                    now = _clock.Now,
                });

            AssignRoles(userId, input.Roles);
            EnsureAnActiveOwnerRemains();

            _audit.Record(nameof(User), userId, AuditAction.Updated, actor,
                new { before.Username, before.FullName, before.BaseSalary, Roles = beforeRoles },
                new { input.Username, input.FullName, input.BaseSalary, input.Roles });

            return Get(userId);
        });
    }

    public void SetActive(long userId, bool active, Actor actor)
    {
        actor.Require(Permission.UserManage, "إدارة المستخدمين من صلاحيات المالك فقط.");

        if (userId == actor.Id && !active)
        {
            throw new BusinessRuleException("لا يمكنك إيقاف حسابك أنت.");
        }

        _db.InTransaction(() =>
        {
            if (!active)
            {
                var hasOpenShift = _db.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM shift_sessions WHERE user_id = @userId AND status = 'Open'", new { userId }) > 0;

                if (hasOpenShift)
                {
                    throw new BusinessRuleException("لدى الموظف وردية مفتوحة؛ يجب إغلاقها قبل إيقاف حسابه.");
                }
            }

            _db.Execute("UPDATE users SET is_active = @active, updated_at = @now WHERE id = @userId",
                new { userId, active, now = _clock.Now });

            EnsureAnActiveOwnerRemains();

            _audit.Record(nameof(User), userId, AuditAction.Updated, actor,
                new { IsActive = !active }, new { IsActive = active });
        });
    }

    /// <summary>A user changing their own password; the current one is required.</summary>
    public void ChangeOwnPassword(Actor actor, string currentPassword, string newPassword)
    {
        var user = Get(actor.Id);

        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
        {
            throw new BusinessRuleException("كلمة المرور الحالية غير صحيحة.");
        }

        ValidatePassword(newPassword);

        _db.InTransaction(() =>
        {
            _db.Execute(
                "UPDATE users SET password_hash = @hash, must_change_password = 0, updated_at = @now WHERE id = @id",
                new { id = user.Id, hash = Hash(newPassword), now = _clock.Now });

            _audit.Record(nameof(User), user.Id, AuditAction.Updated, actor, reason: "تغيير كلمة المرور");
        });
    }

    /// <summary>An owner resetting someone's password; they must change it at next sign-in.</summary>
    public void ResetPassword(long userId, string newPassword, Actor actor)
    {
        actor.Require(Permission.UserManage, "إدارة المستخدمين من صلاحيات المالك فقط.");
        ValidatePassword(newPassword);

        _db.InTransaction(() =>
        {
            _db.Execute(
                "UPDATE users SET password_hash = @hash, must_change_password = 1, updated_at = @now WHERE id = @userId",
                new { userId, hash = Hash(newPassword), now = _clock.Now });

            _audit.Record(nameof(User), userId, AuditAction.Updated, actor, reason: "إعادة تعيين كلمة المرور");
        });
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength)
        {
            throw new BusinessRuleException($"كلمة المرور يجب أن تكون {MinimumPasswordLength} أحرف على الأقل.");
        }
    }

    private User Insert(UserInput input, string password, Actor? actor)
    {
        Validate(input);
        ValidatePassword(password);

        var id = _db.Insert(
            """
            INSERT INTO users (username, full_name, password_hash, employee_code, phone, hire_date,
                               base_salary, is_active, created_at, updated_at)
            VALUES (@Username, @FullName, @PasswordHash, @EmployeeCode, @Phone, @HireDate,
                    @BaseSalary, 1, @now, @now)
            RETURNING id
            """,
            new
            {
                Username = input.Username.Trim(),
                FullName = input.FullName.Trim(),
                PasswordHash = Hash(password),
                EmployeeCode = Clean(input.EmployeeCode),
                Phone = Clean(input.Phone),
                input.HireDate,
                input.BaseSalary,
                now = _clock.Now,
            });

        AssignRoles(id, input.Roles);

        _audit.Record(nameof(User), id, AuditAction.Created, actor,
            newValues: new { input.Username, input.FullName, input.Roles, input.BaseSalary });

        return Get(id);
    }

    private void AssignRoles(long userId, IReadOnlyCollection<string> roles)
    {
        if (roles.Count == 0)
        {
            throw new BusinessRuleException("يجب اختيار دور واحد على الأقل للمستخدم.");
        }

        _db.Execute("DELETE FROM user_roles WHERE user_id = @userId", new { userId });

        foreach (var role in roles.Distinct())
        {
            var inserted = _db.Execute(
                "INSERT INTO user_roles (user_id, role_id) SELECT @userId, id FROM roles WHERE name = @role",
                new { userId, role });

            if (inserted == 0)
            {
                throw new BusinessRuleException($"الدور {role} غير معروف.");
            }
        }
    }

    private void EnsureAnActiveOwnerRemains()
    {
        var owners = _db.ExecuteScalar<long>(
            """
            SELECT COUNT(*) FROM users u
            JOIN user_roles ur ON ur.user_id = u.id
            JOIN roles r ON r.id = ur.role_id
            WHERE r.name = @owner AND u.is_active = 1
            """,
            new { owner = RoleName.Owner });

        if (owners == 0)
        {
            throw new BusinessRuleException("يجب أن يبقى مالك واحد نشط على الأقل في النظام.");
        }
    }

    private static void Validate(UserInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Username))
        {
            throw new BusinessRuleException("اسم المستخدم مطلوب.");
        }

        if (input.Username.Trim().Contains(' ', StringComparison.Ordinal))
        {
            throw new BusinessRuleException("اسم المستخدم يجب ألا يحتوي على مسافات.");
        }

        if (string.IsNullOrWhiteSpace(input.FullName))
        {
            throw new BusinessRuleException("الاسم الكامل مطلوب.");
        }

        if (input.BaseSalary.IsNegative)
        {
            throw new BusinessRuleException("الراتب الأساسي لا يمكن أن يكون سالباً.");
        }
    }

    private static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record UserInput(
    string Username,
    string FullName,
    string? EmployeeCode,
    string? Phone,
    DateOnly? HireDate,
    Money BaseSalary,
    IReadOnlyCollection<string> Roles);
