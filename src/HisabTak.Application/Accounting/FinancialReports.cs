namespace HisabTak.Application.Accounting;
public sealed record TrialBalanceRow(int AccountId,string Code,string Name,decimal OpeningDebit,decimal OpeningCredit,
    decimal PeriodDebit,decimal PeriodCredit,decimal ClosingDebit,decimal ClosingCredit);
public interface IFinancialReports
{
    // null selects historical book amounts in base currency, never summed face currencies.
    Task<IReadOnlyList<TrialBalanceRow>> TrialBalanceAsync(int actor,int branch,int? currency,DateOnly from,DateOnly to,CancellationToken ct=default);
}
