using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// Customers and suppliers, their balances and their statements.
///
/// Balances are computed from the documents, never stored, so they cannot
/// drift. The integrity check compares their totals with the Receivable and
/// Payable control accounts in the ledger.
/// </summary>
public sealed class PartyService
{
    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly AccountingService _accounting;
    private readonly AuditService _audit;

    public PartyService(DbSession db, IClock clock, AccountingService accounting, AuditService audit)
    {
        _db = db;
        _clock = clock;
        _accounting = accounting;
        _audit = audit;
    }

    // --- Customers --------------------------------------------------------------

    public Customer GetCustomer(long id) =>
        _db.QueryFirstOrDefault<Customer>("SELECT * FROM customers WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("العميل غير موجود.");

    public IReadOnlyList<CustomerRow> Customers(string? search = null, bool activeOnly = false, bool withBalanceOnly = false) =>
        _db.Query<CustomerRow>(
            $"""
            SELECT c.*, {CustomerBalanceSql("c.id")} AS balance
            FROM customers c
            WHERE (@search IS NULL OR c.name LIKE @like OR c.phone LIKE @like)
              AND (@activeOnly = 0 OR c.is_active = 1)
            ORDER BY c.name
            """,
            new { search = Clean(search), like = $"%{search?.Trim()}%", activeOnly })
        .Where(c => !withBalanceOnly || !c.Balance.IsZero)
        .ToList();

    /// <summary>What the customer owes: opening balance + credit sales - credit refunds - receipts.</summary>
    public Money CustomerBalance(long customerId) =>
        Money.FromMinorUnits(_db.ExecuteScalar<long>($"SELECT {CustomerBalanceSql("@customerId")}", new { customerId }));

    public Customer SaveCustomer(long? id, CustomerInput input, Actor actor)
    {
        actor.Require(Permission.CustomerManage, "ليست لديك صلاحية إدارة العملاء.");

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new BusinessRuleException("اسم العميل مطلوب.");
        }

        if (input.CreditLimit.IsNegative)
        {
            throw new BusinessRuleException("حد الائتمان لا يمكن أن يكون سالباً.");
        }

        return _db.InTransaction(() =>
        {
            if (id is null)
            {
                var newId = _db.Insert(
                    """
                    INSERT INTO customers (name, phone, email, address, tax_number, credit_limit, is_active, notes)
                    VALUES (@Name, @Phone, @Email, @Address, @TaxNumber, @CreditLimit, @IsActive, @Notes)
                    RETURNING id
                    """,
                    CustomerParameters(input));

                _audit.Record(nameof(Customer), newId, AuditAction.Created, actor, newValues: input);
                return GetCustomer(newId);
            }

            var before = GetCustomer(id.Value);

            _db.Execute(
                """
                UPDATE customers SET name = @Name, phone = @Phone, email = @Email, address = @Address,
                    tax_number = @TaxNumber, credit_limit = @CreditLimit, is_active = @IsActive, notes = @Notes,
                    updated_at = datetime('now', 'localtime')
                WHERE id = @Id
                """,
                WithId(CustomerParameters(input), id.Value));

            _audit.Record(nameof(Customer), id, AuditAction.Updated, actor,
                new { before.Name, before.CreditLimit, before.IsActive },
                new { input.Name, input.CreditLimit, input.IsActive });

            return GetCustomer(id.Value);
        });
    }

