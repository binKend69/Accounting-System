using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MotoParts.Desktop.Infrastructure;

/// <summary>A list screen filtered by a date range, defaulting to the current month.</summary>
public abstract partial class ListPageViewModel : PageViewModel
{
    protected ListPageViewModel()
    {
        var today = AppSession.Current.Clock.Today;
        _from = new DateTime(today.Year, today.Month, 1);
        _to = today.ToDateTime(TimeOnly.MinValue);
    }

    [ObservableProperty]
    private DateTime? _from;

    [ObservableProperty]
    private DateTime? _to;

    [ObservableProperty]
    private string _search = "";

    protected DateOnly? FromDate => From is { } f ? DateOnly.FromDateTime(f) : null;

    protected DateOnly? ToDate => To is { } t ? DateOnly.FromDateTime(t) : null;

    public override Task LoadAsync() => RefreshAsync();

    /// <summary>Re-queries the list with the current filters.</summary>
    protected abstract Task RefreshAsync();

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    [RelayCommand]
    private Task Today()
    {
        From = To = AppSession.Current.Clock.Today.ToDateTime(TimeOnly.MinValue);
        return RefreshAsync();
    }

    [RelayCommand]
    private Task ThisMonth()
    {
        var today = AppSession.Current.Clock.Today;
        From = new DateTime(today.Year, today.Month, 1);
        To = today.ToDateTime(TimeOnly.MinValue);
        return RefreshAsync();
    }

    /// <summary>Re-runs the query as the operator types in the search box.</summary>
    partial void OnSearchChanged(string value) => _ = RefreshAsync();
}
