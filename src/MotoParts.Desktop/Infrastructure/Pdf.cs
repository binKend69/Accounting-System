using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using QuestPDF.Infrastructure;
using MotoParts.Documents;

namespace MotoParts.Desktop.Infrastructure;

/// <summary>
/// Shows and saves printable documents. Printing is done from the PDF viewer
/// the shop already uses, which handles every printer - A4 and 80 mm thermal
/// alike - without the application having to drive printer drivers itself.
/// </summary>
public static class Pdf
{
    private static string TempFolder => Path.Combine(Path.GetTempPath(), "MotoParts");

    /// <summary>Writes the document to a temporary PDF and opens it in the default viewer.</summary>
    public static void Open(IDocument document, string fileName)
    {
        Directory.CreateDirectory(TempFolder);
        CleanOldFiles();

        var path = Path.Combine(TempFolder, $"{Sanitise(fileName)}-{DateTime.Now:HHmmss}.pdf");
        DocumentFactory.SavePdf(document, path);

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Asks where to save, then writes the PDF there. Returns the path, or null if cancelled.</summary>
    public static string? SaveAs(IDocument document, string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            FileName = Sanitise(suggestedName) + ".pdf",
            Filter = "ملف PDF|*.pdf",
            DefaultExt = ".pdf",
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        DocumentFactory.SavePdf(document, dialog.FileName);
        return dialog.FileName;
    }

    private static string Sanitise(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));

    private static void CleanOldFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(TempFolder, "*.pdf").Where(f => File.GetCreationTime(f) < DateTime.Now.AddDays(-2)))
            {
                File.Delete(file);
            }
        }
        catch (IOException)
        {
            // A PDF still open in a viewer is locked; it will go next time.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
