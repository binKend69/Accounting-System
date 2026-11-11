using System.Globalization;
using System.Windows;
using HisabTak.Application.Inventory;

namespace HisabTak.WPF.Views;

public partial class StockWindow
{
    private sealed record HistoryDisplay(StockHistoryRow Row)
    {
        public string Date=>Row.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        public string? ReversalDate=>Row.ReversalDate?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        public string? OriginalDate=>Row.OriginalDate?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        public string Kind=>Kinds.Single(x=>x.Id==(int)Row.Kind).Label;
        public string State=>Row.State==3?"معكوس":"مرحل";
        public string Payment=>Row.Credit?"آجل":"نقدي";
        public string Price=>Row.OriginalMovementId!=null?"من الأصل":Row.UnitPrice.ToString("G29",CultureInfo.InvariantCulture);
        public string Discount=>Row.OriginalMovementId!=null?"من الأصل":Row.Discount.ToString("G29",CultureInfo.InvariantCulture);
        public string TaxMode=>!Row.TaxEnabled?"معطلة":Row.PricesIncludeTax?"شاملة":"غير شاملة";
        public string TaxRate=>(Row.TaxRate*100).ToString("G29",CultureInfo.InvariantCulture)+"%";
        public decimal Total=>Row.Net+Row.Tax;
    }
    private async Task LoadHistory(StockHistoryFilter filter)
    {
        var result=await Use(s=>s.HistoryAsync(actor,branch,filter));
        historyFilter=filter;HistoryGrid.ItemsSource=result.Rows.Select(x=>new HistoryDisplay(x)).ToArray();
        HistoryPreviousButton.IsEnabled=filter.Page>0;HistoryNextButton.IsEnabled=result.HasMore;
        HistoryPageText.Text=$"الصفحة {filter.Page+1} · {result.Rows.Count} سطر";
    }
    private async void OpenBalanceHistory(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (Balances.SelectedItem is not BalanceRow selected)
        {
            Status.Text = "اختر رصيد الصنف والمستودع أولاً.";
            return;
        }

        var filter = new StockHistoryFilter(new DateOnly(selected.AsOf.Year, 1, 1), selected.AsOf,
            selected.Balance.ProductId, selected.Balance.WarehouseId);
        await LoadHistory(filter);
        HistoryFrom.SelectedDate = filter.From.ToDateTime(TimeOnly.MinValue);
        HistoryTo.SelectedDate = filter.To.ToDateTime(TimeOnly.MinValue);
        HistoryProduct.SelectedValue = filter.ProductId;
        HistoryWarehouse.SelectedValue = filter.WarehouseId;
        HistoryKind.SelectedValue = 0;
        Tabs.SelectedItem = HistoryTab;
        Status.Text = $"تاريخ البيع والشراء منذ بداية السنة · {selected.Product} · {selected.Warehouse} حتى {selected.AsOf:yyyy-MM-dd}";
    });

    private async void SearchHistory(object sender,RoutedEventArgs e)=>await Run(()=>LoadHistory(new(Day(HistoryFrom),Day(HistoryTo),
        Id(HistoryProduct)==0?null:Id(HistoryProduct),Id(HistoryWarehouse)==0?null:Id(HistoryWarehouse),
        Id(HistoryKind)==0?null:(StockDocumentKind)Id(HistoryKind))));
    private async void PreviousHistory(object sender,RoutedEventArgs e)=>await Run(async()=>{if(historyFilter is {Page:>0} f)await LoadHistory(f with {Page=f.Page-1});});
    private async void NextHistory(object sender,RoutedEventArgs e)=>await Run(async()=>{if(historyFilter is {} f && HistoryNextButton.IsEnabled)await LoadHistory(f with {Page=f.Page+1});});
    private async void OpenHistory(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        if(HistoryGrid.SelectedItem is HistoryDisplay selected && Discard())ShowDocument(await Use(s=>s.GetAsync(actor,selected.Row.DocumentId)));
    });
    private async void OpenHistoryOriginal(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        if(HistoryGrid.SelectedItem is HistoryDisplay selected && selected.Row.OriginalDocumentId is long id && Discard())
            ShowDocument(await Use(s=>s.GetAsync(actor,id)));
    });
}
