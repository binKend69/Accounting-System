using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using HisabTak.Application.Inventory;

namespace HisabTak.WPF.Views;

public partial class StockWindow
{
    private long? lastReceipt;
    private static string PrinterPath(bool kitchen) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HisabTak", kitchen ? "kitchen-printer.txt" : "receipt-printer.txt");

    private void ChooseReceiptPrinter(object sender, RoutedEventArgs e) => ChoosePrinter(false);
    private void ChooseKitchenPrinter(object sender, RoutedEventArgs e) => ChoosePrinter(true);

    private void ChoosePrinter(bool kitchen)
    {
        try
        {
            var dialog = new PrintDialog();
            if (dialog.ShowDialog() != true) return;
            var name = dialog.PrintQueue.FullName;
            var other = PrinterPath(!kitchen);
            if (File.Exists(other) && string.Equals(File.ReadAllText(other), name, StringComparison.OrdinalIgnoreCase))
            {
                Status.Text = "اختر طابعة مستقلة لكل من الفاتورة والمطبخ.";
                return;
            }
            var path = PrinterPath(kitchen);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, name);
            Status.Text = $"حُفظت الطابعة على هذا الجهاز · {name}";
        }
        catch (Exception) { Status.Text = "تعذر حفظ الطابعة. تحقق من تثبيتها في Windows."; }
    }

    private string PrintReceipt(StockDocument document, bool kitchen)
    {
        var title = kitchen ? "تذكرة مطبخ" : "فاتورة بيع";
        if (document.Draft.Kind != StockDocumentKind.Sale || document.State != 2) return "اختر فاتورة بيع مرحلة.";
        var lines = document.Draft.Lines.Where(line => !kitchen || line.PrintInKitchen).ToArray();
        if (lines.Length == 0) return "";
        try
        {
            var path = PrinterPath(kitchen);
            if (!File.Exists(path)) return $"اختر طابعة {title} ثم أعد الطباعة.";
            using var server = new LocalPrintServer();
            using var queues = server.GetPrintQueues(new[]{EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections});
            var name = File.ReadAllText(path);
            var queue = queues.FirstOrDefault(q => string.Equals(q.FullName, name, StringComparison.OrdinalIgnoreCase));
            if (queue == null) return $"تعذر العثور على طابعة {title}.";
            var dialog = new PrintDialog { PrintQueue = queue };
            var page = new FlowDocument { FlowDirection = FlowDirection.RightToLeft, FontFamily = FontFamily, FontSize = 12,
                PagePadding = new Thickness(8), ColumnWidth = double.PositiveInfinity, PageWidth = dialog.PrintableAreaWidth,
                PageHeight = dialog.PrintableAreaHeight };
            void Row(string value) => page.Blocks.Add(new Paragraph(new Run(value)) { Margin = new Thickness(0,2,0,2) });
            Row($"{title} رقم {document.Number} · {document.Draft.Date:yyyy-MM-dd}");
            Row($"الفرع {document.Draft.BranchId}");
            foreach (var line in lines)
            {
                Row($"{Product(line.ProductId)} · {line.Quantity:G29} {UnitName(line.UnitId)}");
                if (!kitchen) Row($"سعر الوحدة {line.UnitPrice:G29} · الخصم {line.Discount:G29}");
            }
            if (!kitchen)
            {
                var net = document.Movements.Sum(x => x.Net);
                var tax = document.Movements.Sum(x => x.Tax);
                Row($"الصافي {net:G29} · الضريبة {tax:G29}");
                Row($"الإجمالي {net+tax:G29} · {currencies.FirstOrDefault(c => c.Id == document.Draft.CurrencyId)?.Label}");
            }
            dialog.PrintDocument(((IDocumentPaginatorSource)page).DocumentPaginator, $"{title} {document.Number}");
            return $"أُرسل {title} إلى الطابعة.";
        }
        catch (Exception) { return $"تعذرت طباعة {title}. يمكن إعادة الطباعة من الفاتورة المحفوظة."; }
    }

    private void RepeatReceipt(object sender, RoutedEventArgs e) => RepeatPrint(false);
    private void RepeatKitchen(object sender, RoutedEventArgs e) => RepeatPrint(true);
    private async void RepeatPrint(bool kitchen) => await Run(async () =>
    {
        var id = slotNumber == 0 ? current?.Id : lastReceipt;
        if (id == null) { Status.Text = "لا توجد فاتورة مدفوعة محددة."; return; }
        var saved = await Use(s => s.GetAsync(actor, id.Value));
        var message = PrintReceipt(saved, kitchen);
        Status.Text = message.Length == 0 ? "لا توجد أصناف مطبخ في هذه الفاتورة." : message;
    });

    private async void UpdateCashierProduct(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var product = Id(SetupProduct);
        var dep = ItemDepartment.SelectedValue as int? ?? 0;
        await Change(s => s.SetCashierProductAsync(actor, product, dep == 0 ? null : dep, KitchenItem.IsChecked == true));
        await LoadLookups();
        SetupProduct.SelectedValue = product;
        Status.Text = "تم تحديث قسم الصنف وخيار المطبخ.";
    });

    private async void SaveDepartment(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var dep = Id(ItemDepartment);
        var order = int.Parse(DepartmentOrder.Text, System.Globalization.CultureInfo.InvariantCulture);
        await Change(s => s.SetDepartmentAppearanceAsync(actor, dep, DepartmentColor.Text.Trim(), order));
        await LoadLookups();
        ItemDepartment.SelectedValue = dep;
        Status.Text = "تم حفظ إعدادات القسم.";
    });

    private void ShowDepartments()
    {
        DepartmentButtons.Visibility = TouchItems.Visibility = Visibility.Visible;
        DepartmentButtons.Children.Clear();
        void DepartmentButton(int? id, string name, string? color)
        {
            var button = new Button { Content = name, MinHeight = 44 };
            if (color != null && System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
            {
                var background = (Color)ColorConverter.ConvertFromString(color);
                button.Background = new SolidColorBrush(background);
                button.Foreground = background.R * 0.299 + background.G * 0.587 + background.B * 0.114 > 150 ? Brushes.Black : Brushes.White;
            }
            button.Click += (_, _) => ShowDepartmentItems(id);
            DepartmentButtons.Children.Add(button);
        }
        DepartmentButton(null, "بدون قسم", null);
        foreach (var dep in lookups.Departments) DepartmentButton(dep.Id, dep.Name, dep.Color);
        ShowDepartmentItems(lookups.Departments.FirstOrDefault(d => products.Any(p => p.DepartmentId == d.Id))?.Id);
    }

    private void ShowDepartmentItems(int? department)
    {
        TouchItems.Children.Clear();
        foreach (var product in products.Where(x => x.DepartmentId == department))
        {
            var button = new Button { Content = product.Name, MinHeight = 44 };
            button.Click += async (_, _) => await Run(async () =>
            {
                Item.SelectedValue = product.Id;
                await LineOptions();
                Unit.SelectedValue = product.BaseUnitId;
                var price = await Use(s => s.PriceAsync(actor, product.Id, product.BaseUnitId, Id(InvoiceCurrency), SaleTier.Text));
                Price.Text = price?.ToString("G29", System.Globalization.CultureInfo.InvariantCulture) ?? "";
                Quantity.Focus();
            });
            TouchItems.Children.Add(button);
        }
    }
}
