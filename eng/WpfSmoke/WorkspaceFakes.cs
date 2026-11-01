using HisabTak.Application.Accounting;
using HisabTak.Application.Security;
using HisabTak.Domain.Accounting;

internal sealed class SmokeAuthentication : IAuthenticationService
{
    public Task<AuthenticatedUser?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default) =>
        Task.FromResult<AuthenticatedUser?>(username == "smoke" && password == "smoke-only" ? new(1, "اختبار الواجهة") : null);
}
internal sealed class SmokeWorkspace : IAccountingWorkspaceService
{
    public Task<AccountingWorkspace> LoadAsync(int actorId, int? branchId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AccountingWorkspace([new(1,"الفرع التجريبي")], [new(1,"101 — صندوق",1,1),new(2,"201 — حساب مقابل",1,null)],
            [new(1,"YER — ريال يمني")], [new(1,"الأصول")], [new(1,"صناديق")], 1, [new(1,"2026 — مفتوحة")]));
}
internal sealed class SmokeDocuments : IJournalDocumentService
{
    public JournalDocumentDetails? Stored { get; private set; }
    public int PostCalls { get; private set; }
    public Task<JournalDocument> SaveDraftAsync(int actorId, JournalDraft draft, CancellationToken cancellationToken = default)
    {
        Stored = new(new(1, draft.BranchId, "JOURNAL_VOUCHER", draft.EntryDate, draft.SourceKey, JournalDocumentState.Draft,null,null,null),draft);
        return Task.FromResult(Stored.Document);
    }
    public void MarkPosted() => Stored = Stored! with { Document = Stored!.Document with { State=JournalDocumentState.Posted, Number=1, JournalId=1, Version=2 } };
    public Task<JournalDocumentDetails> DetailsAsync(int actorId,long documentId,CancellationToken cancellationToken=default) => Task.FromResult(Stored!);
    public Task<JournalDocument> GetAsync(int actorId,long documentId,CancellationToken cancellationToken=default) => Task.FromResult(Stored!.Document);
    public Task<IReadOnlyList<JournalDocument>> ListAsync(int actorId,int branchId,int page=0,CancellationToken cancellationToken=default) =>
        Task.FromResult<IReadOnlyList<JournalDocument>>(Stored is null ? [] : [Stored.Document]);
    public Task<JournalDocument> SaveVoucherAsync(int actorId,JournalDraft draft,string typeCode,CancellationToken cancellationToken=default) => throw new NotSupportedException();
    public Task<JournalDocument> UpdateDraftAsync(int actorId,long documentId,int expectedVersion,JournalDraft draft,CancellationToken cancellationToken=default) => throw new NotSupportedException();
    public Task<JournalDocument> PostAsync(int actorId,long documentId,CancellationToken cancellationToken=default) { PostCalls++; throw new InvalidOperationException("UI smoke must not post."); }
    public Task<JournalDocument> PostVersionAsync(int actorId,long documentId,int expectedVersion,CancellationToken cancellationToken=default) => PostAsync(actorId,documentId,cancellationToken);
    public Task<JournalDocument> ReverseAsync(int actorId,long documentId,DateOnly reversalDate,Guid sourceKey,CancellationToken cancellationToken=default) => throw new NotSupportedException();
}
