using HisabTak.Domain.Common;

namespace HisabTak.Domain.Accounting;

public sealed class AccountGeneralGroup : ReferenceEntity
{
    private AccountGeneralGroup() { }
    public AccountGeneralGroup(string code, string name) : base(name) => SystemCode = RequiredText(code, 30);
    public string? SystemCode { get; private set; }
}

public sealed class AccountMainGroup : ReferenceEntity
{
    private AccountMainGroup() { }
    public AccountMainGroup(string code, string name, int? parentId = null) : base(name)
    {
        Code = RequiredText(code, 10);
        if (Code.Any(c => c is < '0' or > '9')) throw new ArgumentException("Account group code must contain digits.", nameof(code));
        ParentMainGroupId = parentId;
        Level = code.Length;
    }
    public string Code { get; private set; } = string.Empty;
    public int? ParentMainGroupId { get; private set; }
    public byte? ClosingReportType { get; private set; }
    public int Level { get; private set; }
}

public sealed class Account : ReferenceEntity
{
    private Account() { }
    public Account(string code, string name, int mainGroupId, int currencyId, byte nature) : base(name)
    {
        AccountCode = RequiredText(code, 20);
        if (mainGroupId <= 0 || currencyId <= 0 || nature is not (1 or 2)) throw new ArgumentException("Invalid account references.");
        MainGroupId = mainGroupId;
        CurrencyId = currencyId;
        Nature = nature;
    }
    public string AccountCode { get; private set; } = string.Empty;
    public int MainGroupId { get; private set; }
    public int? GeneralGroupId { get; private set; }
    public byte Nature { get; private set; }
    public int? BranchId { get; private set; }
    public int CurrencyId { get; private set; }
    public string? Phone { get; private set; }
    public string? Mobile { get; private set; }
    public string? Address { get; private set; }
    public string? Notes { get; private set; }
    public decimal? CreditLimit { get; private set; }
    public decimal OpeningBalance { get; private set; }
    public byte? OpeningBalanceNature { get; private set; }
    public bool IsHidden { get; private set; }
    public bool IsSuspended { get; private set; }
    public bool IsSystemAccount { get; private set; }
}

public sealed class FiscalPeriod : ReferenceEntity
{
    private FiscalPeriod() { }
    public FiscalPeriod(string name, DateOnly start, DateOnly end) : base(name)
    {
        if (end < start) throw new ArgumentException("End must not precede start.");
        StartDate = start;
        EndDate = end;
    }
    public DateOnly StartDate { get; private set; }
    public DateOnly EndDate { get; private set; }
    public bool IsClosed { get; private set; }
    public DateTime? ClosedAt { get; private set; }
    public int? ClosedByUserId { get; private set; }

    public bool Contains(DateOnly date) => date >= StartDate && date <= EndDate;

    public void EnsurePostingAllowed(DateOnly date)
    {
        EnsureActive();
        if (IsClosed) throw new InvalidOperationException("Posting to a closed fiscal period is not allowed.");
        if (!Contains(date)) throw new InvalidOperationException("Posting date is outside the fiscal period.");
    }

    public void Close(int actorId, DateTime utcNow)
    {
        EnsureActive();
        if (actorId <= 0) throw new ArgumentOutOfRangeException(nameof(actorId));
        if (utcNow.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC time is required.", nameof(utcNow));
        if (IsClosed) throw new InvalidOperationException("Fiscal period is already closed.");
        IsClosed = true;
        ClosedAt = utcNow;
        ClosedByUserId = actorId;
    }

    // Authorization and the append-only audit record are enforced by the application service.
    public void Reopen()
    {
        EnsureActive();
        if (!IsClosed) throw new InvalidOperationException("Fiscal period is already open.");
        IsClosed = false;
        ClosedAt = null;
        ClosedByUserId = null;
    }

    private void EnsureActive()
    {
        if (!IsActive || IsDeleted) throw new InvalidOperationException("Fiscal period is unavailable.");
    }
}

public sealed class DocumentType : ReferenceEntity
{
    private DocumentType() { }
    public DocumentType(string code, string name, byte category) : base(name)
    {
        Code = RequiredText(code, 30);
        if (category is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(category));
        Category = category;
    }
    public string Code { get; private set; } = string.Empty;
    public byte Category { get; private set; }
}
