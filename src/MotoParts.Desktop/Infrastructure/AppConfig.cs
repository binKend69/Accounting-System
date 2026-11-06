using System.IO;
using System.Text.Json;

namespace MotoParts.Desktop.Infrastructure;

/// <summary>
/// Where the database, backups and logs live. Read from appsettings.json beside
/// the executable; every path defaults under %LOCALAPPDATA%\MotoParts so the
/// application never needs administrator rights to write its own data.
/// </summary>
public sealed class AppConfig
{
    public string DataDirectory { get; init; } = DefaultRoot("Data");

    public string BackupDirectory { get; init; } = DefaultRoot("Backups");

    public string LogDirectory { get; init; } = DefaultRoot("Logs");

    public string DatabasePath => Path.Combine(DataDirectory, "motoparts.db");

    public static AppConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        try
        {
            using var stream = File.OpenRead(path);
            var document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

            string Read(string name, string fallback)
            {
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = Environment.ExpandEnvironmentVariables(value.GetString() ?? "");
                    return string.IsNullOrWhiteSpace(text) ? fallback : Path.GetFullPath(text);
                }

                return fallback;
            }

            var defaults = new AppConfig();

            return new AppConfig
            {
                DataDirectory = Read("DataDirectory", defaults.DataDirectory),
                BackupDirectory = Read("BackupDirectory", defaults.BackupDirectory),
                LogDirectory = Read("LogDirectory", defaults.LogDirectory),
            };
        }
        catch (JsonException)
        {
            // A broken settings file must not stop the shop from trading.
            return new AppConfig();
        }
    }

    private static string DefaultRoot(string child) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MotoParts", child);
}
