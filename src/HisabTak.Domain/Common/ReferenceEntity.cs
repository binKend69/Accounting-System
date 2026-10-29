namespace HisabTak.Domain.Common;

public abstract class ReferenceEntity
{
    public int Id { get; private set; }
    public string NameAr { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;
    public bool IsDeleted { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ModifiedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    protected ReferenceEntity() { }
    protected ReferenceEntity(string name) => NameAr = RequiredText(name, 200);

    public void Rename(string name) => NameAr = RequiredText(name, 200);
    public void SetActive(bool active) => IsActive = active;
    public void MarkDeleted() => IsDeleted = true;
    public void Restore() => IsDeleted = false;

    protected static string RequiredText(string value, int maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var text = value.Trim();
        if (text.Length > maximum) throw new ArgumentOutOfRangeException(nameof(value));
        return text;
    }
}
