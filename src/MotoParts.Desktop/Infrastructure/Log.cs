using System.IO;
using System.Text;

namespace MotoParts.Desktop.Infrastructure;

/// <summary>
/// A plain daily text log of errors and notable events, for diagnosing a
/// problem after the fact. Never throws: logging must not be able to stop a sale.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _directory;

    public static void Initialise(string directory)
    {
        _directory = directory;

        try
        {
            Directory.CreateDirectory(directory);

            // Keep a quarter of logs; older files are no longer useful.
            foreach (var file in Directory.GetFiles(directory, "*.log").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-90)))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        if (_directory is null)
        {
            return;
        }

        var text = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .Append(" [").Append(level).Append("] ")
            .AppendLine(message);

        if (exception is not null)
        {
            text.AppendLine(exception.ToString());
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(_directory, $"{DateTime.Now:yyyy-MM-dd}.log"), text.ToString(), Encoding.UTF8);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