    /// <summary>
    /// Debt brought forward from before the system: Dr Receivables / Cr Opening
    /// Balances. Once only, so the ledger and the customer card always agree.
    /// </summary>
    public Customer SetCustomerOpeningBalance(long customerId, Money amount, Actor actor)
    {
        actor.Require(Permission.AccountingPostManualEntry, "إدخال الأرصدة الافتتاحية من صلاحيات المالك.");

        if (!amount.IsPositive)
        {
            throw new BusinessRuleException("الرصيد الافتتاحي يجب أن يكون أكبر من صفر.");
        }

        return _db.InTransaction(() =>
        {
            var customer = GetCustomer(customerId);

            if (customer.OpeningJournalEntryId is not null)
            {
                throw new BusinessRuleException("تم إدخال الرصيد الافتتاحي لهذا العميل من قبل.");
            }

            var entry = _accounting.Post(
                $"رصيد افتتاحي للعميل {customer.Name}",
                [
                    LedgerLine.Debit(AccountCodes.AccountsReceivable, amount, customer.Name),
                    LedgerLine.Credit(AccountCodes.OpeningBalanceEquity, amount, customer.Name),
                ],
                JournalSource.Opening, _clock.Today, actor, nameof(Customer), customerId);

            _db.Execute(
                "UPDATE customers SET opening_balance = @amount, opening_journal_entry_id = @entryId WHERE id = @customerId",
                new { customerId, amount, entryId = entry.Id });

            return GetCustomer(customerId);
        });
    }

    /// <summary>Every document that moved the customer's balance, oldest first, with a running balance.</summary>
    public IReadOnlyList<StatementLine> CustomerStatement(long customerId, DateOnly? from = null, DateOnly? to = null)
    {
        var customer = GetCustomer(customerId);

        var rows = _db.Query<StatementLine>(
            """
            SELECT * FROM (
                SELECT sale_date AS date, sold_at AS at, invoice_number AS reference, 'فاتورة مبيعات آجلة' AS description,
                       total AS debit, 0 AS credit
                FROM sales WHERE customer_id = @customerId AND payment_method = 'Credit'
                UNION ALL
                SELECT r.refund_date, r.refunded_at, r.refund_number, 'مرتجع على الفاتورة ' || s.invoice_number,
                       0, r.total
                FROM sale_refunds r JOIN sales s ON s.id = r.sale_id
                WHERE s.customer_id = @customerId AND r.payment_method = 'Credit'
                UNION ALL
                SELECT receipt_date, received_at, receipt_number, 'سند قبض', 0, amount
                FROM customer_receipts WHERE customer_id = @customerId AND voided_at IS NULL
            )
            ORDER BY at
            """,
            new { customerId }).ToList();

        return WithRunningBalance(rows, customer.OpeningBalance, from, to);
    }

    // --- Suppliers --------------------------------------------------------------

    public Supplier GetSupplier(long id) =>
        _db.QueryFirstOrDefault<Supplier>("SELECT * FROM suppliers WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("المورد غير موجود.");

    public IReadOnlyList<SupplierRow> Suppliers(string? search = null, bool activeOnly = false) =>
        _db.Query<SupplierRow>(
            $"""
            SELECT s.*, {SupplierBalanceSql("s.id")} AS balance,
                   (SELECT COUNT(*) FROM purchases p WHERE p.supplier_id = s.id AND p.status = 'Received') AS purchase_count
            FROM suppliers s
            WHERE (@search IS NULL OR s.name LIKE @like OR s.phone LIKE @like OR s.contact_person LIKE @like)
              AND (@activeOnly = 0 OR s.is_active = 1)
            ORDER BY s.name
            """,
            new { search = Clean(search), like = $"%{search?.Trim()}%", activeOnly }).ToList();

    /// <summary>What the shop owes: opening balance + received credit purchases - payments.</summary>
    public Money SupplierBalance(long supplierId) =>
        Money.FromMinorUnits(_db.ExecuteScalar<long>($"SELECT {SupplierBalanceSql("@supplierId")}", new { supplierId }));

    public Supplier SaveSupplier(long? id, SupplierInput input, Actor actor)
    {
        actor.Require(Permission.PurchaseManage, "ليست لديك صلاحية إدارة الموردين.");

        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new BusinessRuleException("اسم المورد مطلوب.");
        }

        return _db.InTransaction(() =>
        {
            if (id is null)
            {
                var newId = _db.Insert(
                    """
                    INSERT INTO suppliers (name, contact_person, phone, email, address, tax_number, is_active, notes)
                    VALUES (@Name, @ContactPerson, @Phone, @Email, @Address, @TaxNumber, @IsActive, @Notes)
                    RETURNING id
                    """,
                    SupplierParameters(input));

                _audit.Record(nameof(Supplier), newId, AuditAction.Created, actor, newValues: input);
                return GetSupplier(newId);
            }

            _db.Execute(
                """
                UPDATE suppliers SET name = @Name, contact_person = @ContactPerson, phone = @Phone, email = @Email,
                    address = @Address, tax_number = @TaxNumber, is_active = @IsActive, notes = @Notes,
                    updated_at = datetime('now', 'localtime')
                WHERE id = @Id
                """,
                WithId(SupplierParameters(input), id.Value));

            _audit.Record(nameof(Supplier), id, AuditAction.Updated, actor, newValues: input);
            return GetSupplier(id.Value);
        });
    }

