namespace MotoParts.Desktop.Infrastructure;

/// <summary>An entry in a filter drop-down; a null value means "all".</summary>
public sealed record FilterOption<T>(string Label, T? Value)
{
    public override string ToString() => Label;
}
