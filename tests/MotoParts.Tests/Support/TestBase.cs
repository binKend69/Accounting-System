using Microsoft.Data.Sqlite;
using MotoParts.Core;
using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;
using MotoParts.Core.Services;

namespace MotoParts.Tests.Support;

/// <summary>
/// A fresh, migrated and seeded database file per test, with time frozen at
/// 2026-06-15 10:00 so every date in an assertion is predictable.
///
/// Tests run against a real SQLite file rather than mocks, because the rules
/// being tested live partly in triggers and constraints.
/// </summary>
public abstract class TestBase : IDisposable
{
    private readonly string _directory;
    private readonly List<ServiceScope> _scopes = [];
    private int _userCounter;

    protected TestBase()
    {
        SqlTypeHandlers.Register();

        _directory = Path.Combine(Path.GetTempPath(), "motoparts-tests", Guid.NewGuid().ToString("N"));
        Database = new Database(Path.Combine(_directory, "test.db"));
        Clock = new FixedClock(new DateTime(2026, 6, 15, 10, 0, 0));

        new MigrationRunner(Database).Migrate();
        App = NewScope();
        App.Seeder.Seed();
    }

    protected Database Database { get; }

    protected FixedClock Clock { get; }

    /// <summary>The default scope for a test. Use <see cref="NewScope"/> to simulate a second till.</summary>
    protected ServiceScope App { get; }

    protected static DateOnly Today => new(2026, 6, 15);

    protected ServiceScope NewScope()
    {
        var scope = new ServiceScope(Database, Clock);
        _scopes.Add(scope);
        return scope;
    }

    protected Actor CreateUser(string role, string? name = null, decimal baseSalary = 0m)
    {
        _userCounter++;
        var username = $"{role}{_userCounter}";

        // Created directly rather than through UserService.Create, which would
        // need an owner actor to exist first.
        var user = App.Db.InTransaction(() =>
        {
            var id = App.Db.Insert(
                """
                INSERT INTO users (username, full_name, password_hash, base_salary, is_active)
                VALUES (@username, @name, @hash, @salary, 1) RETURNING id
                """,
                new
                {
                    username,
                    name = name ?? $"{role} {_userCounter}",
                    hash = BCrypt.Net.BCrypt.HashPassword("secret123", 4),
                    salary = Money.Of(baseSalary),
                });

            App.Db.Execute(
                "INSERT INTO user_roles (user_id, role_id) SELECT @id, id FROM roles WHERE name = @role",
                new { id, role });

            return id;
        });

        return App.Users.LoadActor(user);
    }

    protected Actor Owner(string? name = null) => CreateUser(RoleName.Owner, name);

    protected Actor Accountant(string? name = null) => CreateUser(RoleName.Accountant, name);

    protected Actor Salesperson(string? name = null) => CreateUser(RoleName.Salesperson, name);

    /// <summary>Runs raw SQL outside the services, to prove the database itself refuses it.</summary>
    protected void RawSql(string sql, object? parameters = null)
    {
        using var scope = new DbSession(Database);
        scope.Execute(sql, parameters);
    }

    protected T RawScalar<T>(string sql, object? parameters = null)
    {
        using var scope = new DbSession(Database);
        return scope.ExecuteScalar<T>(sql, parameters)!;
    }

    private Actor? _admin;

    /// <summary>An owner for setup steps, created on first use.</summary>
    protected Actor Admin => _admin ??= Owner("مدير الاختبار");

    protected Product CreateProduct(string sku, decimal sellingPrice, decimal costPrice = 0m, decimal reorderLevel = 0m, string unit = "قطعة") =>
        App.Catalog.CreateProduct(
            new ProductInput(sku, null, $"صنف {sku}", null, null, null, null, unit,
                Money.Of(sellingPrice), Money.Of(costPrice), Quantity.Of(reorderLevel), true),
            Admin);

    /// <summary>Puts stock on the shelf at a known unit cost, through the real opening-stock path.</summary>
    protected Product Stock(string sku, decimal quantity, decimal unitCost, decimal sellingPrice)
    {
        var product = CreateProduct(sku, sellingPrice);
        App.Inventory.RecordOpeningStock(Admin, product.Id, Quantity.Of(quantity), Money.Of(unitCost));
        return App.Catalog.Get(product.Id);
    }

    protected long MorningShiftId => App.Shifts.Shifts().First().Id;

    protected ShiftSession OpenTill(Actor actor, decimal openingCash = 100m) =>
        App.Shifts.Open(actor, MorningShiftId, Money.Of(openingCash));

    /// <summary>Sets the tax configuration the till will use.</summary>
    protected void Tax(decimal rate, bool pricesIncludeTax)
    {
        App.Settings.Set(SettingsService.TaxRate, rate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        App.Settings.Set(SettingsService.PricesIncludeTax, pricesIncludeTax ? "1" : "0");
    }

    protected static CartLine Line(Product product, decimal quantity, decimal? unitPrice = null, decimal discount = 0m) =>
        new(product.Id, Quantity.Of(quantity), unitPrice is null ? null : Money.Of(unitPrice.Value), Money.Of(discount));

    protected Money Balance(string accountCode) => App.Accounting.BalanceByCode(accountCode);

    /// <summary>Sum of every product's stock value - must always equal the Inventory account.</summary>
    protected Money ShelfValue() => Money.FromMinorUnits(RawScalar<long>("SELECT COALESCE(SUM(stock_value), 0) FROM products"));

    /// <summary>True when total debits equal total credits across all posted entries.</summary>
    protected bool LedgerBalances() =>
        RawScalar<long>("""
            SELECT COALESCE(SUM(l.debit), 0) - COALESCE(SUM(l.credit), 0)
            FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id
            WHERE e.posted_at IS NOT NULL
            """) == 0;

    protected static Money M(decimal amount) => Money.Of(amount);

    protected static Quantity Q(decimal quantity) => Quantity.Of(quantity);

    public void Dispose()
    {
        foreach (var scope in _scopes)
        {
            scope.Dispose();
        }

        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }
}
