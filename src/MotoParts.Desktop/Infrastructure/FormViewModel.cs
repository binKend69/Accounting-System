using System.Windows;
using MotoParts.Desktop.Views.Popups;

namespace MotoParts.Desktop.Infrastructure;

/// <summary>
/// A data-entry dialog: its fields, and what saving means. Shown in a
/// <see cref="FormWindow"/>, which supplies the frame, the buttons and the
/// error banner, so each form only declares what is specific to it.
/// </summary>
public abstract class FormViewModel : ViewModelBase
{
    public abstract string Title { get; }

    public virtual string SaveLabel => "حفظ";

    /// <summary>False for read-only views: the save button is hidden and cancel reads "إغلاق".</summary>
    public virtual bool CanSave => true;

    public virtual double Width => 560;

    /// <summary>Validates and saves. Returns true to close the dialog.</summary>
    public abstract Task<bool> SaveAsync();

    /// <summary>Loads anything the form needs before it is shown.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;
}

public static class FormHost
{
    /// <summary>Shows a form modally. Returns true when it was saved.</summary>
    public static bool Show(FormViewModel form)
    {
        var window = new FormWindow(form);
        return Dialogs.Show(window) == true;
    }
}
