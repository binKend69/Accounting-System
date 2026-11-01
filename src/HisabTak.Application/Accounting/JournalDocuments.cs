using HisabTak.Domain.Accounting;

namespace HisabTak.Application.Accounting;

public enum JournalDocumentState { Draft = 1, Posted = 2, Reversed = 3 }
public sealed record JournalDocument(long Id, int BranchId, string TypeCode, DateOnly Date,
    Guid SourceKey, JournalDocumentState State, long? Number, long? JournalId, long? ReversalId);

public interface IJournalDocumentService
{
    Task<JournalDocument> SaveDraftAsync(int actorId, JournalDraft draft, CancellationToken cancellationToken = default);
    Task<JournalDocument> PostAsync(int actorId, long documentId, CancellationToken cancellationToken = default);
    Task<JournalDocument> ReverseAsync(int actorId, long documentId, DateOnly reversalDate, Guid sourceKey, CancellationToken cancellationToken = default);
    Task<JournalDocument> GetAsync(int actorId, long documentId, CancellationToken cancellationToken = default);
}
