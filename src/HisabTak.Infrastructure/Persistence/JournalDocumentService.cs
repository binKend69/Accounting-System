using System.Globalization;
using System.Text.Json;
using HisabTak.Application.Accounting;
using HisabTak.Application.Security;
using HisabTak.Domain.Accounting;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

/// <summary>Owns the transaction; journal posting participates without committing it.</summary>
public sealed class JournalDocumentService(HisabTakDbContext db, IAuthorizationService authorization,
    JournalService journals) : IJournalDocumentService
{
    private readonly SqlLedger sql = new(db);
    private sealed record Line(int AccountId, JournalSide Side, decimal Amount, int CurrencyId,
        decimal Rate, int BaseCurrencyId, DateOnly ValuationDate);
    private sealed record Payload(JournalKind Kind, int BranchId, DateOnly Date, Guid SourceKey, Line[] Lines);

    public Task<JournalDocument> SaveDraftAsync(int actorId, JournalDraft draft, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(actorId, draft, draft.Kind == JournalKind.Opening ? "JOURNAL_OPENING" : "JOURNAL_VOUCHER", cancellationToken);

    public Task<JournalDocument> SaveVoucherAsync(int actorId, JournalDraft draft, string typeCode, CancellationToken cancellationToken = default)
    {
        if (typeCode is not ("RECEIPT_VOUCHER" or "PAYMENT_VOUCHER")) throw new ArgumentException("Unsupported voucher type.", nameof(typeCode));
        return SaveCoreAsync(actorId, draft, typeCode, cancellationToken);
    }

    private async Task<JournalDocument> SaveCoreAsync(int actorId, JournalDraft draft, string type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Kind is not (JournalKind.Standard or JournalKind.Opening)) throw new InvalidOperationException("Unsupported document type.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAndAuthorizeAsync(actorId, PermissionCodes.JournalPost, cancellationToken);
        await AccountingAccess.BranchAsync(db, authorization, actorId, draft.BranchId, cancellationToken);
        await ValidateVoucherAsync(draft, type, cancellationToken);
        var payload = Serialize(draft);
        var existing = await sql.ScalarAsync("SELECT DocumentId FROM dbo.JournalDocuments WHERE SourceKey=@source", cancellationToken, ("@source", draft.SourceKey));
        if (existing != 0)
        {
            var (saved, originalPayload) = await ReadAsync(existing, cancellationToken);
            if (payload != originalPayload || saved.TypeCode != type) throw new InvalidOperationException("Source reference was reused with different document content.");
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        if (await sql.ScalarAsync("SELECT COUNT_BIG(*) FROM dbo.JournalEntries WHERE SourceKey=@source", cancellationToken, ("@source", draft.SourceKey)) != 0)
            throw new InvalidOperationException("Source reference already belongs to a standalone journal.");
        var id = await sql.ScalarAsync("""
            INSERT dbo.JournalDocuments(BranchId,TypeCode,EntryDate,SourceKey,DocumentState,Payload,CreatedBy)
            VALUES(@branch,@type,@date,@source,1,@payload,@actor);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """, cancellationToken, ("@branch", draft.BranchId), ("@type", type), ("@date", draft.EntryDate),
            ("@source", draft.SourceKey), ("@payload", payload), ("@actor", actorId));
        await AuditAsync(actorId, "DOCUMENT_DRAFT_SAVED", id, cancellationToken);
        var result = (await ReadAsync(id, cancellationToken)).Document;
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<JournalDocument> PostAsync(int actorId, long documentId, CancellationToken cancellationToken = default) =>
        PostCoreAsync(actorId, documentId, null, cancellationToken);

    public Task<JournalDocument> PostVersionAsync(int actorId, long documentId, int expectedVersion, CancellationToken cancellationToken = default) =>
        PostCoreAsync(actorId, documentId, expectedVersion, cancellationToken);

    private async Task<JournalDocument> PostCoreAsync(int actorId, long documentId, int? expectedVersion, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAndAuthorizeAsync(actorId, PermissionCodes.JournalPost, cancellationToken);
        var (document, json) = await ReadAsync(documentId, cancellationToken);
        await AccountingAccess.BranchAsync(db, authorization, actorId, document.BranchId, cancellationToken);
        if (document.State != JournalDocumentState.Draft)
        {
            var postedFromVersion = document.Version - (document.State == JournalDocumentState.Reversed ? 2 : 1);
            if (expectedVersion is not null && expectedVersion != postedFromVersion) throw new DocumentConflictException();
            await transaction.CommitAsync(cancellationToken);
            return document; // Includes cancelled originals: never post their effects again.
        }
        if (expectedVersion is not null && expectedVersion != document.Version) throw new DocumentConflictException();
        var draft = Deserialize(json);
        await ValidateVoucherAsync(draft, document.TypeCode, cancellationToken);
        var number = await sql.ScalarAsync("""
            UPDATE dbo.DocumentNumbers SET LastNumber=LastNumber+1 WHERE BranchId=@branch AND TypeCode=@type AND DocumentYear=@year;
            IF @@ROWCOUNT=0 INSERT dbo.DocumentNumbers(BranchId,TypeCode,DocumentYear,LastNumber) VALUES(@branch,@type,@year,1);
            SELECT LastNumber FROM dbo.DocumentNumbers WHERE BranchId=@branch AND TypeCode=@type AND DocumentYear=@year;
            """, cancellationToken, ("@branch", document.BranchId), ("@type", document.TypeCode), ("@year", document.Date.Year));
        var journalId = await journals.PostDocumentAsync(actorId, draft, cancellationToken);
        await sql.ExecuteAsync("UPDATE dbo.JournalDocuments SET Version=Version+1,DocumentState=2,DocumentNumber=@number,JournalEntryId=@journal WHERE DocumentId=@id",
            cancellationToken, ("@number", number), ("@journal", journalId), ("@id", documentId));
        await AuditAsync(actorId, "DOCUMENT_POSTED", documentId, cancellationToken);
        var result = (await ReadAsync(documentId, cancellationToken)).Document;
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<JournalDocument> ReverseAsync(int actorId, long documentId, DateOnly reversalDate, Guid sourceKey, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAndAuthorizeAsync(actorId, PermissionCodes.JournalReverse, cancellationToken);
        var document = (await ReadAsync(documentId, cancellationToken)).Document;
        await AccountingAccess.BranchAsync(db, authorization, actorId, document.BranchId, cancellationToken);
        if (document.State == JournalDocumentState.Draft) throw new InvalidOperationException("An unposted draft cannot be reversed.");
        if (document.State == JournalDocumentState.Reversed)
        {
            await transaction.CommitAsync(cancellationToken);
            return document;
        }
        var reversal = await journals.ReverseDocumentAsync(actorId, document.JournalId!.Value, reversalDate, sourceKey, cancellationToken);
        await sql.ExecuteAsync("UPDATE dbo.JournalDocuments SET Version=Version+1,DocumentState=3,ReversalEntryId=@reversal WHERE DocumentId=@id",
            cancellationToken, ("@reversal", reversal), ("@id", documentId));
        await AuditAsync(actorId, "DOCUMENT_REVERSED", documentId, cancellationToken);
        var result = (await ReadAsync(documentId, cancellationToken)).Document;
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<JournalDocument> GetAsync(int actorId, long documentId, CancellationToken cancellationToken = default)
    {
        await authorization.DemandAsync(actorId, PermissionCodes.JournalRead, cancellationToken);
        var result = (await ReadAsync(documentId, cancellationToken)).Document;
        await AccountingAccess.BranchAsync(db, authorization, actorId, result.BranchId, cancellationToken);
        return result;
    }

    private async Task<(JournalDocument Document, string Payload)> ReadAsync(long id, CancellationToken ct)
    {
        await using var command = await sql.CommandAsync("""
            SELECT DocumentId,BranchId,TypeCode,EntryDate,SourceKey,DocumentState,DocumentNumber,JournalEntryId,ReversalEntryId,Payload,Version
            FROM dbo.JournalDocuments WHERE DocumentId=@id
            """, ct, ("@id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Journal document not found.");
        return (new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), DateOnly.FromDateTime(reader.GetDateTime(3)), reader.GetGuid(4),
            (JournalDocumentState)reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.GetInt32(10)), reader.GetString(9));
    }

    private static string Serialize(JournalDraft draft)
    {
        static decimal Normalize(decimal value) => decimal.Parse(value.ToString("G29", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return JsonSerializer.Serialize(new Payload(draft.Kind, draft.BranchId, draft.EntryDate, draft.SourceKey,
            draft.Lines.Select(l => new Line(l.AccountId, l.Side, Normalize(l.Valuation.Original.Amount), l.Valuation.Original.CurrencyId,
                Normalize(l.Valuation.RateToBase), l.Valuation.ExactBaseAmount.CurrencyId, l.Valuation.OperationDate)).ToArray()));
    }

    private static JournalDraft Deserialize(string json)
    {
        var payload = JsonSerializer.Deserialize<Payload>(json) ?? throw new InvalidOperationException("Missing document payload.");
        var period = new FiscalPeriod("proposal", payload.Date, payload.Date);
        var lines = payload.Lines.Select(l => new JournalLineDefinition(l.AccountId, l.Side,
            new(new(l.Amount, l.CurrencyId), l.BaseCurrencyId, l.Rate, l.ValuationDate)));
        return payload.Kind == JournalKind.Opening
            ? JournalDraft.CreateOpening(payload.BranchId, period, payload.Date, payload.SourceKey, lines)
            : JournalDraft.Create(payload.BranchId, period, payload.Date, payload.SourceKey, lines);
    }

    private async Task ValidateVoucherAsync(JournalDraft draft, string type, CancellationToken ct)
    {
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

    public async Task<JournalDocument> UpdateDraftAsync(int actorId, long documentId, int expectedVersion, JournalDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAndAuthorizeAsync(actorId, PermissionCodes.JournalPost, cancellationToken);
        var (document, oldPayload) = await ReadAsync(documentId, cancellationToken);
        await AccountingAccess.BranchAsync(db, authorization, actorId, document.BranchId, cancellationToken);
        if (document.State != JournalDocumentState.Draft) throw new InvalidOperationException("Posted documents cannot be edited.");
        if (draft.BranchId != document.BranchId || draft.SourceKey != document.SourceKey || draft.Kind != Deserialize(oldPayload).Kind)
            throw new InvalidOperationException("Document identity and type cannot change.");
        await ValidateVoucherAsync(draft, document.TypeCode, cancellationToken);
        var payload = Serialize(draft);
        // A retry of the exact successful edit is safe, but a stale different edit is not.
        if (document.Version == expectedVersion + 1 && payload == oldPayload)
        {
            await transaction.CommitAsync(cancellationToken); return document;
        }
        if (document.Version != expectedVersion) throw new DocumentConflictException();
        await sql.ExecuteAsync("UPDATE dbo.JournalDocuments SET Payload=@payload,EntryDate=@date,Version=Version+1 WHERE DocumentId=@id",
            cancellationToken, ("@payload", payload), ("@date", draft.EntryDate), ("@id", documentId));
        await AuditAsync(actorId, "DOCUMENT_DRAFT_UPDATED", documentId, cancellationToken);
        var result = (await ReadAsync(documentId, cancellationToken)).Document;
        await transaction.CommitAsync(cancellationToken); return result;
    }

    public async Task<JournalDocumentDetails> DetailsAsync(int actorId, long documentId, CancellationToken cancellationToken = default)
    {
        await authorization.DemandAsync(actorId, PermissionCodes.JournalRead, cancellationToken);
        var (document, payload) = await ReadAsync(documentId, cancellationToken);
        await AccountingAccess.BranchAsync(db, authorization, actorId, document.BranchId, cancellationToken);
        return new(document, Deserialize(payload));
    }

    public async Task<IReadOnlyList<JournalDocument>> ListAsync(int actorId, int branchId, int page = 0, CancellationToken cancellationToken = default)
    {
        if (page < 0 || page > 1000000) throw new ArgumentOutOfRangeException(nameof(page));
        await authorization.DemandAsync(actorId, PermissionCodes.JournalRead, cancellationToken);
        await AccountingAccess.BranchAsync(db, authorization, actorId, branchId, cancellationToken);
        var ids = new List<long>();
        await using (var command = await sql.CommandAsync("SELECT DocumentId FROM dbo.JournalDocuments WHERE BranchId=@branch ORDER BY DocumentId DESC OFFSET @offset ROWS FETCH NEXT 50 ROWS ONLY",
            cancellationToken, ("@branch", branchId), ("@offset", page * 50)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt64(0));
        var result = new List<JournalDocument>();
        foreach (var id in ids) result.Add((await ReadAsync(id, cancellationToken)).Document);
        return result.AsReadOnly();
    }

    private async Task LockAndAuthorizeAsync(int actor, string permission, CancellationToken ct)
    {
        await DatabaseLock.AcquireAsync(db, "HisabTak.Accounting.Periods", ct);
        await authorization.DemandAsync(actor, permission, ct);
    }
    private async Task AuditAsync(int actor, string action, long id, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog(actor, action, "JournalDocuments", id, Environment.MachineName));
        await db.SaveChangesAsync(ct);
    }
}
