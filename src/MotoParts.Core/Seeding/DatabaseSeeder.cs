using System.Globalization;
using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;
using MotoParts.Core.Services;

namespace MotoParts.Core.Seeding;

/// <summary>
/// Reference data every installation needs: roles and permissions, the chart
/// of accounts, fiscal periods, shift templates and default settings.
///
/// Idempotent - safe to run on every start. It adds what is missing and never
/// overwrites what the shop has changed (a renamed account keeps its name).
/// It does not create users: the owner account is created by first-run setup
/// with a password the owner chooses, so no installation ships with a default
/// password.
/// </summary>
public sealed class DatabaseSeeder
{
    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly FiscalPeriodService _periods;

    public DatabaseSeeder(DbSession db, IClock clock, FiscalPeriodService periods)
    {
        _db = db;
        _clock = clock;
        _periods = periods;
    }

    public void Seed()
    {
        _db.InTransaction(() =>
        {
            SeedRolesAndPermissions();
            SeedChartOfAccounts();
            SeedShifts();
            SeedSettings();

            // This year and next, so January doesn't arrive with nowhere to post.
            _periods.CreateMonthsForYear(_clock.Today.Year, actor: null);
            _periods.CreateMonthsForYear(_clock.Today.Year + 1, actor: null);
        });
    }

    private void SeedRolesAndPermissions()
    {
        foreach (var permission in Permission.All)
        {
            _db.Execute("INSERT OR IGNORE INTO permissions (name) VALUES (@permission)", new { permission });
        }

        foreach (var (role, (displayName, permissions)) in RoleDefaults.Roles)
        {
            _db.Execute(
                "INSERT OR IGNORE INTO roles (name, display_name) VALUES (@role, @displayName)",
                new { role, displayName });

            // Roles are code-defined: new permissions in an update reach the
            // right roles automatically. Grants are only ever added here.
            foreach (var permission in permissions)
            {
                _db.Execute(
                    """
                    INSERT OR IGNORE INTO role_permissions (role_id, permission_id)
                    SELECT r.id, p.id FROM roles r, permissions p WHERE r.name = @role AND p.name = @permission
                    """,
                    new { role, permission });
            }
        }
    }

    private void SeedChartOfAccounts()
    {
        foreach (var account in DefaultChartOfAccounts.Accounts)
        {
            _db.Execute(
                """
                INSERT OR IGNORE INTO accounts (code, name, type, normal_balance, description, is_system, is_active)
                VALUES (@Code, @Name, @Type, @NormalBalance, @Description, @IsSystem, 1)
                """,
                new
                {
                    account.Code,
                    account.Name,
                    Type = account.Type.ToString(),
                    NormalBalance = account.Type.NormalBalance().ToString(),
                    account.Description,
                    account.IsSystem,
                });
        }
    }

    private void SeedShifts()
    {
        _db.Execute(
            """
            INSERT OR IGNORE INTO shifts (name, start_time, end_time, is_active) VALUES
                ('الوردية الصباحية', '08:00', '16:00', 1),
                ('الوردية المسائية', '16:00', '23:00', 1)
            """);
    }

    private void SeedSettings()
    {
        var defaults = new Dictionary<string, string>
        {
            [SettingsService.ShopName] = "محل قطع غيار الدراجات النارية",
            [SettingsService.CurrencySymbol] = "ر.س",
            [SettingsService.TaxRate] = 0.15m.ToString(CultureInfo.InvariantCulture),
            [SettingsService.PricesIncludeTax] = "1",
            [SettingsService.BackupKeepCount] = "30",
            [SettingsService.InvoiceFooter] = "شكراً لتعاملكم معنا. البضاعة المباعة تُستبدل خلال 7 أيام مع الفاتورة.",
        };

        foreach (var (key, value) in defaults)
        {
            _db.Execute("INSERT OR IGNORE INTO settings (key, value) VALUES (@key, @value)", new { key, value });
        }
    }
}
