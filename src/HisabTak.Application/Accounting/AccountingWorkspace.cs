namespace HisabTak.Application.Accounting;

public sealed record AccountingLookup(int Id, string Label);
public sealed record FiscalPeriodLookup(int Id, string Label);
public sealed record AccountLookup(int Id, string Label, int CurrencyId, int? GeneralGroupId);
public sealed record AccountingWorkspace(IReadOnlyList<AccountingLookup> Branches, IReadOnlyList<AccountLookup> Accounts,
    IReadOnlyList<AccountingLookup> Currencies, IReadOnlyList<AccountingLookup> Groups,
    IReadOnlyList<AccountingLookup> GeneralGroups, int? BaseCurrencyId, IReadOnlyList<FiscalPeriodLookup> Periods);
public interface IAccountingWorkspaceService
{
    Task<AccountingWorkspace> LoadAsync(int actorId, int? branchId, CancellationToken cancellationToken = default);
}
