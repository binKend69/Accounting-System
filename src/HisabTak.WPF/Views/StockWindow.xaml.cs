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
    private int page;
    private Guid source=Guid.NewGuid();
    private bool busy;
    public sealed record DisplayLine(StockLine Line,string Product,string Unit);
    private sealed record Choice(int Id,string Label);
    private sealed record DocChoice(StockDocument Document,string Label);
    private sealed record BatchChoice(long Id,string Label);
    private sealed record BalanceRow(string Product,string Warehouse,decimal Quantity,decimal BookValue);
    private static readonly Choice[] Kinds=[new(1,"افتتاحي مخزون"),new(2,"شراء"),new(3,"بيع"),new(4,"مردود شراء"),new(5,"مردود بيع"),new(6,"تحويل مخزني"),new(7,"زيادة جرد"),new(8,"نقص جرد")];
    public StockWindow(IServiceScopeFactory scopes,int actor,int branch,int currency,AccountLookup[] accounts,AccountingLookup[]? currencies=null)
    {
        InitializeComponent(); this.scopes=scopes; this.actor=actor; this.branch=branch; this.currency=currency; this.accounts=accounts;this.currencies=currencies??[new(currency,currency.ToString())];
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
        busy=true; Tabs.IsEnabled=false;
        try { await work(); }
        catch(UnauthorizedAccessException){Status.Text="لا تملك صلاحية هذه العملية في الفرع.";}
        catch(DocumentConflictException){Status.Text="تغيرت النسخة المحفوظة. افتح المستند مجددًا من السجل.";}
        catch(Exception e) when(e is ArgumentException or FormatException or OverflowException){Status.Text="راجع الاختيارات والأرقام والتاريخ. استخدم النقطة للفاصل العشري.";}
        catch(InvalidOperationException e) when(e.Message.Contains("valuation policy",StringComparison.Ordinal)){Status.Text="اختر معالجة فرق آخر شراء في إعداد حسابات الفرع."; }
        catch(InvalidOperationException e) when(e.Message.StartsWith("Insufficient",StringComparison.Ordinal)){Status.Text="الكمية المتاحة للصرف لا تكفي. راجع رصيد المستودع وصلاحية الدفعات."; }
        catch(InvalidOperationException e) when(e.Message.Contains("credit limit",StringComparison.OrdinalIgnoreCase)){Status.Text="راجع الحد الائتماني للعميل وقيمة رصيده قبل البيع الآجل."; }
        catch(InvalidOperationException e) when(e.Message.StartsWith("Backdated",StringComparison.Ordinal)){Status.Text="تاريخ المستند أقدم من آخر حركة للصنف في المستودع."; }
        catch(Exception){Status.Text="تعذرت العملية. راجع الرصيد والصلاحية والفترة والحسابات والاتصال ثم افتح النسخة المحفوظة للتحقق.";}
        finally{busy=false;Tabs.IsEnabled=true;Buttons();}
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
        Bind(PriceCurrency,currencies.Select(x=>new Choice(x.Id,x.Label)));var firstLoad=InvoiceCurrency.Items.Count==0;Bind(InvoiceCurrency,currencies.Select(x=>new Choice(x.Id,x.Label))); Bind(CurrencySettings,currencies.Select(x=>new Choice(x.Id,x.Label)));
        if(firstLoad)InvoiceCurrency.SelectedValue=currency;
        var ac=accounts.Where(x=>x.CurrencyId==currency).Select(x=>new Choice(x.Id,x.Label));
        foreach(var box in new[]{StockAccount,SalesAccount,CostAccount,Adjustment,InputTax,OutputTax}) Bind(box,ac);
        foreach(var box in new[]{Party,Cash,LimitCustomer}) Bind(box,accounts.Select(x=>new Choice(x.Id,x.Label)));
        foreach(var box in new[]{FxGain,FxLoss}) Bind(box,new[]{new Choice(0,"غير محدد")}.Concat(ac));
        if(lookups.Accounts is InventoryAccounts a)
        {
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
        Documents.ItemsSource=docs.Select(d=>new DocChoice(d,$"{Kinds.Single(x=>x.Id==(int)d.Draft.Kind).Label} · {(d.Number?.ToString()??"مسودة")} · {d.Draft.Date:yyyy-MM-dd} · {(d.State==1?"مسودة":d.State==2?"مرحل":"معكوس")}")).ToArray();
        var stock=await Use(s=>s.BalancesAsync(actor,branch,Day(AsOf)));
        Balances.ItemsSource=stock.Select(x=>new BalanceRow(Product(x.ProductId),lookups.Warehouses.FirstOrDefault(w=>w.Id==x.WarehouseId)?.Name??x.WarehouseId.ToString(),x.Quantity,x.BookValue)).ToArray();
        Debts.ItemsSource=await Use(s=>s.DebtsAsync(actor,branch));
    }
    private StockDocumentDraft Build()
    {
        var kind=(StockDocumentKind)Id(Kind);
        var trade=kind is StockDocumentKind.Purchase or StockDocumentKind.Sale or StockDocumentKind.PurchaseReturn or StockDocumentKind.SalesReturn;
        return new(source,branch,kind,Day(EntryDate),Id(Warehouse),kind==StockDocumentKind.Transfer?Id(Target):null,Id(InvoiceCurrency),Number(InvoiceRate),trade?Id(Party):0,
            trade && Credit.IsChecked==true,rows.Select(x=>x.Line).ToArray(),TaxEnabled.IsChecked==true,TaxEnabled.IsChecked==true?Number(TaxRate)/100m:0m,TaxEnabled.IsChecked==true && Inclusive.IsChecked==true);
    }
    private bool Dirty()
    {
        if(current==null)return rows.Count!=0;
        if(current.State!=1)return false;
        try{var d=Build();return (d with {Lines=current.Draft.Lines})!=current.Draft || !d.Lines.SequenceEqual(current.Draft.Lines);}
        catch(Exception){return true;}
    }
    private bool Discard()=>!Dirty() || MessageBox.Show(this,"ترك تعديلات المستند غير المحفوظة؟","تأكيد",MessageBoxButton.YesNo)==MessageBoxResult.Yes;
    private void Buttons()
    {
        PreviewButton.IsEnabled=current!=null;SaveButton.IsEnabled=current?.State is null or 1;PostButton.IsEnabled=current?.State==1;ReverseButton.IsEnabled=current?.State==2;
        foreach(var control in new Control[]{Warehouse,Target,Party,InvoiceCurrency,InvoiceRate,Credit,EntryDate,TaxEnabled,TaxRate,Inclusive,Item,Unit,Quantity,Price,Discount,Batch,Original,Reason})control.IsEnabled=SaveButton.IsEnabled;
        Kind.IsEnabled=current==null;
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
        if(!Discard())return;current=null;source=Guid.NewGuid();rows.Clear();Moves.ItemsSource=null;EntryDate.SelectedDate=DateTime.Today;
        InvoiceCurrency.SelectedValue=currency;InvoiceRate.Text="1";Credit.IsChecked=TaxEnabled.IsChecked=Inclusive.IsChecked=false;TaxRate.Text="0";Buttons();Status.Text="مستند جديد.";
    }
    private async void AddLine(object sender,RoutedEventArgs e)=>await Run(()=> {
        if(current?.State is 2 or 3)return Task.CompletedTask;
        var line=new StockLine(Id(Item),Id(Unit),Number(Quantity),Number(Price),Number(Discount),Batch.SelectedValue is long b && b>0?b:null,
            string.IsNullOrWhiteSpace(Reason.Text)?null:Reason.Text.Trim(),string.IsNullOrWhiteSpace(Original.Text)?null:long.Parse(Original.Text,CultureInfo.InvariantCulture));
        rows.Add(new(line,Product(line.ProductId),UnitName(line.UnitId)));return Task.CompletedTask;
    });
    private void RemoveLine(object sender,RoutedEventArgs e){if(current?.State is not (2 or 3) && Lines.SelectedItem is DisplayLine row)rows.Remove(row);}
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
        Row(Kinds.Single(k=>k.Id==(int)d.Kind).Label+" · "+(saved.Number?.ToString()??"مسودة"));
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
    private async void Post(object sender,RoutedEventArgs e)=>await Run(async()=>{
        if(current==null || Dirty()){Status.Text="احفظ التعديلات قبل الترحيل.";return;}
        if(MessageBox.Show(this,"ترحيل النسخة المحفوظة وإثبات آثارها؟","تأكيد",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        ShowDocument(await Use(s=>s.PostAsync(actor,current.Id,current.Version)));await RefreshLists();Status.Text="تم الترحيل.";
    });
    private async void Reverse(object sender,RoutedEventArgs e)=>await Run(async()=>{
        if(current==null || MessageBox.Show(this,"عكس المستند وآثاره في التاريخ المحدد؟","تأكيد",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        ShowDocument(await Use(s=>s.ReverseAsync(actor,current.Id,Day(ReverseDate),Guid.NewGuid())));await RefreshLists();Status.Text="تم العكس.";
    });
    private async void Open(object sender,RoutedEventArgs e)=>await Run(async()=>{if(Documents.SelectedItem is DocChoice doc && Discard()){ShowDocument(await Use(s=>s.GetAsync(actor,doc.Document.Id)));await LineOptions();Status.Text="تم فتح المستند."; }});
    private async void Refresh(object sender,RoutedEventArgs e)=>await Run(RefreshLists);
    private async void Next(object sender,RoutedEventArgs e)=>await Run(async()=>{page++;await RefreshLists();});
    private async void Previous(object sender,RoutedEventArgs e)=>await Run(async()=>{page=Math.Max(0,page-1);await RefreshLists();});
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
        await LoadLookups();SetupProduct.SelectedValue=id;Status.Text="تم إنشاء الصنف ووحدة الأساس.";
    });
    private async void ChangeMethod(object sender,RoutedEventArgs e)=>await Run(async()=>{await Change(s=>s.ChangeCostMethodAsync(actor,Id(SetupProduct),ExistingMethod.SelectedIndex==0?CostMethod.WeightedAverage:CostMethod.LastPurchase));await LoadLookups();Status.Text="حفظت طريقة التكلفة.";});
    private async void CreateUnit(object sender,RoutedEventArgs e)=>await Run(async()=>{await Change(s=>s.AddUnitAsync(actor,Id(SetupProduct),Id(ExtraUnit),Number(Factor)));await LineOptions();Status.Text="حفظ تحويل الوحدة.";});
    private async void CreateBatch(object sender,RoutedEventArgs e)=>await Run(async()=>{await Use(s=>s.CreateBatchAsync(actor,branch,Id(SetupProduct),Id(BatchWarehouse),BatchCode.Text,Day(Expiry)));await LineOptions();Status.Text="تم إنشاء الدفعة.";});
    private async void SetLimit(object sender,RoutedEventArgs e)=>await Run(async()=>{await Change(s=>s.SetCreditLimitAsync(actor,branch,Id(LimitCustomer),Number(LimitAmount)));Status.Text="تم تحديث حد الائتمان.";});
    private async void SetPrecision(object sender,RoutedEventArgs e)=>await Run(async()=>{await Change(s=>s.SetCurrencyPrecisionAsync(actor,Id(CurrencySettings),int.Parse(Decimals.Text,CultureInfo.InvariantCulture)));Status.Text="حفظت دقة العملة.";});
    private async void Configure(object sender,RoutedEventArgs e)=>await Run(async()=>{await Change(s=>s.ConfigureAsync(actor,branch,new(Id(StockAccount),Id(SalesAccount),Id(CostAccount),Id(Adjustment),Id(InputTax),Id(OutputTax),Id(FxGain)==0?null:Id(FxGain),Id(FxLoss)==0?null:Id(FxLoss),(LastPurchaseBookPolicy)BookPolicy.SelectedIndex)));Status.Text="حفظت حسابات الترحيل.";});
    private async void Settle(object sender,RoutedEventArgs e)=>await Run(async()=>{
        if(Debts.SelectedItem is not DebtBalance debt){Status.Text="اختر فاتورة من قائمة الذمم.";return;}
        var candidate=new DebtPayment(pendingPayment?.SourceKey??Guid.NewGuid(),debt.DocumentId,Day(PaymentDate),Number(Payment),Id(Cash),string.IsNullOrWhiteSpace(CashAmount.Text)?null:Number(CashAmount),string.IsNullOrWhiteSpace(CashRate.Text)?null:Number(CashRate));
        if(pendingPayment!=null && candidate!=pendingPayment) candidate=candidate with {SourceKey=Guid.NewGuid()};
        if(MessageBox.Show(this,$"تسجيل سداد {candidate.Amount} للفاتورة {debt.DocumentId}؟","تأكيد",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        pendingPayment=candidate;await Use(s=>s.SettleAsync(actor,candidate));await RefreshLists();pendingPayment=null;Payment.Clear();CashAmount.Clear();CashRate.Clear();Status.Text="حفظ السداد وقيده.";
    });
}
