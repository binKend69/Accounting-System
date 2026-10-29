namespace HisabTak.Infrastructure.Diagnostics;

public sealed class DiagnosticsOptions
{
    public const string SectionName = "Diagnostics";

    // Empty means the current user's local application data directory.
    public string Directory { get; set; } = string.Empty;

    public int RetainedFileCountLimit { get; set; } = 14;
}