    public Supplier SetSupplierOpeningBalance(long supplierId, Money amount, Actor actor)
    {
        actor.Require(Permission.AccountingPostManualEntry, "إدخال الأرصدة الافتتاحية من صلاحيات المالك.");

        if (!amount.IsPositive)
        {
            throw new BusinessRuleException("الرصيد الافتتاحي يجب أن يكون أكبر من صفر.");
        }

        return _db.InTransaction(() =>
        {
            var supplier = GetSupplier(supplierId);

            if (supplier.OpeningJournalEntryId is not null)
            {
                throw new BusinessRuleException("تم إدخال الرصيد الافتتاحي لهذا المورد من قبل.");
            }

            var entry = _accounting.Post(
                $"رصيد افتتاحي للمورد {supplier.Name}",
                [
                    LedgerLine.Debit(AccountCodes.OpeningBalanceEquity, amount, supplier.Name),
                    LedgerLine.Credit(AccountCodes.AccountsPayable, amount, supplier.Name),
                ],
                JournalSource.Opening, _clock.Today, actor, nameof(Supplier), supplierId);

            _db.Execute(
                "UPDATE suppliers SET opening_balance = @amount, opening_journal_entry_id = @entryId WHERE id = @supplierId",
                new { supplierId, amount, entryId = entry.Id });

            return GetSupplier(supplierId);
        });
    }

    public IReadOnlyList<StatementLine> SupplierStatement(long supplierId, DateOnly? from = null, DateOnly? to = null)
    {
        var supplier = GetSupplier(supplierId);

        // For a supplier, "debit" is what we paid and "credit" what we owe.
        var rows = _db.Query<StatementLine>(
            """
            SELECT * FROM (
                SELECT purchase_date AS date, received_at AS at, purchase_number AS reference,
                       'مشتريات آجلة' || COALESCE(' - فاتورة المورد ' || supplier_invoice_number, '') AS description,
                       0 AS debit, total AS credit
                FROM purchases WHERE supplier_id = @supplierId AND status = 'Received' AND payment_method = 'Credit'
                UNION ALL
                SELECT payment_date, paid_at, payment_number, 'سند صرف', amount, 0
                FROM supplier_payments WHERE supplier_id = @supplierId AND voided_at IS NULL
            )
            ORDER BY at
            """,
            new { supplierId }).ToList();

        // Running balance in the supplier's direction: owed goes up with credits.
        foreach (var row in rows)
        {
            (row.Debit, row.Credit) = (row.Credit, row.Debit);
        }

        var statement = WithRunningBalance(rows, supplier.OpeningBalance, from, to);

        foreach (var row in statement)
        {
            (row.Debit, row.Credit) = (row.Credit, row.Debit);
        }

        return statement;
    }

