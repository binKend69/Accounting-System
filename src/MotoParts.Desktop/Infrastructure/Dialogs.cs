using System.Windows;
using MotoParts.Core.Common;
using MotoParts.Desktop.Views.Popups;

namespace MotoParts.Desktop.Infrastructure;

/// <summary>Arabic message boxes, confirmations and prompts, owned by the active window.</summary>
public static class Dialogs
{
    public static Window? Owner =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current.MainWindow;

    public static void Info(string message, string title = "تم")
    {
        Show(MessageDialog.Create(MessageKind.Info, title, message, "موافق", null));
    }

    public static void Error(string message, string title = "تعذر تنفيذ العملية")
    {
        Show(MessageDialog.Create(MessageKind.Error, title, message, "موافق", null));
    }

    public static bool Confirm(string message, string title = "تأكيد", string yes = "نعم", string no = "إلغاء", bool danger = false)
    {
        return Show(MessageDialog.Create(danger ? MessageKind.Danger : MessageKind.Question, title, message, yes, no)) == true;
    }

    /// <summary>Asks for text, e.g. a reason. Returns null when cancelled or, if required, left blank.</summary>
    public static string? Prompt(string title, string message, string? initial = null, bool multiline = true, bool required = true)
    {
        while (true)
        {
            var dialog = MessageDialog.Create(MessageKind.Question, title, message, "متابعة", "إلغاء").WithInput(initial, multiline);

            if (Show(dialog) != true)
            {
                return null;
            }

            var text = dialog.InputText?.Trim();

            if (!required || !string.IsNullOrEmpty(text))
            {
                return text;
            }

            Error("هذا الحقل مطلوب.");
        }
    }

    /// <summary>Asks for an amount. Returns null when cancelled.</summary>
    public static Money? PromptMoney(string title, string message, Money? initial = null)
    {
        while (true)
        {
            var dialog = MessageDialog.Create(MessageKind.Question, title, message, "متابعة", "إلغاء")
                .WithInput(initial?.ToString(), multiline: false);
            dialog.Input.Style = (Style)Application.Current.FindResource("NumberBox");

            if (Show(dialog) != true)
            {
                return null;
            }

            if (Money.TryParse(dialog.InputText, out var amount) && !amount.IsNegative)
            {
                return amount;
            }

            Error("أدخل مبلغاً صحيحاً، مثل 150 أو 150.50");
        }
    }

    public static bool? Show(Window dialog)
    {
        var owner = Owner;

        if (owner is not null && owner != dialog && owner.IsLoaded)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog();
    }
}
