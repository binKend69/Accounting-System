namespace HisabTak.Infrastructure.Diagnostics;

public static class DiagnosticPaths
{
    public static string ResolveDirectory(DiagnosticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.Directory))
        {
            if (!Path.IsPathFullyQualified(options.Directory))
            {
                throw new ArgumentException("The diagnostic directory must be an absolute path.", nameof(options));
            }

            return Path.GetFullPath(options.Directory);
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("No local application data directory is available. Configure Diagnostics:Directory.");
        }

        return Path.Combine(localData, "HisabTak", "Logs");
    }
}
