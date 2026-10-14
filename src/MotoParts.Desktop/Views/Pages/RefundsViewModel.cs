using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MotoParts.Core.Common;
using MotoParts.Core.Services;
using MotoParts.Desktop.Infrastructure;
using MotoParts.Documents;

namespace MotoParts.Desktop.Views.Pages;

public sealed partial class RefundsViewModel : ListPageViewModel
{
    public override string Title => "المرتجعات";

    public ObservableCollection<RefundRow> Refunds { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    private RefundRow? _selected;

    [ObservableProperty]
    private string _summary = "";

    protected override async Task RefreshAsync()
    {
        var viewer = User;
        var (from, to) = (FromDate, ToDate);
        var search = Search.Trim();

        var rows = await RunAsync(scope => scope.Sales.Refunds(viewer, from, to));

        if (rows is null)
        {
            return;
        }

        Refunds.Clear();

        foreach (var row in rows.Where(r => search.Length == 0
                     || r.RefundNumber.Contains(search, StringComparison.OrdinalIgnoreCase)
                     || r.InvoiceNumber.Contains(search, StringComparison.OrdinalIgnoreCase)
                     || r.Reason.Contains(search, StringComparison.OrdinalIgnoreCase)))
        {
            Refunds.Add(row);
        }

        Summary = $"{Refunds.Count} إشعار   |   الإجمالي {Refunds.Sum(r => r.Total).ToDisplay()}";
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Print()
    {
        using var scope = Session.NewScope();
        Pdf.Open(new DocumentFactory(scope).Refund(Selected!.Id), Selected.RefundNumber);
    }
}
