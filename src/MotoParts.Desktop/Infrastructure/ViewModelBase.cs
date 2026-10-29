using CommunityToolkit.Mvvm.ComponentModel;
using MotoParts.Core;
using MotoParts.Core.Common;
using MotoParts.Core.Security;

namespace MotoParts.Desktop.Infrastructure;

/// <summary>
/// Base for every screen's view-model.
///
/// <see cref="RunAsync{T}"/> is how screens talk to the business layer: the
/// work runs on a background thread in its own <see cref="ServiceScope"/>, so the
/// window stays responsive, and every failure is turned into something the
/// operator can read. A <see cref="BusinessRuleException"/> is an expected
/// refusal and its Arabic message is shown as is; anything else is a bug, which
/// is logged in full and reported in plain words.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _successMessage;

    protected static AppSession Session => AppSession.Current;

    protected static Actor User => Session.RequireUser;

    public bool Can(string permission) => Session.User?.Can(permission) ?? false;

    /// <summary>Runs work that returns a value. Null means it failed (the message is already shown).</summary>
    protected async Task<T?> RunAsync<T>(Func<ServiceScope, T> work, string? successMessage = null)
        where T : class
    {
        var (ok, value) = await TryRunAsync(work, successMessage);
        return ok ? value : null;
    }

    /// <summary>Runs work with no result. Returns whether it succeeded.</summary>
    protected async Task<bool> RunAsync(Action<ServiceScope> work, string? successMessage = null)
    {
        var (ok, _) = await TryRunAsync(scope =>
        {
            work(scope);
            return true;
        }, successMessage);

        return ok;
    }

    /// <summary>Runs work and reports success separately from the value, for value-type results.</summary>
    protected async Task<(bool Ok, T? Value)> TryRunAsync<T>(Func<ServiceScope, T> work, string? successMessage = null)
    {
        ErrorMessage = null;
        SuccessMessage = null;
        IsBusy = true;

        try
        {
            var value = await Task.Run(() =>
            {
                using var scope = Session.NewScope();
                return work(scope);
            });

            if (successMessage is not null)
            {
                SuccessMessage = successMessage;
            }

            return (true, value);
        }
        catch (BusinessRuleException ex)
        {
            ErrorMessage = ex.Message;
            OnBusinessRuleRefused(ex);
            return (false, default);
        }
        catch (Exception ex)
        {
            Log.Error($"Unexpected error in {GetType().Name}", ex);
            ErrorMessage = "حدث خطأ غير متوقع ولم تُنفذ العملية. سُجّلت التفاصيل في ملف السجل؛ أبلغ الدعم الفني إن تكرر.";
            return (false, default);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Hook for screens that want to react to a refusal, e.g. refocus the scanner box.</summary>
    protected virtual void OnBusinessRuleRefused(BusinessRuleException exception)
    {
    }
}

/// <summary>A page in the main window: has a title and loads its data when shown.</summary>
public abstract class PageViewModel : ViewModelBase
{
    public abstract string Title { get; }

    /// <summary>Called each time the page is navigated to.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;
}
