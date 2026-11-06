using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// Money in and out that is not a sale or a purchase:
///
///   Customer receipt (سند قبض):   Dr Cash/Bank       / Cr Receivables
///   Supplier payment (سند صرف):   Dr Payables        / Cr Cash/Bank
///   Expense voucher (سند مصروف):  Dr expense account / Cr Cash/Bank
///
/// A cash voucher can go through the operator's till drawer, in which case it
/// counts towards that session's expected cash, or through the safe.
///
/// Vouchers are never edited or deleted. A mistake is voided: a reversing entry
/// is posted, and the voucher stays on record marked void, with who and why.
/// </summary>
public sealed class VoucherService
{
    private readonly DbSession _db;
    private readonly IClock _clock;
    private readonly AccountingService _accounting;
    private readonly ShiftService _shifts;
    private readonly SequenceService _sequences;
    private readonly PartyService _parties;
    private readonly AuditService _audit;

    public VoucherService(DbSession db, IClock clock, AccountingService accounting, ShiftService shifts,
        SequenceService sequences, PartyService parties, AuditService audit)
    {
        _db = db;
        _clock = clock;
        _accounting = accounting;
        _shifts = shifts;
        _sequences = sequences;
        _parties = parties;
        _audit = audit;
    }

    // --- Customer receipts ------------------------------------------------------

    public CustomerReceipt ReceiveFromCustomer(Actor actor, long customerId, Money amount, PaymentMethod method,
        bool throughDrawer, string? reference = null, string? notes = null)
    {
        actor.Require(Permission.CustomerReceive, "ليست لديك صلاحية تحصيل مبالغ العملاء.");
        ValidateAmountAndMethod(amount, method);

        var date = _clock.Today;
        _accounting.ResolveOpenPeriodFor(date);

        return _db.InTransaction(() =>
        {
            var customer = _parties.GetCustomer(customerId);
            var balance = _parties.CustomerBalance(customerId);

            if (amount > balance)
            {
                throw new BusinessRuleException(
                    $"المبلغ المحصل ({amount.ToDisplay()}) أكبر من رصيد العميل {customer.Name} ({balance.ToDisplay()}).");
            }

            var sessionId = DrawerSession(actor, method, throughDrawer);
            var number = _sequences.Next(SequenceService.CustomerReceipt, date.Year);

            var id = _db.Insert(
                """
                INSERT INTO customer_receipts
                    (receipt_number, customer_id, receipt_date, received_at, amount, payment_method,
                     shift_session_id, user_id, reference, notes)
                VALUES (@number, @customerId, @date, @now, @amount, @method, @sessionId, @userId, @reference, @notes)
                RETURNING id
                """,
                new { number, customerId, date, now = _clock.Now, amount, method = method.ToString(), sessionId, userId = actor.Id, reference = Clean(reference), notes = Clean(notes) });

            var entry = _accounting.Post(
                $"سند قبض {number} من العميل {customer.Name}",
                [
                    LedgerLine.Debit(SaleService.SettlementAccount(method), amount, customer.Name),
                    LedgerLine.Credit(AccountCodes.AccountsReceivable, amount, customer.Name),
                ],
                JournalSource.CustomerReceipt, date, actor, nameof(CustomerReceipt), id);

            _db.Execute("UPDATE customer_receipts SET journal_entry_id = @entryId WHERE id = @id", new { id, entryId = entry.Id });

            return GetCustomerReceipt(id);
        });
    }

    public CustomerReceipt VoidCustomerReceipt(Actor actor, long receiptId, string reason) =>
        _db.InTransaction(() =>
        {
            var receipt = GetCustomerReceipt(receiptId);
            var reversalId = VoidCommon(actor, reason, receipt.IsVoided, receipt.JournalEntryId!.Value,
                $"إلغاء سند القبض {receipt.ReceiptNumber}", nameof(CustomerReceipt), receiptId);

            _db.Execute(
                "UPDATE customer_receipts SET voided_at = @now, voided_by = @userId, void_reason = @reason, void_journal_entry_id = @reversalId WHERE id = @receiptId",
                new { receiptId, now = _clock.Now, userId = actor.Id, reason = reason.Trim(), reversalId });

            return GetCustomerReceipt(receiptId);
        });

