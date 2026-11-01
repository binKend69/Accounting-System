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

    public async Task<JournalDocument> SaveDraftAsync(int actorId, JournalDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Kind is not (JournalKind.Standard or JournalKind.Opening)) throw new InvalidOperationException("Unsupported document type.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAndAuthorizeAsync(actorId, PermissionCodes.JournalPost, cancellationToken);
        await AccountingAccess.BranchAsync(db, authorization, actorId, draft.BranchId, cancellationToken);
        static decimal Normalize(decimal value) => decimal.Parse(value.ToString("G29", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var payload = JsonSerializer.Serialize(new Payload(draft.Kind, draft.BranchId, draft.EntryDate, draft.SourceKey,
            draft.Lines.Select(l => new Line(l.AccountId, l.Side, Normalize(l.Valuation.Original.Amount), l.Valuation.Original.CurrencyId,
                Normalize(l.Valuation.RateToBase), l.Valuation.ExactBaseAmount.CurrencyId, l.Valuation.OperationDate)).ToArray()));
        var existing = await sql.ScalarAsync("SELECT DocumentId FROM dbo.JournalDocuments WHERE SourceKey=@source", cancellationToken, ("@source", draft.SourceKey));
        if (existing != 0)
        {
            var (saved, originalPayload) = await ReadAsync(existing, cancellationToken);
            if (payload != originalPayload) throw new InvalidOperationException("Source reference was reused with different document content.");
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        if (await sql.ScalarAsync("SELECT COUNT_BIG(*) FROM dbo.JournalEntries WHERE SourceKey=@source", cancellationToken, ("@source", draft.SourceKey)) != 0)
            throw new InvalidOperationException("Source reference already belongs to a standalone journal.");
        var type = draft.Kind == JournalKind.Opening ? "JOURNAL_OPENING" : "JOURNAL_VOUCHER";
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

    public async Task<JournalDocument> PostAsync(int actorId, long documentId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAndAuthorizeAsync(actorId, PermissionCodes.JournalPost, cancellationToken);
        var (document, json) = await ReadAsync(documentId, cancellationToken);
        await AccountingAccess.BranchAsync(db, authorization, actorId, document.BranchId, cancellationToken);
        if (document.State != JournalDocumentState.Draft)
        {
            await transaction.CommitAsync(cancellationToken);
            return document; // Includes cancelled originals: never post their effects again.
        }
        var payload = JsonSerializer.Deserialize<Payload>(json) ?? throw new InvalidOperationException("Missing document payload.");
        var period = new FiscalPeriod("proposal", payload.Date, payload.Date);
        var lines = payload.Lines.Select(l => new JournalLineDefinition(l.AccountId, l.Side,
            new(new(l.Amount, l.CurrencyId), l.BaseCurrencyId, l.Rate, l.ValuationDate)));
        var draft = payload.Kind == JournalKind.Opening
            ? JournalDraft.CreateOpening(payload.BranchId, period, payload.Date, payload.SourceKey, lines)
            : JournalDraft.Create(payload.BranchId, period, payload.Date, payload.SourceKey, lines);
        var number = await sql.ScalarAsync("""
            UPDATE dbo.DocumentNumbers SET LastNumber=LastNumber+1 WHERE BranchId=@branch AND TypeCode=@type AND DocumentYear=@year;
            IF @@ROWCOUNT=0 INSERT dbo.DocumentNumbers(BranchId,TypeCode,DocumentYear,LastNumber) VALUES(@branch,@type,@year,1);
            SELECT LastNumber FROM dbo.DocumentNumbers WHERE BranchId=@branch AND TypeCode=@type AND DocumentYear=@year;
            """, cancellationToken, ("@branch", document.BranchId), ("@type", document.TypeCode), ("@year", document.Date.Year));
        var journalId = await journals.PostDocumentAsync(actorId, draft, cancellationToken);
        await sql.ExecuteAsync("UPDATE dbo.JournalDocuments SET DocumentState=2,DocumentNumber=@number,JournalEntryId=@journal WHERE DocumentId=@id",
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
        await sql.ExecuteAsync("UPDATE dbo.JournalDocuments SET DocumentState=3,ReversalEntryId=@reversal WHERE DocumentId=@id",
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
            SELECT DocumentId,BranchId,TypeCode,EntryDate,SourceKey,DocumentState,DocumentNumber,JournalEntryId,ReversalEntryId,Payload
            FROM dbo.JournalDocuments WHERE DocumentId=@id
            """, ct, ("@id", id));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Journal document not found.");
        return (new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), DateOnly.FromDateTime(reader.GetDateTime(3)), reader.GetGuid(4),
            (JournalDocumentState)reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8)), reader.GetString(9));
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
