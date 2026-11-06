using System.Globalization;
using MotoParts.Core.Data;

namespace MotoParts.Core.Services;

/// <summary>
/// Issues gapless, sequential, per-year document numbers: INV-2026-000001.
///
/// The counter is read and advanced inside the caller's write transaction, so
/// if the document fails the counter rolls back with it and the number is
/// reused. Allocating outside a transaction would leave a hole on every failed
/// checkout, and tax authorities that require gapless invoice numbers do not
/// accept holes. That is why this refuses to run outside one.
/// </summary>
public sealed class SequenceService
{
    public const string Invoice = "invoice";
    public const string JournalEntry = "journal_entry";
    public const string Purchase = "purchase";
    public const string Refund = "refund";
    public const string CustomerReceipt = "customer_receipt";
    public const string SupplierPayment = "supplier_payment";
    public const string ExpenseVoucher = "expense_voucher";
    public const string Payroll = "payroll";

    private static readonly IReadOnlyDictionary<string, string> Prefixes = new Dictionary<string, string>
    {
        [Invoice] = "INV",
        [JournalEntry] = "JE",
        [Purchase] = "PO",
        [Refund] = "RFD",
        [CustomerReceipt] = "RCV",
        [SupplierPayment] = "PAY",
        [ExpenseVoucher] = "EXP",
        [Payroll] = "PRL",
    };

    private readonly DbSession _db;

    public SequenceService(DbSession db)
    {
        _db = db;
    }

    /// <summary>Reserves the next number for a document kind, e.g. "INV-2026-000042".</summary>
    public string Next(string key, int year)
    {
        if (!Prefixes.TryGetValue(key, out var prefix))
        {
            throw new ArgumentException($"Unknown sequence '{key}'.", nameof(key));
        }

        var value = NextValue(key, year);

        return string.Create(CultureInfo.InvariantCulture, $"{prefix}-{year}-{value:000000}");
    }

    public long NextValue(string key, int year)
    {
        _db.RequireTransaction($"Sequence '{key}'");

        // Creates the row for a new key/year; a no-op once it exists. Under the
        // write lock held by the transaction, the read below cannot race.
        _db.Execute(
            "INSERT OR IGNORE INTO number_sequences (sequence_key, year, next_value) VALUES (@key, @year, 1)",
            new { key, year });

        var value = _db.QuerySingle<long>(
            "SELECT next_value FROM number_sequences WHERE sequence_key = @key AND year = @year",
            new { key, year });

        _db.Execute(
            "UPDATE number_sequences SET next_value = next_value + 1 WHERE sequence_key = @key AND year = @year",
            new { key, year });

        return value;
    }
}
