using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HisabTak.Application.Accounting;
using HisabTak.Application.Security;
using HisabTak.Domain.Accounting;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed class JournalService(HisabTakDbContext db, IAuthorizationService authorization) : IJournalService
{
    private readonly SqlLedger sql = new(db);

    public async Task SetBaseCurrencyAsync(int actorId, int currencyId, CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await DatabaseLock.AcquireAsync(db, "HisabTak.Accounting.Periods", cancellationToken);
        await authorization.DemandAsync(actorId, PermissionCodes.SetupManage, cancellationToken);
        if (!await db.Currencies.AsNoTracking().AnyAsync(x => x.Id == currencyId && x.IsActive, cancellationToken))
            throw new InvalidOperationException("Base currency is unavailable.");
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(x => x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("Company setup is required.");
        if (company.BaseCurrencyId != currencyId && await sql.ScalarAsync("SELECT COUNT_BIG(*) FROM dbo.JournalEntries WHERE PostedAt IS NOT NULL", cancellationToken)>0)
            throw new InvalidOperationException("Base currency is fixed after posting.");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.CompanyInfo SET BaseCurrencyId={currencyId} WHERE CompanyInfoId={company.Id}", cancellationToken);
        db.AuditLogs.Add(new AuditLog(actorId, "BASE_CURRENCY_SET", "CompanyInfo", company.Id, Environment.MachineName));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<long> PostAsync(int actorId, JournalDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Kind == JournalKind.Reversal) throw new InvalidOperationException("Use the stored-journal reversal service.");
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await DatabaseLock.AcquireAsync(db, "HisabTak.Accounting.Periods", cancellationToken);
        await authorization.DemandAsync(actorId, PermissionCodes.JournalPost, cancellationToken);
        var id = await WriteAsync(actorId, draft, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<long> ReverseAsync(int actorId, long originalId, DateOnly date, Guid sourceKey, CancellationToken cancellationToken = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await DatabaseLock.AcquireAsync(db, "HisabTak.Accounting.Periods", cancellationToken);
        await authorization.DemandAsync(actorId, PermissionCodes.JournalReverse, cancellationToken);
        var original = await ReadAsync(originalId, cancellationToken);
        await AccountingAccess.BranchAsync(db, authorization, actorId, original.BranchId, cancellationToken);
        if (original.Kind == JournalKind.Reversal) throw new InvalidOperationException("A reversal cannot itself be reversed.");
        var originalPeriod = new FiscalPeriod("snapshot", original.EntryDate, original.EntryDate);
        var lines = original.Lines.Select(l => new JournalLineDefinition(l.AccountId, l.Side,
            new BaseCurrencyValuation(new CurrencyAmount(l.Amount,l.CurrencyId),l.BaseCurrencyId,l.RateToBase,l.ValuationDate)));
        var proposal = JournalDraft.Create(original.BranchId, originalPeriod, original.EntryDate, original.SourceKey, lines);
        // The real persisted target period is reloaded and checked inside WriteAsync.
        var reversal = proposal.Reverse(originalId, new FiscalPeriod("target", date, date), date, sourceKey);
        var id = await WriteAsync(actorId, reversal, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return id;
    }

    private async Task<long> WriteAsync(int actorId, JournalDraft draft, CancellationToken ct)
    {
        await AccountingAccess.BranchAsync(db, authorization, actorId, draft.BranchId, ct);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(draft))));
        await using (var existing = await sql.CommandAsync("SELECT JournalEntryId,ContentHash FROM dbo.JournalEntries WHERE SourceKey=@source AND PostedAt IS NOT NULL", ct, ("@source",draft.SourceKey)))
        await using (var reader = await existing.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(1) != hash) throw new InvalidOperationException("Source reference was reused with different journal content.");
                return reader.GetInt64(0);
            }
        }
        var period = await db.FiscalPeriods.AsNoTracking().SingleOrDefaultAsync(x => x.IsActive && x.StartDate<=draft.EntryDate && x.EndDate>=draft.EntryDate, ct)
            ?? throw new InvalidOperationException("Posting date has no active fiscal period.");
        period.EnsurePostingAllowed(draft.EntryDate);
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(x => x.IsActive, ct)
            ?? throw new InvalidOperationException("Company setup is required.");
        if (company.BaseCurrencyId is not int baseId) throw new InvalidOperationException("Choose the company base currency before posting.");
        if (!await db.Currencies.AsNoTracking().AnyAsync(x => x.Id==baseId && x.IsActive, ct)) throw new InvalidOperationException("Base currency is unavailable.");
        if (draft.ReversesEntryId is long originalId && await sql.ScalarAsync("SELECT COUNT_BIG(*) FROM dbo.JournalEntries WHERE ReversesEntryId=@original",ct,("@original",originalId))>0)
            throw new InvalidOperationException("Journal has already been reversed.");
        foreach (var line in draft.Lines)
        {
            var account=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id==line.AccountId,ct)
                ?? throw new InvalidOperationException("Account is unavailable.");
            account.EnsurePostingAllowed(line.Valuation.Original.CurrencyId);
            if (account.BranchId != draft.BranchId) throw new InvalidOperationException("Account must belong to the journal branch.");
            if (!await db.AccountMainGroups.AsNoTracking().AnyAsync(x=>x.Id==account.MainGroupId && x.IsActive,ct)) throw new InvalidOperationException("Account group is unavailable.");
            if (!await db.Currencies.AsNoTracking().AnyAsync(x=>x.Id==account.CurrencyId && x.IsActive,ct)) throw new InvalidOperationException("Currency is unavailable.");
            if (line.Valuation.ExactBaseAmount.CurrencyId!=baseId) throw new InvalidOperationException("Journal base currency differs from the company base currency.");
            if (draft.Kind!=JournalKind.Reversal && line.Valuation.OperationDate!=draft.EntryDate) throw new InvalidOperationException("Historical valuation date must match the original entry date.");
            RequireStorage(line.Valuation.Original.Amount,8,20);
            RequireStorage(line.Valuation.RateToBase,12,16);
            RequireStorage(line.Valuation.ExactBaseAmount.Amount,8,20);
        }
        var id=await sql.ScalarAsync("""
            INSERT dbo.JournalEntries(SourceKey,ContentHash,BranchId,FiscalPeriodId,EntryDate,Kind,ReversesEntryId,CreatedBy)
            VALUES(@source,@hash,@branch,@period,@date,@kind,@original,@actor);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """,ct,("@source",draft.SourceKey),("@hash",hash),("@branch",draft.BranchId),("@period",period.Id),("@date",draft.EntryDate),
            ("@kind",(int)draft.Kind),("@original",draft.ReversesEntryId),("@actor",actorId));
        var number=0;
        foreach(var line in draft.Lines)
            await sql.ExecuteAsync("""
                INSERT dbo.JournalLines(JournalEntryId,LineNumber,AccountId,Side,Amount,CurrencyId,RateToBase,BaseAmount,BaseCurrencyId,ValuationDate)
                VALUES(@id,@number,@account,@side,@amount,@currency,@rate,@base,@baseCurrency,@date)
                """,ct,("@id",id),("@number",++number),("@account",line.AccountId),("@side",(int)line.Side),
                ("@amount",line.Valuation.Original.Amount),("@currency",line.Valuation.Original.CurrencyId),("@rate",line.Valuation.RateToBase),
                ("@base",line.Valuation.ExactBaseAmount.Amount),("@baseCurrency",baseId),("@date",line.Valuation.OperationDate));
        if(draft.Kind==JournalKind.Opening)
            await sql.ExecuteAsync("INSERT dbo.JournalOpeningAccounts(BranchId,AccountId,JournalEntryId) SELECT @branch,AccountId,@id FROM dbo.JournalLines WHERE JournalEntryId=@id GROUP BY AccountId",ct,("@branch",draft.BranchId),("@id",id));
        await sql.ExecuteAsync("UPDATE dbo.JournalEntries SET PostedAt=SYSUTCDATETIME() WHERE JournalEntryId=@id",ct,("@id",id));
        await AuditAsync(actorId,draft.Kind==JournalKind.Reversal?"JOURNAL_REVERSED":"JOURNAL_POSTED",id,ct);
        return id;
    }

    private static void RequireStorage(decimal value,int scale,int integerDigits)
    {
        decimal limit=1m;
        for(var i=0;i<integerDigits;i++) limit*=10m;
        if(decimal.Round(value,scale)!=value || decimal.Abs(value)>=limit)
            throw new ArgumentOutOfRangeException(nameof(value),"Value exceeds exact ledger storage; implicit rounding is forbidden.");
    }

    public async Task<PostedJournal> GetAsync(int actorId,long journalId,CancellationToken cancellationToken=default)
    {
        await authorization.DemandAsync(actorId,PermissionCodes.JournalRead,cancellationToken);
        var entry=await ReadAsync(journalId,cancellationToken);
        await AccountingAccess.BranchAsync(db,authorization,actorId,entry.BranchId,cancellationToken);
        return entry;
    }

    private async Task<PostedJournal> ReadAsync(long id,CancellationToken ct)
    {
        Guid source; int branch,period; DateOnly date; JournalKind kind; long? original;
        await using(var command=await sql.CommandAsync("SELECT SourceKey,BranchId,FiscalPeriodId,EntryDate,Kind,ReversesEntryId FROM dbo.JournalEntries WHERE JournalEntryId=@id AND PostedAt IS NOT NULL",ct,("@id",id)))
        await using(var reader=await command.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct)) throw new InvalidOperationException("Posted journal was not found.");
            source=reader.GetGuid(0);branch=reader.GetInt32(1);period=reader.GetInt32(2);date=DateOnly.FromDateTime(reader.GetDateTime(3));kind=(JournalKind)reader.GetInt32(4);original=reader.IsDBNull(5)?null:reader.GetInt64(5);
        }
        var lines=new List<LedgerLineSnapshot>();
        await using(var command=await sql.CommandAsync("SELECT AccountId,Side,Amount,CurrencyId,RateToBase,BaseAmount,BaseCurrencyId,ValuationDate FROM dbo.JournalLines WHERE JournalEntryId=@id ORDER BY LineNumber",ct,("@id",id)))
        await using(var reader=await command.ExecuteReaderAsync(ct))
            while(await reader.ReadAsync(ct)) lines.Add(new(reader.GetInt32(0),(JournalSide)reader.GetInt32(1),reader.GetDecimal(2),reader.GetInt32(3),reader.GetDecimal(4),reader.GetDecimal(5),reader.GetInt32(6),DateOnly.FromDateTime(reader.GetDateTime(7))));
        return new(id,source,branch,period,date,kind,original,lines.AsReadOnly());
    }

    public async Task<IReadOnlyList<LedgerBalance>> BalancesAsync(int actorId,int branchId,int currencyId,DateOnly asOf,CancellationToken cancellationToken=default)
    {
        if(currencyId<=0) throw new ArgumentOutOfRangeException(nameof(currencyId));
        await authorization.DemandAsync(actorId,PermissionCodes.JournalRead,cancellationToken);
        await AccountingAccess.BranchAsync(db,authorization,actorId,branchId,cancellationToken);
        var rows=new List<LedgerBalance>();
        await using var command=await sql.CommandAsync("""
            SELECT l.AccountId,SUM(CASE WHEN Side=1 THEN Amount ELSE 0 END),SUM(CASE WHEN Side=2 THEN Amount ELSE 0 END),
                SUM(CASE WHEN Side=1 THEN BaseAmount ELSE 0 END),SUM(CASE WHEN Side=2 THEN BaseAmount ELSE 0 END)
            FROM dbo.JournalLines l JOIN dbo.JournalEntries e ON e.JournalEntryId=l.JournalEntryId
            WHERE e.PostedAt IS NOT NULL AND e.BranchId=@branch AND e.EntryDate<=@date AND l.CurrencyId=@currency
            GROUP BY l.AccountId ORDER BY l.AccountId
            """,cancellationToken,("@branch",branchId),("@date",asOf),("@currency",currencyId));
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken)) rows.Add(new(reader.GetInt32(0),reader.GetDecimal(1),reader.GetDecimal(2),reader.GetDecimal(3),reader.GetDecimal(4)));
        return rows.AsReadOnly();
    }

    private async Task AuditAsync(int actorId,string action,long id,CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog(actorId,action,"JournalEntries",id,Environment.MachineName));
        await db.SaveChangesAsync(ct);
    }
}
