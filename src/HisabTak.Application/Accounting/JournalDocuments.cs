using HisabTak.Domain.Accounting;

namespace HisabTak.Application.Accounting;

public enum JournalDocumentState { Draft = 1, Posted = 2, Reversed = 3 }
public sealed record JournalDocument(long Id, int BranchId, string TypeCode, DateOnly Date,
    Guid SourceKey, JournalDocumentState State, long? Number, long? JournalId, long? ReversalId, int Version = 1);

public sealed record JournalDocumentDetails(JournalDocument Document, JournalDraft Draft);
public sealed class DocumentConflictException() : InvalidOperationException("The document changed. Reload before saving or posting.");

public interface IJournalDocumentService
{
    Task<JournalDocument> SaveVoucherAsync(int actorId, JournalDraft draft, string typeCode, CancellationToken cancellationToken = default);
    Task<JournalDocument> UpdateDraftAsync(int actorId, long documentId, int expectedVersion, JournalDraft draft, CancellationToken cancellationToken = default);
    Task<JournalDocument> PostVersionAsync(int actorId, long documentId, int expectedVersion, CancellationToken cancellationToken = default);
    Task<JournalDocumentDetails> DetailsAsync(int actorId, long documentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JournalDocument>> ListAsync(int actorId, int branchId, int page = 0, CancellationToken cancellationToken = default);
    Task<JournalDocument> SaveDraftAsync(int actorId, JournalDraft draft, CancellationToken cancellationToken = default);
    Task<JournalDocument> PostAsync(int actorId, long documentId, CancellationToken cancellationToken = default);
    Task<JournalDocument> ReverseAsync(int actorId, long documentId, DateOnly reversalDate, Guid sourceKey, CancellationToken cancellationToken = default);
    Task<JournalDocument> GetAsync(int actorId, long documentId, CancellationToken cancellationToken = default);
}