    internal static string CustomerBalanceSql(string id) =>
        $"""
        ((SELECT opening_balance FROM customers WHERE id = {id})
         + COALESCE((SELECT SUM(total) FROM sales WHERE customer_id = {id} AND payment_method = 'Credit'), 0)
         - COALESCE((SELECT SUM(r.total) FROM sale_refunds r JOIN sales s ON s.id = r.sale_id
                     WHERE s.customer_id = {id} AND r.payment_method = 'Credit'), 0)
         - COALESCE((SELECT SUM(amount) FROM customer_receipts WHERE customer_id = {id} AND voided_at IS NULL), 0))
        """;

    internal static string SupplierBalanceSql(string id) =>
        $"""
        ((SELECT opening_balance FROM suppliers WHERE id = {id})
         + COALESCE((SELECT SUM(total) FROM purchases
                     WHERE supplier_id = {id} AND status = 'Received' AND payment_method = 'Credit'), 0)
         - COALESCE((SELECT SUM(amount) FROM supplier_payments WHERE supplier_id = {id} AND voided_at IS NULL), 0))
        """;

    private static List<StatementLine> WithRunningBalance(List<StatementLine> rows, Money opening, DateOnly? from, DateOnly? to)
    {
        var balance = opening;
        var result = new List<StatementLine>();

        foreach (var row in rows)
        {
            if (from is not null && row.Date < from)
            {
                balance += row.Debit - row.Credit;
                continue;
            }

            if (to is not null && row.Date > to)
            {
                break;
            }

            if (result.Count == 0)
            {
                result.Add(new StatementLine { Description = "الرصيد السابق", Balance = balance, Date = from ?? row.Date });
            }

            balance += row.Debit - row.Credit;
            row.Balance = balance;
            result.Add(row);
        }

        if (result.Count == 0)
        {
            result.Add(new StatementLine { Description = "الرصيد", Balance = balance, Date = from ?? DateOnly.MinValue });
        }

        return result;
    }

    private static object CustomerParameters(CustomerInput input) => new
    {
        Name = input.Name.Trim(),
        Phone = Clean(input.Phone),
        Email = Clean(input.Email),
        Address = Clean(input.Address),
        TaxNumber = Clean(input.TaxNumber),
        input.CreditLimit,
        input.IsActive,
        Notes = Clean(input.Notes),
    };

    private static object SupplierParameters(SupplierInput input) => new
    {
        Name = input.Name.Trim(),
        ContactPerson = Clean(input.ContactPerson),
        Phone = Clean(input.Phone),
        Email = Clean(input.Email),
        Address = Clean(input.Address),
        TaxNumber = Clean(input.TaxNumber),
        input.IsActive,
        Notes = Clean(input.Notes),
    };

    private static Dapper.DynamicParameters WithId(object parameters, long id)
    {
        var combined = new Dapper.DynamicParameters(parameters);
        combined.Add("Id", id);
        return combined;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record CustomerInput(
    string Name, string? Phone, string? Email, string? Address, string? TaxNumber,
    Money CreditLimit, bool IsActive, string? Notes);

public sealed record SupplierInput(
    string Name, string? ContactPerson, string? Phone, string? Email, string? Address, string? TaxNumber,
    bool IsActive, string? Notes);

public sealed class CustomerRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? TaxNumber { get; set; }
    public Money CreditLimit { get; set; }
    public Money OpeningBalance { get; set; }
    public bool IsActive { get; set; }
    public string? Notes { get; set; }
    public Money Balance { get; set; }

    public bool IsOverLimit => CreditLimit.IsPositive && Balance >= CreditLimit;
}

public sealed class SupplierRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? TaxNumber { get; set; }
    public Money OpeningBalance { get; set; }
    public bool IsActive { get; set; }
    public string? Notes { get; set; }
    public Money Balance { get; set; }
    public long PurchaseCount { get; set; }
}

public sealed class StatementLine
{
    public DateOnly Date { get; set; }
    public DateTime? At { get; set; }
    public string? Reference { get; set; }
    public string Description { get; set; } = "";
    public Money Debit { get; set; }
    public Money Credit { get; set; }
    public Money Balance { get; set; }
}
