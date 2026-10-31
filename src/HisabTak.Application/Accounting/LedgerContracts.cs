using HisabTak.Domain.Accounting;

namespace HisabTak.Application.Accounting;

public interface IChartOfAccountsService
{
    Task<int> CreateGroupAsync(int actorId, string code, string name, int? parentId, CancellationToken cancellationToken = default);
    Task MoveGroupAsync(int actorId, int groupId, int? parentId, CancellationToken cancellationToken = default);
    Task<int> CreateAccountAsync(int actorId, string code, string name, int groupId, int currencyId, byte nature, int branchId, CancellationToken cancellationToken = default);
    Task SetAccountSuspendedAsync(int actorId, int accountId, bool suspended, CancellationToken cancellationToken = default);
}

public sealed record LedgerLineSnapshot(int AccountId, JournalSide Side, decimal Amount, int CurrencyId,
    decimal RateToBase, decimal BaseAmount, int BaseCurrencyId, DateOnly ValuationDate, decimal? BookBaseAmount = null, bool IsRounding = false);
public sealed record PostedJournal(long Id, Guid SourceKey, int BranchId, int PeriodId, DateOnly EntryDate,
    JournalKind Kind, long? ReversesEntryId, IReadOnlyList<LedgerLineSnapshot> Lines, int? RoundingPrecision = null);
public sealed record LedgerBalance(int AccountId, decimal Debit, decimal Credit, decimal BaseDebit, decimal BaseCredit);

public interface IJournalService
{
    Task ConfigureRoundingAsync(int actorId, int branchId, int accountId, int precision, CancellationToken cancellationToken = default);
    Task SetBaseCurrencyAsync(int actorId, int currencyId, CancellationToken cancellationToken = default);
    Task<long> PostAsync(int actorId, JournalDraft draft, CancellationToken cancellationToken = default);
    Task<long> ReverseAsync(int actorId, long originalId, DateOnly date, Guid sourceKey, CancellationToken cancellationToken = default);
    Task<PostedJournal> GetAsync(int actorId, long journalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LedgerBalance>> BalancesAsync(int actorId, int branchId, int currencyId, DateOnly asOf, CancellationToken cancellationToken = default);
}
