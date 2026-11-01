using HisabTak.Application.Accounting;
using HisabTak.Domain.Accounting;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed class JournalDocumentStrategy(HisabTakDbContext db, string typeCode, JournalKind kind) : IJournalDocumentStrategy
{
    public string TypeCode { get; } = typeCode;
    public async Task ValidateAsync(JournalDraft draft, CancellationToken ct = default)
    {
        if (draft.Kind != kind) throw new InvalidOperationException("Document kind does not match its strategy.");
        var type = TypeCode;
        if (type is not ("RECEIPT_VOUCHER" or "PAYMENT_VOUCHER")) return;
        if (draft.Kind != JournalKind.Standard || draft.Lines.Count != 2 || draft.Lines[0].AccountId == draft.Lines[1].AccountId)
            throw new InvalidOperationException("A cash voucher requires exactly two distinct accounts.");
        var cashSide = type == "RECEIPT_VOUCHER" ? JournalSide.Debit : JournalSide.Credit;
        var cash = draft.Lines.SingleOrDefault(l => l.Side == cashSide) ?? throw new InvalidOperationException("Cash voucher side is invalid.");
        var other = draft.Lines.Single(l => l != cash);
        if (cash.Valuation.Original.CurrencyId != other.Valuation.Original.CurrencyId
            || cash.Valuation.Original.Amount != other.Valuation.Original.Amount
            || cash.Valuation.RateToBase != other.Valuation.RateToBase)
            throw new InvalidOperationException("Basic cash vouchers require matching currencies and rates; debt settlements use their own workflow.");
        var valid = await (from a in db.Accounts.AsNoTracking()
            join g in db.AccountGeneralGroups.AsNoTracking() on a.GeneralGroupId equals g.Id
            where a.Id == cash.AccountId && a.BranchId == draft.BranchId && a.IsActive && !a.IsSuspended
                && g.IsActive && (g.SystemCode == "CASH" || g.SystemCode == "BANK") select a.Id).AnyAsync(ct);
        if (!valid) throw new InvalidOperationException("The cash side requires an active cash or bank account in this branch.");
    }

}
