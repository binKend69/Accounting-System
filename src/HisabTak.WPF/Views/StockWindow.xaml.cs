using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using HisabTak.Application.Accounting;
using HisabTak.Application.Inventory;
using HisabTak.Domain.Inventory;
using Microsoft.Extensions.DependencyInjection;

namespace HisabTak.WPF.Views;

public partial class StockWindow : Window
{
    private readonly IServiceScopeFactory scopes;
    private readonly int actor,branch,currency;
    private readonly ObservableCollection<DisplayLine> rows=[];
    private readonly AccountLookup[] accounts;
    private readonly AccountingLookup[] currencies;
    private StockLookups lookups=new([],[],null);
    private IReadOnlyList<ProductInfo> products=[];
    private StockDocument? current;
    private DebtPayment? pendingPayment;
    private bool returnToInvoice;
    private void OpenProductEntry(object sender, RoutedEventArgs e)
    {
        returnToInvoice = true;
        Tabs.SelectedItem = ProductSetupTab;
        ProductName.Focus();
    }
    private void CatalogSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Catalog.SelectedItem is ProductInfo product) SetupProduct.SelectedValue = product.Id;
    }
    private async void FindReturnInvoice(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var until = Day(EntryDate);
        var kind = Id(Kind) == (int)StockDocumentKind.SalesReturn ? StockDocumentKind.Sale : StockDocumentKind.Purchase;
        var from = new DateOnly(until.Year, 1, 1);
        await LoadHistory(new StockHistoryFilter(from, until, Kind: kind));
        HistoryFrom.SelectedDate = from.ToDateTime(TimeOnly.MinValue);
        HistoryTo.SelectedDate = until.ToDateTime(TimeOnly.MinValue);
        HistoryProduct.SelectedValue = HistoryWarehouse.SelectedValue = 0;
        HistoryKind.SelectedValue = (int)kind;
        Tabs.SelectedItem = HistoryTab;
        MovementDetails.IsExpanded = true;
        Status.Text = "اختر الفاتورة وافتح تفاصيلها ثم حدّد حركة الصنف لإنشاء المردود. يمكن تغيير فترة البحث.";
    });
    private async void ReturnMovement(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (current is not { State: 2 } invoice || invoice.Draft.Kind is not (StockDocumentKind.Purchase or StockDocumentKind.Sale)
            || Moves.SelectedItem is not StockMovement movement)
        {
            Status.Text = "افتح فاتورة بيع أو شراء مرحلة ثم اختر حركة الصنف المطلوب إرجاعه.";
            return;
        }
        var line = invoice.Draft.Lines[movement.LineNumber];
        Item.SelectedValue = line.ProductId;
        await LineOptions();
        Unit.SelectedValue = line.UnitId;
        if (!Equals(Item.SelectedValue, line.ProductId) || !Equals(Unit.SelectedValue, line.UnitId))
            throw new ArgumentException("Original unit unavailable.");
        current = null;
        source = Guid.NewGuid();
        rows.Clear();
        Moves.ItemsSource = null;
        Kind.SelectedValue = (int)(invoice.Draft.Kind == StockDocumentKind.Purchase ? StockDocumentKind.PurchaseReturn : StockDocumentKind.SalesReturn);
        EntryDate.SelectedDate = DateTime.Today;
        Batch.SelectedValue = movement.BatchId ?? 0L;
        Original.Text = movement.Id.ToString(CultureInfo.InvariantCulture);
        Price.Text = Discount.Text = "0";
        MovementDetails.IsExpanded = false;
        Quantity.Clear();
        Quantity.Focus();
        Status.Text = $"مردود من الفاتورة {invoice.Number} · أدخل الكمية ثم أضف السطر.";
    });
    private int page;
    private StockHistoryFilter? historyFilter;
    private Guid source=Guid.NewGuid();
    private bool busy;
    internal Func<string,bool>? Confirmation { get; set; }
    private bool Confirm(string message)=>Confirmation?.Invoke(message)??MessageBox.Show(this,message,"تأكيد",MessageBoxButton.YesNo)==MessageBoxResult.Yes;
    public sealed record DisplayLine(StockLine Line,string Product,string Unit);
    private sealed record Choice(int Id,string Label);
    private sealed record DocChoice(StockDocument Document,string Label);
    private sealed record BatchChoice(long Id,string Label);
    private sealed record BalanceRow(StockBalance Balance, string Product, string Warehouse, DateOnly AsOf);
    private static readonly Choice[] Kinds=[new(1,"افتتاحي مخزون"),new(2,"شراء"),new(3,"بيع"),new(4,"مردود شراء"),new(5,"مردود بيع"),new(6,"تحويل مخزني"),new(7,"زيادة جرد"),new(8,"نقص جرد"),new(9,"عرض سعر"),new(10,"أمر شراء")];
    public StockWindow(IServiceScopeFactory scopes,int actor,int branch,int currency,AccountLookup[] accounts,AccountingLookup[]? currencies=null)
    {
        InitializeComponent(); this.scopes=scopes; this.actor=actor; this.branch=branch; this.currency=currency; this.accounts=accounts;this.currencies=currencies??[new(currency,currency.ToString())];
        HistoryFrom.SelectedDate=DateTime.Today.AddMonths(-1);HistoryTo.SelectedDate=DateTime.Today;
        Bind(HistoryKind,new[]{new Choice(0,"الكل")}.Concat(Kinds.Where(x=>x.Id is >=2 and <=5)));
        Bind(Kind,Kinds); Kind.SelectedValue=(int)StockDocumentKind.Purchase;
        Lines.ItemsSource=rows; EntryDate.SelectedDate=ReverseDate.SelectedDate=AsOf.SelectedDate=PaymentDate.SelectedDate=DateTime.Today;
        Item.SelectionChanged+=RefreshLineOptions; Warehouse.SelectionChanged+=RefreshLineOptions;
        Closing+=(_,e)=>{ if(busy || !Discard()) e.Cancel=true; };
    }
    private static void Bind(ComboBox box,IEnumerable<Choice> choices)
    {
        var selected=box.SelectedValue; box.DisplayMemberPath="Label"; box.SelectedValuePath="Id"; box.ItemsSource=choices.ToArray();
        box.SelectedValue=selected;
        if(box.SelectedIndex<0 && box.Items.Count>0) box.SelectedIndex=0;
    }
    private static int Id(ComboBox box)=>box.SelectedValue is int id?id:throw new ArgumentException("Selection required.");
    private static decimal Number(TextBox field)=>decimal.Parse(field.Text.Trim(),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);
    private static DateOnly Day(DatePicker field)=>DateOnly.FromDateTime(field.SelectedDate??throw new ArgumentException("Date required."));
    private async Task<T> Use<T>(Func<IStockDocumentService,Task<T>> work)
    {
        await using var scope=scopes.CreateAsyncScope(); return await work(scope.ServiceProvider.GetRequiredService<IStockDocumentService>());
    }
    private Task Change(Func<IStockDocumentService,Task> work)=>Use(async s=>{await work(s);return true;});
    private async Task Run(Func<Task> work)
    {
        if(busy) return;
        busy=true; Tabs.IsEnabled=false; PosCommands.IsEnabled=false;
        try { await work(); }
        catch(UnauthorizedAccessException){Status.Text="لا تملك صلاحية هذه العملية في الفرع.";}
        catch(DocumentConflictException){Status.Text=slotNumber == 0 ? "تغيرت النسخة المحفوظة. افتح المستند مجددًا من السجل." : "تغيرت الخانة في جلسة أخرى. اختر استعادة آخر حفظ."; }
        catch(Exception e) when(e is ArgumentException or FormatException or OverflowException){Status.Text="راجع الاختيارات والأرقام والتاريخ. استخدم النقطة للفاصل العشري.";}
        catch(InvalidOperationException e) when(e.Message.Contains("sales returns account",StringComparison.Ordinal)){Status.Text="حدد حساب مردودات المبيعات المستقل في إعداد الفرع قبل الترحيل.";}
        catch(InvalidOperationException e) when(e.Message.Contains("valuation policy",StringComparison.Ordinal)){Status.Text="اختر معالجة فرق آخر شراء في إعداد حسابات الفرع."; }
        catch(InvalidOperationException e) when(e.Message.StartsWith("Insufficient",StringComparison.Ordinal)){Status.Text="الكمية المتاحة للصرف لا تكفي. راجع رصيد المستودع وصلاحية الدفعات."; }
        catch(InvalidOperationException e) when(e.Message.Contains("credit limit",StringComparison.OrdinalIgnoreCase)){Status.Text="راجع الحد الائتماني للعميل وقيمة رصيده قبل البيع الآجل."; }
        catch(InvalidOperationException e) when(e.Message.StartsWith("Backdated",StringComparison.Ordinal)){Status.Text="تاريخ المستند أقدم من آخر حركة للصنف في المستودع."; }
        catch(Exception){Status.Text="تعذرت العملية. راجع الرصيد والصلاحية والفترة والحسابات والاتصال ثم افتح النسخة المحفوظة للتحقق.";}
        finally{busy=false;Tabs.IsEnabled=true;PosCommands.IsEnabled=true;Buttons();}
    }
    public async Task LoadAsync()
    {
        busy=true;
        try {await LoadLookups();await RefreshLists();TitleText.Text=$"المبيعات والمخزون · الفرع {branch}";Status.Text="اختر نوع المستند والمستودع وأضف السطور.";}
        finally {busy=false;Buttons();}
    }
    private async Task LoadLookups()
    {
        lookups=await Use(s=>s.LookupsAsync(actor,branch));products=await Use(s=>s.ProductsAsync(actor));
        var ws=lookups.Warehouses.Select(x=>new Choice(x.Id,x.Name));
        foreach(var box in new[]{Warehouse,Target,BatchWarehouse})Bind(box,ws);
        var units=lookups.Units.Select(x=>new Choice(x.Id,x.Name));
        Bind(BaseUnit,units);Bind(ExtraUnit,units);Bind(PriceUnit,units);
        var items=products.Select(x=>new Choice(x.Id,x.Code+" · "+x.Name));
        Bind(Item,items);Bind(SetupProduct,items);
        Catalog.ItemsSource = products;
        FilterProducts();
        Bind(HistoryProduct,new[]{new Choice(0,"الكل")}.Concat(items));
        Bind(HistoryWarehouse,new[]{new Choice(0,"الكل")}.Concat(ws));
        Bind(PriceCurrency,currencies.Select(x=>new Choice(x.Id,x.Label)));var firstLoad=InvoiceCurrency.Items.Count==0;Bind(InvoiceCurrency,currencies.Select(x=>new Choice(x.Id,x.Label))); Bind(CurrencySettings,currencies.Select(x=>new Choice(x.Id,x.Label)));
        if(firstLoad)InvoiceCurrency.SelectedValue=currency;
        var ac=accounts.Where(x=>x.CurrencyId==currency).Select(x=>new Choice(x.Id,x.Label));
        foreach(var box in new[]{StockAccount,SalesAccount,CostAccount,Adjustment,InputTax,OutputTax}) Bind(box,ac);
        foreach(var box in new[]{Party,Cash,LimitCustomer}) Bind(box,accounts.Select(x=>new Choice(x.Id,x.Label)));
        Bind(SalesReturnsAccount,new[]{new Choice(0,"غير محدد")}.Concat(ac));
        foreach(var box in new[]{FxGain,FxLoss}) Bind(box,new[]{new Choice(0,"غير محدد")}.Concat(ac));
        if(lookups.Accounts is InventoryAccounts a)
        {
            SalesReturnsAccount.SelectedValue=a.SalesReturnsAccountId??0;
            StockAccount.SelectedValue=a.StockAccountId;SalesAccount.SelectedValue=a.SalesAccountId;CostAccount.SelectedValue=a.CostAccountId;
            Adjustment.SelectedValue=a.AdjustmentAccountId;InputTax.SelectedValue=a.InputTaxAccountId;OutputTax.SelectedValue=a.OutputTaxAccountId;FxGain.SelectedValue=a.FxGainAccountId??0;FxLoss.SelectedValue=a.FxLossAccountId??0;BookPolicy.SelectedIndex=(int)a.BookPolicy;
        }
        await LineOptions();
    }
    private async Task LineOptions()
    {
        if(Item.SelectedValue is not int p || Warehouse.SelectedValue is not int w){Unit.ItemsSource=null;Batch.ItemsSource=null;return;}
        var allowed=await Use(s=>s.UnitsAsync(actor,p));
        Bind(Unit,from u in lookups.Units join a in allowed on u.Id equals a.UnitId select new Choice(u.Id,u.Name+" × "+a.Factor.ToString("G29")));
        var batches=await Use(s=>s.BatchesAsync(actor,branch,w));
        Batch.DisplayMemberPath="Label";Batch.SelectedValuePath="Id";
        Batch.ItemsSource=new[]{new BatchChoice(0,"تلقائي للصادر / دون دفعة")}.Concat(batches.Where(x=>x.ProductId==p).Select(x=>new BatchChoice(x.Id,$"{x.Code} · {x.Expiry:yyyy-MM-dd}"))).ToArray();Batch.SelectedIndex=0;
    }
    private async void RefreshLineOptions(object sender,SelectionChangedEventArgs e){if(!busy && IsLoaded)await Run(LineOptions);}
    private string Product(int id)=>products.FirstOrDefault(x=>x.Id==id)?.Name??id.ToString();
    private string UnitName(int id)=>lookups.Units.FirstOrDefault(x=>x.Id==id)?.Name??id.ToString();
    private async Task RefreshLists()
    {
        var docs=await Use(s=>s.ListAsync(actor,branch,page));
        Documents.ItemsSource=docs.Select(d=>new DocChoice(d,$"{Kinds.Single(x=>x.Id==(int)d.Draft.Kind).Label} · {(d.Number?.ToString()??$"سجل {d.Id}")} · {d.Draft.Date:yyyy-MM-dd} · {(d.ConvertedInvoiceId.HasValue?"محوّل":d.State==1?"مسودة":d.State==2?"مرحل":"معكوس")}")).ToArray();
        var balanceDate = Day(AsOf);
        var stock = await Use(s => s.BalancesAsync(actor, branch, balanceDate));
        Balances.ItemsSource = stock.Select(balance => new BalanceRow(balance, Product(balance.ProductId),
            lookups.Warehouses.FirstOrDefault(w => w.Id == balance.WarehouseId)?.Name ?? balance.WarehouseId.ToString(),
            balanceDate)).ToArray();
        Debts.ItemsSource=await Use(s=>s.DebtsAsync(actor,branch));
    }
    private StockDocumentDraft Build()
    {
        var kind=(StockDocumentKind)Id(Kind);
        var trade=kind is StockDocumentKind.SalesQuote or StockDocumentKind.PurchaseOrder or StockDocumentKind.Purchase or StockDocumentKind.Sale or StockDocumentKind.PurchaseReturn or StockDocumentKind.SalesReturn;
        return new(source,branch,kind,Day(EntryDate),Id(Warehouse),kind==StockDocumentKind.Transfer?Id(Target):null,Id(InvoiceCurrency),Number(InvoiceRate),trade?Id(Party):0,
            trade && Credit.IsChecked==true,rows.Select(x=>x.Line).ToArray(),TaxEnabled.IsChecked==true,TaxEnabled.IsChecked==true?Number(TaxRate)/100m:0m,TaxEnabled.IsChecked==true && Inclusive.IsChecked==true);
    }
    private bool Dirty()
    {
        if(current==null)return rows.Count!=0;
        if(current.State!=1 || current.ConvertedInvoiceId.HasValue)return false;
        try{var d=Build();return (d with {Lines=current.Draft.Lines})!=current.Draft || !d.Lines.SequenceEqual(current.Draft.Lines);}
        catch(Exception){return true;}
    }
    private bool Discard()=>!Dirty() || Confirm("ترك تعديلات المستند غير المحفوظة؟");
    private void Buttons()
    {
        PreviewButton.IsEnabled=current!=null;SaveButton.IsEnabled=current?.State is null or 1;PostButton.IsEnabled=current?.State==1;ReverseButton.IsEnabled=current?.State==2;
        var intent = current?.Draft.Kind is StockDocumentKind.SalesQuote or StockDocumentKind.PurchaseOrder;
        ConvertButton.Visibility = intent ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.Content = current?.ConvertedInvoiceId != null ? "فتح الفاتورة المرتبطة" : "تحويل إلى مسودة فاتورة";
        PostButton.IsEnabled = current?.State == 1 && !intent;
        if (current?.ConvertedInvoiceId != null) SaveButton.IsEnabled = false;
        AddStockLineButton.IsEnabled = SaveButton.IsEnabled;
        foreach(var control in new Control[]{Warehouse,Target,Party,InvoiceCurrency,InvoiceRate,Credit,EntryDate,TaxEnabled,TaxRate,Inclusive,Item,Unit,Quantity,Price,Discount,Batch,Original,Reason})control.IsEnabled=SaveButton.IsEnabled;
        Kind.IsEnabled=current==null && slotNumber == 0;
        if (slotNumber != 0) Credit.IsEnabled = false;
    }
    private void ShowDocument(StockDocument doc)
    {
        current=doc;var d=doc.Draft;source=d.SourceKey;Kind.SelectedValue=(int)d.Kind;EntryDate.SelectedDate=d.Date.ToDateTime(TimeOnly.MinValue);
        InvoiceCurrency.SelectedValue=d.CurrencyId;InvoiceRate.Text=d.RateToBase.ToString("G29",CultureInfo.InvariantCulture);
        Warehouse.SelectedValue=d.WarehouseId;Target.SelectedValue=d.TargetWarehouseId;Party.SelectedValue=d.CounterpartyAccountId;Credit.IsChecked=d.Credit;
        TaxEnabled.IsChecked=d.TaxEnabled;TaxRate.Text=(d.TaxRate*100).ToString("G29",CultureInfo.InvariantCulture);Inclusive.IsChecked=d.PricesIncludeTax;
        rows.Clear();foreach(var l in d.Lines)rows.Add(new(l,Product(l.ProductId),UnitName(l.UnitId)));
        Moves.ItemsSource=doc.Movements;Tabs.SelectedIndex=0;Buttons();
    }
    private void NewDocument(object sender,RoutedEventArgs e)
    {
        if(!Discard())return;Original.Clear();current=null;source=Guid.NewGuid();rows.Clear();Moves.ItemsSource=null;EntryDate.SelectedDate=DateTime.Today;
        InvoiceCurrency.SelectedValue=currency;InvoiceRate.Text="1";Credit.IsChecked=TaxEnabled.IsChecked=Inclusive.IsChecked=false;TaxRate.Text="0";Buttons();Status.Text="مستند جديد.";
    }
    private async void AddLine(object sender,RoutedEventArgs e)=>await Run(()=> {
        if(!SaveButton.IsEnabled)return Task.CompletedTask;
        var line=new StockLine(Id(Item),Id(Unit),Number(Quantity),Number(Price),Number(Discount),Batch.SelectedValue is long b && b>0?b:null,
            string.IsNullOrWhiteSpace(Reason.Text)?null:Reason.Text.Trim(),Id(Kind) is not (4 or 5) || string.IsNullOrWhiteSpace(Original.Text)?null:long.Parse(Original.Text,CultureInfo.InvariantCulture));
        rows.Add(new(line,Product(line.ProductId),UnitName(line.UnitId)));return Task.CompletedTask;
    });
    private void RemoveLine(object sender,RoutedEventArgs e){if(SaveButton.IsEnabled && Lines.SelectedItem is DisplayLine row)rows.Remove(row);}
    private async void Save(object sender,RoutedEventArgs e)=>await Run(async()=>{
        var draft=Build();var doc=current==null?await Use(s=>s.SaveAsync(actor,draft)):await Use(s=>s.UpdateAsync(actor,current.Id,current.Version,draft));
        ShowDocument(doc);await RefreshLists();Status.Text="حفظت المسودة.";
    });
    private async void Preview(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        if(current==null)return;
        var saved=await Use(s=>s.GetAsync(actor,current.Id));var d=saved.Draft;
        var page=new FlowDocument {FlowDirection=FlowDirection.RightToLeft,FontFamily=FontFamily,FontSize=15};
        void Row(string text)=>page.Blocks.Add(new Paragraph(new Run(text)));
        Row(Kinds.Single(k=>k.Id==(int)d.Kind).Label+" · "+(saved.Number?.ToString()??$"سجل {d.Id}"));
        if (saved.OriginIntentId is long origin) Row($"المستند الأصلي رقم {origin}");
        if (saved.ConvertedInvoiceId is long child) Row($"الفاتورة المرتبطة رقم السجل {child}");
        Row($"الفرع {branch} · {d.Date:yyyy-MM-dd} · {(saved.State==1?"مسودة":saved.State==2?"مرحل":"معكوس")} · العملة {d.CurrencyId}");
        if(d.CounterpartyAccountId>0)Row("حساب الطرف · "+(accounts.FirstOrDefault(a=>a.Id==d.CounterpartyAccountId)?.Label??d.CounterpartyAccountId.ToString()));
        for(var i=0;i<d.Lines.Count;i++)
        {
            var l=d.Lines[i];Row($"{Product(l.ProductId)} · {l.Quantity:G29} {UnitName(l.UnitId)} · السعر {l.UnitPrice:G29} · الخصم {l.Discount:G29}");
            if(l.OriginalMovementId!=null)Row($"الحركة الأصلية {l.OriginalMovementId}");
            foreach(var m in saved.Movements.Where(m=>m.LineNumber==i))
                Row($"حركة {m.Id} · مستودع {m.WarehouseId} · دفعة {m.BatchId?.ToString()??"—"} · كمية {m.Quantity:G29} · أثر دفتري {m.Value:G29}");
        }
        if(saved.State>=2 && d.Kind is StockDocumentKind.Sale or StockDocumentKind.Purchase or StockDocumentKind.PurchaseReturn or StockDocumentKind.SalesReturn)
        {
            var net=saved.Movements.Sum(x=>x.Net);var tax=saved.Movements.Sum(x=>x.Tax);
            Row($"الصافي {net:G29} · الضريبة {tax:G29} · الإجمالي {net+tax:G29}");
        }
        new Window { Owner=this,Title="معاينة النسخة المحفوظة",Width=780,Height=640,Content=new FlowDocumentReader {Document=page} }.ShowDialog();
    });
    private async void ConvertIntent(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (current == null || Dirty()) { Status.Text = "احفظ تعديلات المستند قبل التحويل."; return; }
        var origin = current.Id;
        if (current.ConvertedInvoiceId == null && !Confirm("إنشاء مسودة فاتورة مرتبطة وتثبيت المستند الأصلي؟")) return;
        var invoice = await Use(s => s.ConvertIntentAsync(actor, origin, current.Version));
        ShowDocument(invoice);
        await RefreshLists();
        Status.Text = $"فُتحت الفاتورة المرتبطة بالمستند {origin}.";
    });

    private async void Post(object sender,RoutedEventArgs e)=>await Run(async()=>{
        if(current==null || Dirty()){Status.Text="احفظ التعديلات قبل الترحيل.";return;}
        if(!Confirm("ترحيل النسخة المحفوظة وإثبات آثارها؟"))return;
        ShowDocument(await Use(s=>s.PostAsync(actor,current.Id,current.Version)));await RefreshLists();Status.Text="تم الترحيل.";
    });
    private async void Reverse(object sender,RoutedEventArgs e)=>await Run(async()=>{
        if(current==null || !Confirm("عكس المستند وآثاره في التاريخ المحدد؟"))return;
        ShowDocument(await Use(s=>s.ReverseAsync(actor,current.Id,Day(ReverseDate),Guid.NewGuid())));await RefreshLists();Status.Text="تم العكس.";
    });
    private async void Open(object sender,RoutedEventArgs e)=>await Run(async()=>{if(Documents.SelectedItem is DocChoice doc && Discard()){ShowDocument(await Use(s=>s.GetAsync(actor,doc.Document.Id)));await LineOptions();Status.Text="تم فتح المستند."; }});
    private async void Refresh(object sender,RoutedEventArgs e)=>await Run(RefreshLists);
    private async void Next(object sender,RoutedEventArgs e)=>await Run(async()=>{page++;await RefreshLists();});
    private async void Previous(object sender,RoutedEventArgs e)=>await Run(async()=>{page=Math.Max(0,page-1);await RefreshLists();});
    private void FocusProductSearch(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        ProductSearch.Focus();
        ProductSearch.SelectAll();
    }
    private void ProductSearchChanged(object sender, TextChangedEventArgs e) => FilterProducts();
    private void FilterProducts()
    {
        if (ProductMatches is null) return;
        var query = ProductSearch.Text.Trim();
        var matches = query.Length < 2 ? Array.Empty<Choice>() : products
            .Where(x => x.Code.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(x => new Choice(x.Id, x.Code + " · " + x.Name)).ToArray();
        ProductMatches.ItemsSource = matches;
        if (matches.Length > 0) ProductMatches.SelectedIndex = 0;
        ChooseProductButton.IsEnabled = matches.Length > 0;
    }
    private async void ChooseProduct(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (current?.State is 2 or 3 || ProductMatches.SelectedValue is not int id) return;
        Item.SelectedValue = id;
        await LineOptions();
        Unit.SelectedValue = products.Single(x => x.Id == id).BaseUnitId;
        Price.Text = "0";
        Status.Text = "تم اختيار الصنف ووحدته الأساسية. أدخل السعر أو اجلبه من الشريحة.";
    });
    private async void Scan(object sender,RoutedEventArgs e)=>await Run(async()=>{
        if(current?.State is 2 or 3)return;
        var match=await Use(s=>s.ScanAsync(actor,ScanCode.Text));
        if(match==null){Status.Text="لم يعثر على الباركود.";return;}
        Item.SelectedValue=match.ProductId;await LineOptions();Unit.SelectedValue=match.UnitId;Price.Text="0";Status.Text="تم اختيار الصنف ووحدة الباركود.";
    });
    private async void Quote(object sender,RoutedEventArgs e)=>await Run(async()=>{
        if(current?.State is 2 or 3)return;
        var value=await Use(s=>s.PriceAsync(actor,Id(Item),Id(Unit),Id(InvoiceCurrency),SaleTier.Text));
        Price.Text=value?.ToString("G29",CultureInfo.InvariantCulture)??"";
        Status.Text=value.HasValue?"تم تحميل السعر.":"لا يوجد سعر لهذه الوحدة والشريحة والعملة.";
    });
    private async void SetBarcode(object sender,RoutedEventArgs e)=>await Run(async()=>{await Change(s=>s.SetBarcodeAsync(actor,Id(SetupProduct),Id(PriceUnit),SetupBarcode.Text));Status.Text="تم ربط الباركود.";});
    private async void SetPrice(object sender,RoutedEventArgs e)=>await Run(async()=>{await Change(s=>s.SetPriceAsync(actor,Id(SetupProduct),Id(PriceUnit),Id(PriceCurrency),SetupTier.Text,Number(SetupPrice)));Status.Text="تم حفظ السعر.";});
    private async void CreateProduct(object sender,RoutedEventArgs e)=>await Run(async()=>{
        var id=await Use(s=>s.CreateProductAsync(actor,new(Code.Text,ProductName.Text,Id(BaseUnit),Method.SelectedIndex==0?CostMethod.WeightedAverage:CostMethod.LastPurchase,Tracked.IsChecked==true,ServiceItem.IsChecked!=true)));
        await LoadLookups();
        SetupProduct.SelectedValue = id;
        Code.Clear();
        ProductName.Clear();
        if (returnToInvoice)
        {
            returnToInvoice = false;
            Item.SelectedValue = id;
            await LineOptions();
            Unit.SelectedValue = Id(BaseUnit);
            Tabs.SelectedItem = InvoiceTab;
            Quantity.Focus();
        }
        Status.Text = "تم إنشاء الصنف ووحدة الأساس.";
    });
    private async void ChangeMethod(object sender,RoutedEventArgs e)=>await Run(async()=>{await Change(s=>s.ChangeCostMethodAsync(actor,Id(SetupProduct),ExistingMethod.SelectedIndex==0?CostMethod.WeightedAverage:CostMethod.LastPurchase));await LoadLookups();Status.Text="حفظت طريقة التكلفة.";});
    private async void CreateUnit(object sender,RoutedEventArgs e)=>await Run(async()=>{await Change(s=>s.AddUnitAsync(actor,Id(SetupProduct),Id(ExtraUnit),Number(Factor)));await LineOptions();Status.Text="حفظ تحويل الوحدة.";});
    private async void CreateBatch(object sender,RoutedEventArgs e)=>await Run(async()=>{await Use(s=>s.CreateBatchAsync(actor,branch,Id(SetupProduct),Id(BatchWarehouse),BatchCode.Text,Day(Expiry)));await LineOptions();Status.Text="تم إنشاء الدفعة.";});
    private async void SetLimit(object sender,RoutedEventArgs e)=>await Run(async()=>{await Change(s=>s.SetCreditLimitAsync(actor,branch,Id(LimitCustomer),Number(LimitAmount)));Status.Text="تم تحديث حد الائتمان.";});
    private async void SetPrecision(object sender,RoutedEventArgs e)=>await Run(async()=>{await Change(s=>s.SetCurrencyPrecisionAsync(actor,Id(CurrencySettings),int.Parse(Decimals.Text,CultureInfo.InvariantCulture)));Status.Text="حفظت دقة العملة.";});
    private async void Configure(object sender,RoutedEventArgs e)=>await Run(async()=>{await Change(s=>s.ConfigureAsync(actor,branch,new(Id(StockAccount),Id(SalesAccount),Id(CostAccount),Id(Adjustment),Id(InputTax),Id(OutputTax),Id(FxGain)==0?null:Id(FxGain),Id(FxLoss)==0?null:Id(FxLoss),(LastPurchaseBookPolicy)BookPolicy.SelectedIndex,Id(SalesReturnsAccount)==0?null:Id(SalesReturnsAccount))));Status.Text="حفظت حسابات الترحيل.";});
    private async void Settle(object sender,RoutedEventArgs e)=>await Run(async()=>{
        if(Debts.SelectedItem is not DebtBalance debt){Status.Text="اختر فاتورة من قائمة الذمم.";return;}
        var candidate=new DebtPayment(pendingPayment?.SourceKey??Guid.NewGuid(),debt.DocumentId,Day(PaymentDate),Number(Payment),Id(Cash),string.IsNullOrWhiteSpace(CashAmount.Text)?null:Number(CashAmount),string.IsNullOrWhiteSpace(CashRate.Text)?null:Number(CashRate));
        if(pendingPayment!=null && candidate!=pendingPayment) candidate=candidate with {SourceKey=Guid.NewGuid()};
        if(!Confirm($"تسجيل سداد {candidate.Amount} للفاتورة {debt.DocumentId}؟"))return;
        pendingPayment=candidate;await Use(s=>s.SettleAsync(actor,candidate));await RefreshLists();pendingPayment=null;Payment.Clear();CashAmount.Clear();CashRate.Clear();Status.Text="حفظ السداد وقيده.";
    });
}
