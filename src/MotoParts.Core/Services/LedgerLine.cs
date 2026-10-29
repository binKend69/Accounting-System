using MotoParts.Core.Common;

namespace MotoParts.Core.Services;

/// <summary>
/// One side of a journal entry, as handed to <see cref="AccountingService.Post"/>.
///
/// Built only through <see cref="Debit(string, Money, string?)"/> and
/// <see cref="Credit(string, Money, string?)"/>, so a caller cannot construct a
/// two-sided, zero or negative line - the shapes the schema would otherwise
/// reject at runtime.
/// </summary>
public sealed class LedgerLine
{
    private LedgerLine(string? accountCode, long? accountId, Money debit, Money credit, string? memo)
    {
        AccountCode = accountCode;
        AccountId = accountId;
        DebitAmount = debit;
        CreditAmount = credit;
        Memo = memo;
    }

    /// <summary>Set when the account is a system account addressed by code.</summary>
    public string? AccountCode { get; }

    /// <summary>Set when the account was picked by the operator.</summary>
    public long? AccountId { get; }

    public Money DebitAmount { get; }

    public Money CreditAmount { get; }

    public string? Memo { get; }

    public bool IsDebit => DebitAmount.IsPositive;

    public Money Amount => IsDebit ? DebitAmount : CreditAmount;

    public static LedgerLine Debit(string accountCode, Money amount, string? memo = null) =>
        new(accountCode, null, Positive(amount, "مدين"), Money.Zero, memo);

    public static LedgerLine Credit(string accountCode, Money amount, string? memo = null) =>
        new(accountCode, null, Money.Zero, Positive(amount, "دائن"), memo);

    public static LedgerLine Debit(long accountId, Money amount, string? memo = null) =>
        new(null, accountId, Positive(amount, "مدين"), Money.Zero, memo);

    public static LedgerLine Credit(long accountId, Money amount, string? memo = null) =>
        new(null, accountId, Money.Zero, Positive(amount, "دائن"), memo);

    /// <summary>The same line on the opposite side, for building reversals.</summary>
    public LedgerLine Inverted() => IsDebit
        ? new LedgerLine(AccountCode, AccountId, Money.Zero, DebitAmount, Memo)
        : new LedgerLine(AccountCode, AccountId, CreditAmount, Money.Zero, Memo);

    private static Money Positive(Money amount, string side)
    {
        if (amount.IsNegative)
        {
            throw new BusinessRuleException($"مبلغ الطرف ال{side} لا يمكن أن يكون سالباً ({amount}).");
        }

        if (amount.IsZero)
        {
            throw new BusinessRuleException($"سطر القيد ال{side} يجب أن يحمل مبلغاً أكبر من صفر.");
        }

        return amount;
    }
}
