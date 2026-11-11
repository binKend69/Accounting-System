using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using HisabTak.Application.Inventory;
using Microsoft.Extensions.DependencyInjection;

namespace HisabTak.WPF.Views;

public partial class StockWindow
{
    private int slotNumber;
    private int slotVersion;

    internal async Task StartPos()
    {
        foreach (TabItem tab in Tabs.Items)
            if (tab != InvoiceTab) tab.Visibility = Visibility.Collapsed;
        DocumentCommands.Visibility = MovementDetails.Visibility = AddCatalogItem.Visibility = Visibility.Collapsed;
        PosCommands.Visibility = Visibility.Visible;
        await RestoreSlot(1);
    }

    private async Task RestoreSlot(int number)
    {
        await using var scope = scopes.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<IPosSlots>().LoadAsync(actor, branch, number);
        var draft = saved.Draft;
        slotNumber = number;
        slotVersion = saved.Version;
        source = draft?.SourceKey ?? Guid.NewGuid();
        current = null;
        rows.Clear();
        Kind.SelectedValue = (int)StockDocumentKind.Sale;
        Credit.IsChecked = false;
        EntryDate.SelectedDate = draft?.Date.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
        InvoiceCurrency.SelectedValue = draft?.CurrencyId ?? currency;
        InvoiceRate.Text = (draft?.RateToBase ?? 1m).ToString(CultureInfo.InvariantCulture);
        TaxEnabled.IsChecked = draft?.TaxEnabled ?? false;
        TaxRate.Text = ((draft?.TaxRate ?? 0m) * 100).ToString(CultureInfo.InvariantCulture);
        Inclusive.IsChecked = draft?.PricesIncludeTax ?? false;
        if (draft != null)
        {
            Warehouse.SelectedValue = draft.WarehouseId;
            Party.SelectedValue = draft.CounterpartyAccountId;
            foreach (var line in draft.Lines) rows.Add(new(line, Product(line.ProductId), UnitName(line.UnitId)));
        }
        else
        {
            Warehouse.SelectedIndex = Warehouse.Items.Count > 0 ? 0 : -1;
            Party.SelectedIndex = -1;
        }
        Quantity.Text = "1";
        Price.Text = Discount.Text = "0";
        Reason.Clear();
        Original.Clear();
        await LineOptions();
        TitleText.Text = $"نقطة البيع · الفاتورة {number}";
        Status.Text = rows.Count > 0 ? "استعيدت السطور المحفوظة." : "الخانة فارغة.";
        Buttons();
    }

    private async Task PersistSlot()
    {
        await using var scope = scopes.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<IPosSlots>()
            .SaveSlotAsync(actor, branch, slotNumber, slotVersion, rows.Count == 0 ? null : Build());
        slotVersion = saved.Version;
    }

    private async void SwitchPos(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var number = int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
        if (number == slotNumber) return;
        await PersistSlot();
        await RestoreSlot(number);
    });

    private async void SavePos(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        await PersistSlot();
        Status.Text = $"حُفظت الفاتورة في الخانة {slotNumber}.";
    });

    private async void ReloadPos(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (Confirm("استعادة آخر حفظ للخانة وترك التغييرات المحلية؟")) await RestoreSlot(slotNumber);
    });

    private async void PayPos(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (rows.Count == 0 || !Confirm("تأكيد دفع الفاتورة وترحيلها؟")) return;
        await PersistSlot();
        await using var scope = scopes.CreateAsyncScope();
        var receipt = await scope.ServiceProvider.GetRequiredService<IPosSlots>().PayAsync(actor, branch, slotNumber, slotVersion);
        await RestoreSlot(slotNumber);
        Status.Text = $"تم الدفع · فاتورة بيع رقم {receipt.Number} · القيد {receipt.JournalId}.";
    });
}