    public CustomerReceipt GetCustomerReceipt(long id) =>
        _db.QueryFirstOrDefault<CustomerReceipt>("SELECT * FROM customer_receipts WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("سند القبض غير موجود.");

    public IReadOnlyList<VoucherRow> CustomerReceipts(DateOnly? from, DateOnly? to, long? customerId = null) =>
        _db.Query<VoucherRow>(
            """
            SELECT r.id, r.receipt_number AS number, r.receipt_date AS date, r.amount, r.payment_method,
                   c.name AS party_name, u.full_name AS user_name, r.reference, r.notes AS description,
                   r.shift_session_id, r.voided_at, r.void_reason
            FROM customer_receipts r
            JOIN customers c ON c.id = r.customer_id
            JOIN users u ON u.id = r.user_id
            WHERE (@from IS NULL OR r.receipt_date >= @from) AND (@to IS NULL OR r.receipt_date <= @to)
              AND (@customerId IS NULL OR r.customer_id = @customerId)
            ORDER BY r.id DESC
            """,
            new { from, to, customerId }).ToList();

    // --- Supplier payments ------------------------------------------------------

    public SupplierPayment PaySupplier(Actor actor, long supplierId, Money amount, PaymentMethod method,
        bool throughDrawer, string? reference = null, string? notes = null)
    {
        actor.Require(Permission.SupplierPay, "ليست لديك صلاحية الدفع للموردين.");
        ValidateAmountAndMethod(amount, method);

        var date = _clock.Today;
        _accounting.ResolveOpenPeriodFor(date);

        return _db.InTransaction(() =>
        {
            var supplier = _parties.GetSupplier(supplierId);
            var balance = _parties.SupplierBalance(supplierId);

            if (amount > balance)
            {
                throw new BusinessRuleException(
                    $"المبلغ المدفوع ({amount.ToDisplay()}) أكبر من المستحق للمورد {supplier.Name} ({balance.ToDisplay()}).");
            }

            var sessionId = DrawerSession(actor, method, throughDrawer);
            var number = _sequences.Next(SequenceService.SupplierPayment, date.Year);

            var id = _db.Insert(
                """
                INSERT INTO supplier_payments
                    (payment_number, supplier_id, payment_date, paid_at, amount, payment_method,
                     shift_session_id, user_id, reference, notes)
                VALUES (@number, @supplierId, @date, @now, @amount, @method, @sessionId, @userId, @reference, @notes)
                RETURNING id
                """,
                new { number, supplierId, date, now = _clock.Now, amount, method = method.ToString(), sessionId, userId = actor.Id, reference = Clean(reference), notes = Clean(notes) });

            var entry = _accounting.Post(
                $"سند صرف {number} للمورد {supplier.Name}",
                [
                    LedgerLine.Debit(AccountCodes.AccountsPayable, amount, supplier.Name),
                    LedgerLine.Credit(SaleService.SettlementAccount(method), amount, supplier.Name),
                ],
                JournalSource.SupplierPayment, date, actor, nameof(SupplierPayment), id);

            _db.Execute("UPDATE supplier_payments SET journal_entry_id = @entryId WHERE id = @id", new { id, entryId = entry.Id });

            return GetSupplierPayment(id);
        });
    }

    public SupplierPayment VoidSupplierPayment(Actor actor, long paymentId, string reason) =>
        _db.InTransaction(() =>
        {
            var payment = GetSupplierPayment(paymentId);
            var reversalId = VoidCommon(actor, reason, payment.IsVoided, payment.JournalEntryId!.Value,
                $"إلغاء سند الصرف {payment.PaymentNumber}", nameof(SupplierPayment), paymentId);

            _db.Execute(
                "UPDATE supplier_payments SET voided_at = @now, voided_by = @userId, void_reason = @reason, void_journal_entry_id = @reversalId WHERE id = @paymentId",
                new { paymentId, now = _clock.Now, userId = actor.Id, reason = reason.Trim(), reversalId });

            return GetSupplierPayment(paymentId);
        });

    public SupplierPayment GetSupplierPayment(long id) =>
        _db.QueryFirstOrDefault<SupplierPayment>("SELECT * FROM supplier_payments WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("سند الصرف غير موجود.");

    public IReadOnlyList<VoucherRow> SupplierPayments(DateOnly? from, DateOnly? to, long? supplierId = null) =>
        _db.Query<VoucherRow>(
            """
            SELECT p.id, p.payment_number AS number, p.payment_date AS date, p.amount, p.payment_method,
                   s.name AS party_name, u.full_name AS user_name, p.reference, p.notes AS description,
                   p.shift_session_id, p.voided_at, p.void_reason
            FROM supplier_payments p
            JOIN suppliers s ON s.id = p.supplier_id
            JOIN users u ON u.id = p.user_id
            WHERE (@from IS NULL OR p.payment_date >= @from) AND (@to IS NULL OR p.payment_date <= @to)
              AND (@supplierId IS NULL OR p.supplier_id = @supplierId)
            ORDER BY p.id DESC
            """,
            new { from, to, supplierId }).ToList();

    // --- Expense vouchers -------------------------------------------------------

    public ExpenseVoucher RecordExpense(Actor actor, long expenseAccountId, Money amount, PaymentMethod method,
        bool throughDrawer, string description, string? payee = null)
    {
        actor.Require(Permission.ExpenseCreate, "ليست لديك صلاحية تسجيل المصروفات.");
        ValidateAmountAndMethod(amount, method);

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new BusinessRuleException("بيان المصروف مطلوب.");
        }

        var date = _clock.Today;
        _accounting.ResolveOpenPeriodFor(date);

        return _db.InTransaction(() =>
        {
            var account = _accounting.GetAccountById(expenseAccountId);

            if (account.Type != AccountType.Expense)
            {
                throw new BusinessRuleException("يجب اختيار حساب من نوع مصروفات.");
            }

            if (!account.IsActive)
            {
                throw new BusinessRuleException($"الحساب {account.Label} موقوف.");
            }

            // Expenses that belong to other flows have their own documents.
            if (account.Code is AccountCodes.CostOfGoodsSold or AccountCodes.CashOverShort
                or AccountCodes.InventoryAdjustment or AccountCodes.SalariesExpense)
            {
                throw new BusinessRuleException($"الحساب {account.Label} يُستخدم تلقائياً من النظام ولا يُحمَّل بسند مصروف.");
            }

            var sessionId = DrawerSession(actor, method, throughDrawer);
            var number = _sequences.Next(SequenceService.ExpenseVoucher, date.Year);

            var id = _db.Insert(
                """
                INSERT INTO expense_vouchers
                    (voucher_number, expense_account_id, voucher_date, paid_at, amount, payment_method,
                     shift_session_id, user_id, payee, description)
                VALUES (@number, @expenseAccountId, @date, @now, @amount, @method, @sessionId, @userId, @payee, @description)
                RETURNING id
                """,
                new { number, expenseAccountId, date, now = _clock.Now, amount, method = method.ToString(), sessionId, userId = actor.Id, payee = Clean(payee), description = description.Trim() });

            var entry = _accounting.Post(
                $"سند مصروف {number}: {description.Trim()}",
                [
                    LedgerLine.Debit(expenseAccountId, amount, description.Trim()),
                    LedgerLine.Credit(SaleService.SettlementAccount(method), amount, Clean(payee) ?? description.Trim()),
                ],
                JournalSource.Expense, date, actor, nameof(ExpenseVoucher), id);

            _db.Execute("UPDATE expense_vouchers SET journal_entry_id = @entryId WHERE id = @id", new { id, entryId = entry.Id });

            return GetExpense(id);
        });
    }

    public ExpenseVoucher VoidExpense(Actor actor, long voucherId, string reason) =>
        _db.InTransaction(() =>
        {
            var voucher = GetExpense(voucherId);
            var reversalId = VoidCommon(actor, reason, voucher.IsVoided, voucher.JournalEntryId!.Value,
                $"إلغاء سند المصروف {voucher.VoucherNumber}", nameof(ExpenseVoucher), voucherId);

            _db.Execute(
                "UPDATE expense_vouchers SET voided_at = @now, voided_by = @userId, void_reason = @reason, void_journal_entry_id = @reversalId WHERE id = @voucherId",
                new { voucherId, now = _clock.Now, userId = actor.Id, reason = reason.Trim(), reversalId });

            return GetExpense(voucherId);
        });

    public ExpenseVoucher GetExpense(long id) =>
        _db.QueryFirstOrDefault<ExpenseVoucher>("SELECT * FROM expense_vouchers WHERE id = @id", new { id })
        ?? throw new BusinessRuleException("سند المصروف غير موجود.");

    public IReadOnlyList<VoucherRow> Expenses(DateOnly? from, DateOnly? to, long? accountId = null) =>
        _db.Query<VoucherRow>(
            """
            SELECT v.id, v.voucher_number AS number, v.voucher_date AS date, v.amount, v.payment_method,
                   a.code || ' - ' || a.name AS party_name, u.full_name AS user_name, v.payee AS reference,
                   v.description, v.shift_session_id, v.voided_at, v.void_reason
            FROM expense_vouchers v
            JOIN accounts a ON a.id = v.expense_account_id
            JOIN users u ON u.id = v.user_id
            WHERE (@from IS NULL OR v.voucher_date >= @from) AND (@to IS NULL OR v.voucher_date <= @to)
              AND (@accountId IS NULL OR v.expense_account_id = @accountId)
            ORDER BY v.id DESC
            """,
            new { from, to, accountId }).ToList();

    /// <summary>Expense accounts an expense voucher may be charged to.</summary>
    public IReadOnlyList<Account> ExpenseAccounts() =>
        _db.Query<Account>(
            """
            SELECT * FROM accounts
            WHERE type = 'Expense' AND is_active = 1 AND code NOT IN (@cogs, @overShort, @adjustment, @salaries)
            ORDER BY code
            """,
            new
            {
                cogs = AccountCodes.CostOfGoodsSold,
                overShort = AccountCodes.CashOverShort,
                adjustment = AccountCodes.InventoryAdjustment,
                salaries = AccountCodes.SalariesExpense,
            }).ToList();

    // --- Shared -----------------------------------------------------------------

    private long? DrawerSession(Actor actor, PaymentMethod method, bool throughDrawer)
    {
        if (!throughDrawer)
        {
            return null;
        }

        if (method != PaymentMethod.Cash)
        {
            throw new BusinessRuleException("حركات درج الوردية تكون نقدية فقط.");
        }

        return _shifts.RequireOpenSessionFor(actor.Id).Id;
    }

    private long VoidCommon(Actor actor, string reason, bool alreadyVoided, long entryId, string memo, string documentType, long documentId)
    {
        actor.Require(Permission.VoucherVoid, "إلغاء السندات من صلاحيات المالك.");

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BusinessRuleException("إلغاء السند يتطلب ذكر السبب.");
        }

        if (alreadyVoided)
        {
            throw new BusinessRuleException("السند ملغى من قبل.");
        }

        var reversal = _accounting.Reverse(entryId, _clock.Today, actor, $"{memo}: {reason.Trim()}",
            sourceDocumentType: documentType, sourceDocumentId: documentId);

        _audit.Record(documentType, documentId, AuditAction.Cancelled, actor, reason: reason);

        return reversal.Id;
    }

    private static void ValidateAmountAndMethod(Money amount, PaymentMethod method)
    {
        if (!amount.IsPositive)
        {
            throw new BusinessRuleException("المبلغ يجب أن يكون أكبر من صفر.");
        }

        if (method == PaymentMethod.Credit)
        {
            throw new BusinessRuleException("السند يُسدَّد نقداً أو ببطاقة أو بتحويل، وليس آجلاً.");
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class VoucherRow
{
    public long Id { get; set; }
    public string Number { get; set; } = "";
    public DateOnly Date { get; set; }
    public Money Amount { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public string PartyName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string? Reference { get; set; }
    public string? Description { get; set; }
    public long? ShiftSessionId { get; set; }
    public DateTime? VoidedAt { get; set; }
    public string? VoidReason { get; set; }

    public bool IsVoided => VoidedAt is not null;

    public bool ThroughDrawer => ShiftSessionId is not null;
}
