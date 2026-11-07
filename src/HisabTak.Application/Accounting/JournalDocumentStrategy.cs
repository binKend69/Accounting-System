using HisabTak.Domain.Accounting;

namespace HisabTak.Application.Accounting;

public interface IJournalDocumentStrategy
{
    string TypeCode { get; }
    Task ValidateAsync(JournalDraft draft, CancellationToken cancellationToken = default);
}
