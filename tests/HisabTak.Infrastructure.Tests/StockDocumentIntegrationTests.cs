using HisabTak.Application.Accounting;
using HisabTak.Application.Inventory;
using HisabTak.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Tests;

public sealed partial class LedgerIntegrationTests
{
    private sealed record Store(Fixture F,int Warehouse,int Target,int Product,int Unit,int Box,int Cash,int Customer,int Supplier);
    private static async Task<Store> StoreAsync(SqlTestDatabase test,bool tracked=false,CostMethod method=CostMethod.WeightedAverage)
    {
        var f=await SetupAsync(test); await using var db=test.Context();
        int unit=(await db.Units.FirstAsync(x=>x.NameAr=="حبة")).Id,box=(await db.Units.FirstAsync(x=>x.NameAr=="كرتون")).Id;
        async Task<int> Account(int n,string? classification=null)
        {
            var group=classification==null?(int?)null:(await db.AccountGeneralGroups.SingleAsync(x=>x.SystemCode==classification)).Id;
            return await With<IChartOfAccountsService,int>(test,s=>s.CreateClassifiedAccountAsync(f.Owner,f.Branch,"0091"+n,"Test "+n,f.Group,f.Currency,1,group));
        }
        int cash=await Account(1,"CASH"),customer=await Account(2,"CUSTOMER"),supplier=await Account(3,"SUPPLIER");
        var settings=new InventoryAccounts(f.Debit,f.Credit,await Account(4),await Account(5),await Account(6),await Account(7),BookPolicy:LastPurchaseBookPolicy.KeepAcquisitionValue,SalesReturnsAccountId:await Account(8));
        await Change<IStockDocumentService>(test,s=>s.ConfigureAsync(f.Owner,f.Branch,settings));
        await Change<IStockDocumentService>(test,x=>x.SetCreditLimitAsync(f.Owner,f.Branch,customer,100000));
        Task<int> Warehouse(string name)=>With<IInventorySetupService,int>(test,s=>s.CreateAsync(f.Owner,f.Branch,InventoryReferenceKind.Warehouse,name));
        var w=await Warehouse("Store A"); var target=await Warehouse("Store B");
        var product=await With<IStockDocumentService,int>(test,s=>s.CreateProductAsync(f.Owner,new("001","Item",unit,method,tracked)));
        await Change<IStockDocumentService>(test,s=>s.AddUnitAsync(f.Owner,product,box,10));
        return new(f,w,target,product,unit,box,cash,customer,supplier);
    }
    private static StockDocumentDraft Draft(Store s,StockDocumentKind kind,int account,params StockLine[] lines)=>
        new(Guid.NewGuid(),s.F.Branch,kind,Date,s.Warehouse,kind==StockDocumentKind.Transfer?s.Target:null,s.F.Currency,1,account,account==s.Customer||account==s.Supplier,lines);
    private static Task<StockDocument> Trade(SqlTestDatabase test,Store s,StockDocumentDraft draft)=>
        With<IStockDocumentService,StockDocument>(test,async service=>
        {
            var saved=await service.SaveAsync(s.F.Owner,draft); return await service.PostAsync(s.F.Owner,saved.Id,saved.Version);
        });
    private static Task<IReadOnlyList<StockBalance>> Stock(SqlTestDatabase test,Store s,DateOnly date)=>
        With<IStockDocumentService,IReadOnlyList<StockBalance>>(test,service=>service.BalancesAsync(s.F.Owner,s.F.Branch,date));
    private static Task<IReadOnlyList<DebtBalance>> Debts(SqlTestDatabase test,Store s)=>
        With<IStockDocumentService,IReadOnlyList<DebtBalance>>(test,service=>service.DebtsAsync(s.F.Owner,s.F.Branch));

    private static async Task<StockDocument> AddClearance(SqlTestDatabase test,Store store,StockDocument purchase,decimal fee)
    {
        var settings=(await With<IStockDocumentService,StockLookups>(test,s=>s.LookupsAsync(store.F.Owner,store.F.Branch))).Accounts!;
        await Change<IStockDocumentService>(test,s=>s.ConfigureFeeAsync(store.F.Owner,store.F.Branch,settings.AdjustmentAccountId,FeeTreatment.InventoryByValue));
        var draft=Draft(store,StockDocumentKind.CustomsClearance,store.Cash,purchase.Draft.Lines.ToArray()) with
        { Customs=new(1,"Truck","YE","Parts",1,[new(settings.AdjustmentAccountId,fee,FeeTreatment.InventoryByValue)],purchase.Id) };
        return await Trade(test,store,draft);
    }

    [SqlServerFact]
    public async Task Legacy_last_purchase_policy_is_not_guessed_from_current_configuration()
    {
        await using var test=await SqlTestDatabase.CreateAsync();
        var store=await StoreAsync(test,method:CostMethod.LastPurchase);
        var settings=(await With<IStockDocumentService,StockLookups>(test,s=>s.LookupsAsync(store.F.Owner,store.F.Branch))).Accounts!;
        // Simulate a supported legacy setting; new settings deliberately prohibit receipt revaluation.
        await using(var db=test.Context()) await db.Database.ExecuteSqlRawAsync("UPDATE dbo.StockAccountSettings SET Payload=JSON_MODIFY(Payload,'$.BookPolicy',2)");
        var purchase=await Trade(test,store,Draft(store,StockDocumentKind.Purchase,store.Cash,new StockLine(store.Product,store.Unit,10,20)));
        var next=await Trade(test,store,Draft(store,StockDocumentKind.Purchase,store.Cash,new StockLine(store.Product,store.Unit,10,20)));
        await using(var db=test.Context()) await db.Database.ExecuteSqlRawAsync("UPDATE dbo.StockDocuments SET BookPolicyAtPosting=NULL");
        // Equal-price legacy receipts have no revaluation evidence. Do not silently pick a policy.
        await Assert.ThrowsAsync<InvalidOperationException>(()=>AddClearance(test,store,purchase,50));
        Assert.Equal(400,Assert.Single(await Stock(test,store,Date)).BookValue);
        // A recorded policy resolves the ambiguity; a later receipt resets valuation to its price.
        await using(var db=test.Context()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.StockDocuments SET BookPolicyAtPosting=2 WHERE DocumentId={next.Id}");
        await AddClearance(test,store,purchase,50);
        Assert.Equal(400,Assert.Single(await Stock(test,store,Date)).BookValue);
    }

    [SqlServerFact]
    public async Task Late_fee_follows_transfers_purchases_and_sales_and_can_reverse()
    {
        await using var test=await SqlTestDatabase.CreateAsync();
        var store=await StoreAsync(test);
        var purchase=await Trade(test,store,Draft(store,StockDocumentKind.Purchase,store.Cash,new StockLine(store.Product,store.Unit,10,20)));
        await Trade(test,store,Draft(store,StockDocumentKind.Sale,store.Cash,new StockLine(store.Product,store.Unit,2,40)));
        await Trade(test,store,Draft(store,StockDocumentKind.Transfer,0,new StockLine(store.Product,store.Unit,4)));
        await Trade(test,store,Draft(store,StockDocumentKind.Sale,store.Cash,new StockLine(store.Product,store.Unit,1,40)) with {WarehouseId=store.Target});
        await Trade(test,store,Draft(store,StockDocumentKind.Purchase,store.Cash,new StockLine(store.Product,store.Unit,10,30)));
        await Trade(test,store,Draft(store,StockDocumentKind.Sale,store.Cash,new StockLine(store.Product,store.Unit,7,40)));
        var fee=await AddClearance(test,store,purchase,50);
        var balances=await Stock(test,store,Date);
        Assert.Equal(200,balances.Single(b=>b.WarehouseId==store.Warehouse).BookValue);
        Assert.Equal(75,balances.Single(b=>b.WarehouseId==store.Target).BookValue);
        Assert.Equal(10,balances.Sum(b=>b.Quantity));
        await With<IStockDocumentService,StockDocument>(test,s=>s.ReverseAsync(store.F.Owner,fee.Id,Date,Guid.NewGuid()));
        Assert.Equal(250,(await Stock(test,store,Date)).Sum(b=>b.BookValue));
    }

    [SqlServerFact]
    public async Task Late_fee_preserves_later_last_purchase_price_and_receipt_policy()
    {
        foreach(var policy in new[]{LastPurchaseBookPolicy.KeepAcquisitionValue,LastPurchaseBookPolicy.RevalueAtReceipt})
        {
            await using var test=await SqlTestDatabase.CreateAsync();
            var store=await StoreAsync(test,method:CostMethod.LastPurchase);
            var settings=(await With<IStockDocumentService,StockLookups>(test,s=>s.LookupsAsync(store.F.Owner,store.F.Branch))).Accounts!;
            await using(var db=test.Context()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.StockAccountSettings SET Payload=JSON_MODIFY(Payload,'$.BookPolicy',{(int)policy})");
            var purchase=await Trade(test,store,Draft(store,StockDocumentKind.Purchase,store.Cash,new StockLine(store.Product,store.Unit,10,20)));
            await Trade(test,store,Draft(store,StockDocumentKind.Sale,store.Cash,new StockLine(store.Product,store.Unit,4,40)));
            await Trade(test,store,Draft(store,StockDocumentKind.Purchase,store.Cash,new StockLine(store.Product,store.Unit,10,30)));
            await Trade(test,store,Draft(store,StockDocumentKind.Sale,store.Cash,new StockLine(store.Product,store.Unit,4,40)));
            // Simulate an external settings change; stored posting snapshots must still win.
            await using(var db=test.Context()) await db.Database.ExecuteSqlRawAsync("UPDATE dbo.StockAccountSettings SET Payload=JSON_MODIFY(Payload,'$.BookPolicy',1)");
            await AddClearance(test,store,purchase,50);
            var balance=Assert.Single(await Stock(test,store,Date));
            Assert.Equal(policy==LastPurchaseBookPolicy.KeepAcquisitionValue?330:360,balance.BookValue);
            Assert.Equal(30,balance.LastPurchasePrice);
        }
    }

    [SqlServerFact]
    public async Task Late_fee_handles_count_adjustments_purchase_return_and_second_fee()
    {
        await using var test=await SqlTestDatabase.CreateAsync();
        var store=await StoreAsync(test);
        var purchase=await Trade(test,store,Draft(store,StockDocumentKind.Purchase,store.Cash,new StockLine(store.Product,store.Unit,10,20)));
        await Trade(test,store,Draft(store,StockDocumentKind.Increase,0,new StockLine(store.Product,store.Unit,2,20)));
        await Trade(test,store,Draft(store,StockDocumentKind.Decrease,0,new StockLine(store.Product,store.Unit,2)));
        await Trade(test,store,Draft(store,StockDocumentKind.PurchaseReturn,store.Cash,new StockLine(store.Product,store.Unit,2,OriginalMovementId:purchase.Movements[0].Id)));
        await AddClearance(test,store,purchase,60);
        var balance=Assert.Single(await Stock(test,store,Date));
        Assert.Equal(8,balance.Quantity);
        Assert.Equal(198,balance.BookValue);
        await AddClearance(test,store,purchase,60);
        Assert.Equal(236,Assert.Single(await Stock(test,store,Date)).BookValue);
    }

    [SqlServerFact]
    public async Task Late_fee_handles_returns_before_and_after_adjustment_and_can_reverse()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var store = await StoreAsync(test);
        var owner = store.F.Owner;
        Task<StockDocument> Run(Func<IStockDocumentService,Task<StockDocument>> action) => With<IStockDocumentService,StockDocument>(test,action);
        var purchase = await Trade(test,store,Draft(store,StockDocumentKind.Purchase,store.Cash,new StockLine(store.Product,store.Unit,10,20)));
        var sale = await Trade(test,store,Draft(store,StockDocumentKind.Sale,store.Cash,new StockLine(store.Product,store.Unit,4,40)));
        await Trade(test,store,Draft(store,StockDocumentKind.SalesReturn,store.Cash,new StockLine(store.Product,store.Unit,1,OriginalMovementId:sale.Movements[0].Id)));
        var settings = (await With<IStockDocumentService,StockLookups>(test,s=>s.LookupsAsync(owner,store.F.Branch))).Accounts!;
        await Change<IStockDocumentService>(test,s=>s.ConfigureFeeAsync(owner,store.F.Branch,settings.AdjustmentAccountId,FeeTreatment.InventoryByValue));
        var draft = Draft(store,StockDocumentKind.CustomsClearance,store.Cash,purchase.Draft.Lines.ToArray()) with
        { Date=Date.AddDays(1), Customs = new(1,"Truck","YE","Parts",1,[new(settings.AdjustmentAccountId,50,FeeTreatment.InventoryByValue)],purchase.Id) };
        var saved = await Run(s=>s.SaveAsync(owner,draft));
        var fee = await Run(s=>s.PostAsync(owner,saved.Id,saved.Version));
        Assert.Equal(140,Assert.Single(await Stock(test,store,Date)).BookValue);
        Assert.Equal(175,Assert.Single(await Stock(test,store,Date.AddDays(1))).BookValue);
        await Run(s=>s.ReverseAsync(owner,fee.Id,Date.AddDays(1),Guid.NewGuid()));
        Assert.Equal(140,Assert.Single(await Stock(test,store,Date.AddDays(1))).BookValue);
        var second = await Run(s=>s.SaveAsync(owner,draft with { SourceKey=Guid.NewGuid() }));
        await Run(s=>s.PostAsync(owner,second.Id,second.Version));
        await Trade(test,store,Draft(store,StockDocumentKind.SalesReturn,store.Cash,new StockLine(store.Product,store.Unit,3,OriginalMovementId:sale.Movements[0].Id)) with {Date=Date.AddDays(1)});
        Assert.Equal(250,Assert.Single(await Stock(test,store,Date.AddDays(1))).BookValue);
    }

    [SqlServerFact]
    public async Task Late_credit_clearance_splits_sold_cost_and_stock_and_settles_once()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var store = await StoreAsync(test);
        var owner = store.F.Owner;
        Task<StockDocument> Run(Func<IStockDocumentService,Task<StockDocument>> action) => With<IStockDocumentService,StockDocument>(test,action);
        var purchase = await Trade(test,store,Draft(store,StockDocumentKind.Purchase,store.Cash,new StockLine(store.Product,store.Unit,10,20)));
        var sale = await Trade(test,store,Draft(store,StockDocumentKind.Sale,store.Cash,new StockLine(store.Product,store.Unit,4,40)));
        var settings = (await With<IStockDocumentService,StockLookups>(test,s=>s.LookupsAsync(owner,store.F.Branch))).Accounts!;
        await Change<IStockDocumentService>(test,s=>s.ConfigureFeeAsync(owner,store.F.Branch,settings.AdjustmentAccountId,FeeTreatment.InventoryByValue));
        var draft = Draft(store,StockDocumentKind.CustomsClearance,store.Supplier,purchase.Draft.Lines.ToArray()) with
        { Customs = new(1,"Truck","YE","Parts",1,[new(settings.AdjustmentAccountId,50,FeeTreatment.InventoryByValue)],purchase.Id) };
        var saved = await Run(s=>s.SaveAsync(owner,draft));
        var posted = await Run(s=>s.PostAsync(owner,saved.Id,saved.Version));
        Assert.Equal(posted.Id,(await Run(s=>s.PostAsync(owner,saved.Id,saved.Version))).Id);
        Assert.Equal(150,Assert.Single(await Stock(test,store,Date)).BookValue);
        Assert.Equal(6,Assert.Single(await Stock(test,store,Date)).Quantity);
        Assert.Equal(50,Assert.Single(await Debts(test,store)).RemainingAmount);
        await using var db = test.Context();
        var cost = await db.Database.SqlQuery<decimal>(
            $"SELECT SUM(CASE WHEN Side=1 THEN Amount ELSE -Amount END) AS Value FROM dbo.JournalLines WHERE JournalEntryId={posted.JournalId!.Value} AND AccountId={settings.CostAccountId}").SingleAsync();
        Assert.Equal(20,cost);
        await Trade(test,store,Draft(store,StockDocumentKind.SalesReturn,store.Cash,new StockLine(store.Product,store.Unit,1,OriginalMovementId:sale.Movements[0].Id)));
        Assert.Equal(175,Assert.Single(await Stock(test,store,Date)).BookValue);
        var payment = new DebtPayment(Guid.NewGuid(),posted.Id,Date,50,store.Cash);
        var paid = await With<IStockDocumentService,long>(test,s=>s.SettleAsync(owner,payment));
        Assert.Equal(paid,await With<IStockDocumentService,long>(test,s=>s.SettleAsync(owner,payment)));
        Assert.Equal(0,Assert.Single(await Debts(test,store)).RemainingAmount);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Run(s=>s.ReverseAsync(owner,posted.Id,Date,Guid.NewGuid())));
    }

    [SqlServerFact]
    public async Task Classified_clearance_posts_only_fees_and_reverses_without_changing_purchased_quantity()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var store = await StoreAsync(test);
        var owner = store.F.Owner;
        var branch = store.F.Branch;
        Task<StockDocument> Run(Func<IStockDocumentService,Task<StockDocument>> fn) => With<IStockDocumentService,StockDocument>(test,fn);
        var second = await With<IStockDocumentService,int>(test,s=>s.CreateProductAsync(owner,new("002","Second",store.Unit,CostMethod.WeightedAverage,false)));
        var purchase = await Trade(test,store,Draft(store,StockDocumentKind.Purchase,store.Cash,
            new StockLine(store.Product,store.Unit,10,60),new StockLine(second,store.Unit,10,40)));
        var config = (await With<IStockDocumentService,StockLookups>(test,s=>s.LookupsAsync(owner,branch))).Accounts!;
        var charges = new[] {
            new CustomsCharge(config.AdjustmentAccountId,100,FeeTreatment.InventoryByValue),
            new CustomsCharge(config.SalesAccountId,20,FeeTreatment.InventoryByWeight),
            new CustomsCharge(config.CostAccountId,8,FeeTreatment.Expense),
            new CustomsCharge(config.InputTaxAccountId,5,FeeTreatment.RecoverableTax)
        };
        foreach (var charge in charges)
            await Change<IStockDocumentService>(test,s=>s.ConfigureFeeAsync(owner,branch,charge.AccountId,charge.Treatment!.Value));
        var lines = purchase.Draft.Lines.Select((l,i)=>l with { ShippingWeight=i==0?1:3 }).ToArray();
        var draft = Draft(store,StockDocumentKind.CustomsClearance,store.Cash,lines) with
        { Customs = new(1,"L5","YE","Parts",1,charges,purchase.Id) };
        var saved = await Run(s=>s.SaveAsync(owner,draft));
        var posted = await Run(s=>s.PostAsync(owner,saved.Id,saved.Version));
        Assert.Equal(posted.JournalId,(await Run(s=>s.PostAsync(owner,saved.Id,saved.Version))).JournalId);
        var balances = await Stock(test,store,Date);
        Assert.Equal(665,balances.Single(b=>b.ProductId==store.Product).BookValue);
        Assert.Equal(455,balances.Single(b=>b.ProductId==second).BookValue);
        Assert.Equal(20,balances.Sum(b=>b.Quantity));
        await using var db = test.Context();
        Task<decimal> SignedAmount(int account) => db.Database.SqlQuery<decimal>(
            $"SELECT SUM(CASE WHEN Side=1 THEN Amount ELSE -Amount END) AS Value FROM dbo.JournalLines WHERE JournalEntryId={posted.JournalId!.Value} AND AccountId={account}").SingleAsync();
        Assert.Equal(-133,await SignedAmount(store.Cash));
        Assert.Equal(8,await SignedAmount(config.CostAccountId));
        Assert.Equal(5,await SignedAmount(config.InputTaxAccountId));
        await Run(s=>s.ReverseAsync(owner,posted.Id,Date,Guid.NewGuid()));
        Assert.Equal(1000,(await Stock(test,store,Date)).Sum(b=>b.BookValue));
    }

    [SqlServerFact]
    public async Task Customs_draft_roundtrips_without_posting_and_rejects_fees_on_other_kinds()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var store = await StoreAsync(test);
        var actor = store.F.Owner;
        var declaration = new CustomsDeclaration(1, "T-123", "اليمن", "قطع غيار", 1, [new(store.Cash, 100)]);
        var draft = Draft(store, StockDocumentKind.CustomsClearance, 0, new StockLine(store.Product, store.Unit, 10, 20)) with { Customs = declaration };
        Task<StockDocument> Save(StockDocumentDraft d) => With<IStockDocumentService,StockDocument>(test, s => s.SaveAsync(actor, d));
        var saved = await Save(draft);
        Assert.Equal(declaration.Charges[0], Assert.Single(saved.Draft.Customs!.Charges));
        Assert.Equal("T-123", saved.Draft.Customs.VehicleNumber);
        await Assert.ThrowsAsync<ArgumentException>(() => Save(draft with { Kind = StockDocumentKind.Purchase }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => With<IStockDocumentService,StockDocument>(test, s => s.PostAsync(actor,saved.Id,saved.Version)));
        Assert.Empty(await Stock(test, store, Date));
        Assert.Equal(0, await Count(test,"JournalEntries"));
    }

    [SqlServerFact]
    public async Task Trade_intents_only_affect_stock_and_journals_when_the_linked_invoice_posts()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var store = await StoreAsync(test);
        var actor = store.F.Owner;
        Task<StockDocument> Run(Func<IStockDocumentService, Task<StockDocument>> action) =>
            With<IStockDocumentService, StockDocument>(test, action);

        foreach (var kind in new[] { StockDocumentKind.PurchaseOrder, StockDocumentKind.SalesQuote })
        {
            var buying = kind == StockDocumentKind.PurchaseOrder;
            var draft = Draft(store, kind, store.Cash, new StockLine(store.Product, store.Unit, buying ? 10 : 2, buying ? 20 : 30));
            var intent = await Run(service => service.SaveAsync(actor, draft));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(service => service.PostAsync(actor, intent.Id, intent.Version)));
            Task<StockDocument> Convert() => Run(service => service.ConvertIntentAsync(actor, intent.Id, intent.Version));
            var conversions = await Task.WhenAll(Convert(), Convert());
            var invoice = conversions[0];
            Assert.Equal(invoice.Id, conversions[1].Id);
            Assert.Equal(intent.Id, invoice.OriginIntentId);
            Assert.Equal(buying ? StockDocumentKind.Purchase : StockDocumentKind.Sale, invoice.Draft.Kind);
            Assert.Equal(draft.Lines.ToArray(), invoice.Draft.Lines.ToArray());
            Assert.Null(invoice.JournalId);
            Assert.Empty(invoice.Movements);
            var original = await Run(service => service.GetAsync(actor, intent.Id));
            Assert.Equal(invoice.Id, original.ConvertedInvoiceId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(service => service.UpdateAsync(actor, intent.Id, intent.Version, draft)));
            Assert.Equal(buying ? 0m : 10m, (await Stock(test, store, Date)).Sum(row => row.Quantity));
            Assert.Equal(buying ? 0 : 1, await Count(test, "JournalEntries"));
            var posted = await Run(service => service.PostAsync(actor, invoice.Id, invoice.Version));
            Assert.NotNull(posted.JournalId);
            Assert.Equal(buying ? 10m : 8m, Assert.Single(await Stock(test, store, Date)).Quantity);
        }
    }

    [SqlServerFact]
    public async Task Pos_slots_restore_and_concurrent_payment_posts_once()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var store = await StoreAsync(test);
        var actor = store.F.Owner;
        var branch = store.F.Branch;
        Task<PosSlot> Load(int slot) => With<IPosSlots, PosSlot>(test, x => x.LoadAsync(actor, branch, slot));
        Task<StockDocument> Pay(int version) => With<IPosSlots, StockDocument>(test, x => x.PayAsync(actor, branch, 1, version));
        for (var slot = 1; slot <= 4; slot++)
        {
            var draft = Draft(store, StockDocumentKind.Sale, store.Cash, new StockLine(store.Product, store.Unit, slot, 30));
            await With<IPosSlots, PosSlot>(test, x => x.SaveSlotAsync(actor, branch, slot, 0, draft));
        }
        for (var slot = 1; slot <= 4; slot++)
            Assert.Equal((decimal)slot, Assert.Single((await Load(slot)).Draft!.Lines).Quantity);
        await Assert.ThrowsAsync<DocumentConflictException>(() =>
            With<IPosSlots, PosSlot>(test, x => x.SaveSlotAsync(actor, branch, 1, 0, null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Pay(1));
        Assert.NotNull((await Load(1)).Draft);
        await Trade(test, store, Draft(store, StockDocumentKind.Purchase, store.Cash, new StockLine(store.Product, store.Unit, 10, 20)));
        await Change<IStockDocumentService>(test, service => service.SetCashierProductAsync(actor, store.Product, null, true));
        var receipts = await Task.WhenAll(Pay(1), Pay(1));
        Assert.Equal(receipts[0].Id, (await Load(1)).LastReceiptId);
        Assert.True(Assert.Single(receipts[0].Draft.Lines).PrintInKitchen);
        await Change<IStockDocumentService>(test, service => service.SetCashierProductAsync(actor, store.Product, null, false));
        var reprint = await With<IStockDocumentService,StockDocument>(test, service => service.GetAsync(actor,receipts[0].Id));
        Assert.True(Assert.Single(reprint.Draft.Lines).PrintInKitchen);
        Assert.Equal(receipts[0].Id, receipts[1].Id);
        Assert.NotNull(receipts[0].JournalId);
        Assert.Null((await Load(1)).Draft);
        Assert.NotNull((await Load(2)).Draft);
        Assert.Equal(9m, Assert.Single(await Stock(test, store, Date)).Quantity);
    }

    [SqlServerFact]
    public async Task Unit_barcode_resolves_box_and_price_is_scoped_to_currency_and_tier()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test);
        await Change<IStockDocumentService>(test,x=>x.SetBarcodeAsync(s.F.Owner,s.Product,s.Box,"0000123"));
        var match=await With<IStockDocumentService,ProductUnitInfo?>(test,x=>x.ScanAsync(s.F.Owner,"0000123"));
        Assert.NotNull(match);Assert.Equal(s.Box,match.UnitId);Assert.Equal(10,match.Factor);
        await Change<IStockDocumentService>(test,x=>x.SetPriceAsync(s.F.Owner,s.Product,s.Box,s.F.Currency,"retail",1250));
        Task<decimal?> Price(int unit,int currency,string tier)=>With<IStockDocumentService,decimal?>(test,x=>x.PriceAsync(s.F.Owner,s.Product,unit,currency,tier));
        Assert.Equal(1250,await Price(s.Box,s.F.Currency,"retail"));Assert.Null(await Price(s.Unit,s.F.Currency,"retail"));
        Assert.Null(await Price(s.Box,s.F.OtherCurrency,"retail"));Assert.Null(await Price(s.Box,s.F.Currency,"wholesale"));
        var ids=await Task.WhenAll(Enumerable.Range(0,2).Select(i=>With<IStockDocumentService,int>(test,x=>x.CreateProductAsync(s.F.Owner,new("","Auto "+i,s.Unit,CostMethod.WeightedAverage,false)))));
        var all=await With<IStockDocumentService,IReadOnlyList<ProductInfo>>(test,x=>x.ProductsAsync(s.F.Owner));
        Assert.Equal(2,all.Where(x=>ids.Contains(x.Id)).Select(x=>x.Code).Distinct().Count());
    }
    [SqlServerFact]
    public async Task Service_sale_and_original_linked_return_change_cash_but_never_stock()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test);
        var item=await With<IStockDocumentService,int>(test,x=>x.CreateProductAsync(s.F.Owner,new("S-1","Delivery",s.Unit,CostMethod.WeightedAverage,false,false)));
        var sale=await Trade(test,s,Draft(s,StockDocumentKind.Sale,s.Cash,new StockLine(item,s.Unit,2,50)));
        var original=Assert.Single(sale.Movements);Assert.Equal(2,original.BilledQuantity);Assert.Equal(0,original.Quantity);
        await Trade(test,s,Draft(s,StockDocumentKind.SalesReturn,s.Cash,new StockLine(item,s.Unit,1,OriginalMovementId:original.Id)));
        Assert.Empty(await Stock(test,s,Date));
        var cash=Assert.Single(await With<IJournalService,IReadOnlyList<LedgerBalance>>(test,x=>x.BalancesAsync(s.F.Owner,s.F.Branch,s.F.Currency,Date)),x=>x.AccountId==s.Cash);
        Assert.Equal(50,cash.Debit-cash.Credit);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Trade(test,s,Draft(s,StockDocumentKind.SalesReturn,s.Cash,new StockLine(item,s.Unit,2,OriginalMovementId:original.Id))));
    }
    [SqlServerFact]
    public async Task Manual_batch_selection_requires_an_explicit_grant_and_reason()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test,true);
        var batch=await With<IStockDocumentService,long>(test,x=>x.CreateBatchAsync(s.F.Owner,s.F.Branch,s.Product,s.Warehouse,"batch",Date.AddDays(1)));
        await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,2,10,BatchId:batch)));
        var line=new StockLine(s.Product,s.Unit,1,20,BatchId:batch);
        await Assert.ThrowsAnyAsync<ArgumentException>(()=>Trade(test,s,Draft(s,StockDocumentKind.Sale,s.Cash,line)));
        var draft=Draft(s,StockDocumentKind.Sale,s.Cash,line with {BatchOverrideReason="Customer batch request"});
        int clerk;
        await using(var db=test.Context())
        {
            var user=new Domain.Security.User("stock-clerk",new HisabTak.Infrastructure.Security.BCryptPasswordHasher().Hash("Test-"+Guid.NewGuid().ToString("N")));
            db.Users.Add(user);await db.SaveChangesAsync();clerk=user.Id;
            var grants=await db.Permissions.Where(x=>x.Code==Domain.Security.PermissionCodes.AccountingAllBranches || x.Code==Domain.Security.PermissionCodes.JournalPost).ToListAsync();
            foreach(var grant in grants)db.UserPermissions.Add(new Domain.Security.UserPermission(clerk,grant.Id));
            await db.SaveChangesAsync();
        }
        var saved=await With<IStockDocumentService,StockDocument>(test,x=>x.SaveAsync(clerk,draft));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>With<IStockDocumentService,StockDocument>(test,x=>x.PostAsync(clerk,saved.Id,saved.Version)));
        var posted=await With<IStockDocumentService,StockDocument>(test,x=>x.PostAsync(s.F.Owner,saved.Id,saved.Version));
        Assert.Equal("Customer batch request",posted.Draft.Lines[0].BatchOverrideReason);
        Assert.Equal(batch,Assert.Single(posted.Movements).BatchId);
    }

    [SqlServerFact]
    public async Task Concurrent_sales_cannot_spend_the_same_stock_and_exceeded_limit_leaves_no_effect()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test);
        await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,5,20)));
        await Change<IStockDocumentService>(test,x=>x.SetCreditLimitAsync(s.F.Owner,s.F.Branch,s.Customer,30));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Trade(test,s,Draft(s,StockDocumentKind.Sale,s.Customer,new StockLine(s.Product,s.Unit,4,30))));
        Assert.Empty(await Debts(test,s));
        async Task<bool> Sell()
        {
            try {await Trade(test,s,Draft(s,StockDocumentKind.Sale,s.Cash,new StockLine(s.Product,s.Unit,4,30)));return true;}
            catch(InvalidOperationException){return false;}
        }
        var succeeded=await Task.WhenAll(Sell(),Sell());Assert.Single(succeeded,x=>x);
        var stock=Assert.Single(await Stock(test,s,Date));Assert.Equal(1,stock.Quantity);Assert.Equal(20,stock.BookValue);
    }

    [SqlServerFact]
    public async Task Stock_purchase_sale_partial_return_and_settlement_reconcile()
    {
        await using var test=await SqlTestDatabase.CreateAsync(); var s=await StoreAsync(test);
        var purchase=await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Supplier,new StockLine(s.Product,s.Box,1,1000)));
        var sale=await Trade(test,s,Draft(s,StockDocumentKind.Sale,s.Customer,new StockLine(s.Product,s.Unit,3,150)));
        var balance=Assert.Single(await Stock(test,s,Date));
        Assert.Equal(7,balance.Quantity); Assert.Equal(700,balance.BookValue);
        var journal=await With<IJournalService,PostedJournal>(test,j=>j.GetAsync(s.F.Owner,sale.JournalId!.Value));
        Assert.Contains(journal.Lines,l=>l.AccountId==s.F.Debit && l.Amount==300 && l.Side==Domain.Accounting.JournalSide.Credit);
        var returned=await Trade(test,s,Draft(s,StockDocumentKind.SalesReturn,s.Customer,new StockLine(s.Product,s.Unit,1,OriginalMovementId:sale.Movements[0].Id)));
        Assert.Equal(100,Assert.Single(returned.Movements).Value);
        Assert.Equal(300,(await Debts(test,s)).Single(x=>x.DocumentId==sale.Id).RemainingAmount);
        var payment=new DebtPayment(Guid.NewGuid(),sale.Id,Date,200,s.Cash);
        Task<long> Pay(DebtPayment p)=>With<IStockDocumentService,long>(test,x=>x.SettleAsync(s.F.Owner,p));
        var ids=await Task.WhenAll(Pay(payment),Pay(payment)); Assert.Equal(ids[0],ids[1]);
        Assert.Equal(100,(await Debts(test,s)).Single(x=>x.DocumentId==sale.Id).RemainingAmount);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Pay(payment with { SourceKey=Guid.NewGuid(),Amount=101 }));
        await Trade(test,s,Draft(s,StockDocumentKind.PurchaseReturn,s.Supplier,new StockLine(s.Product,s.Unit,2,OriginalMovementId:purchase.Movements[0].Id)));
        Assert.Equal(800,(await Debts(test,s)).Single(x=>x.DocumentId==purchase.Id).RemainingAmount);
        Assert.Equal(600,Assert.Single(await Stock(test,s,Date)).BookValue);
        var vouchers=await With<IJournalDocumentService,IReadOnlyList<JournalDocument>>(test,j=>j.ListAsync(s.F.Owner,s.F.Branch));
        var receipt=Assert.Single(vouchers,x=>x.JournalId==ids[0]);
        Assert.Equal("RECEIPT_VOUCHER",receipt.TypeCode);
        await With<IJournalDocumentService,JournalDocument>(test,j=>j.ReverseAsync(s.F.Owner,receipt.Id,Date.AddDays(1),Guid.NewGuid()));
        Assert.Equal(300,(await Debts(test,s)).Single(x=>x.DocumentId==sale.Id).RemainingAmount);
    }
    [SqlServerFact]
    public async Task Standalone_journal_cannot_claim_an_unrelated_historical_allocation()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var f=await SetupAsync(test);
        var draft=f.Draft();
        await using(var db=test.Context())await db.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.StockBookBasis(SourceKey,AccountId,Principal,BookValue) VALUES({draft.SourceKey},{f.Debit},100,0.01)");
        var error=await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(()=>Post(test,f,draft));
        Assert.Equal(51159,error.Number);Assert.Equal(0,await Count(test,"JournalEntries"));
    }

    [SqlServerFact]
    public async Task Foreign_invoice_and_final_settlement_reconcile_principal_book_and_realized_exchange()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test);var f=s.F;
        await using var database=test.Context();
        var customerGroup=(await database.AccountGeneralGroups.SingleAsync(x=>x.SystemCode=="CUSTOMER")).Id;
        var foreignCustomer=await With<IChartOfAccountsService,int>(test,x=>x.CreateClassifiedAccountAsync(f.Owner,f.Branch,"009280","SAR customer",f.Group,f.OtherCurrency,1,customerGroup));
        Task<int> Account(string code)=>With<IChartOfAccountsService,int>(test,x=>x.CreateAccountAsync(f.Owner,code,code,f.Group,f.Currency,1,f.Branch));
        await Change<IStockDocumentService>(test,x=>x.SetCreditLimitAsync(f.Owner,f.Branch,foreignCustomer,1000));
        var gain=await Account("009281");var loss=await Account("009282");
        var settings=(await With<IStockDocumentService,StockLookups>(test,x=>x.LookupsAsync(f.Owner,f.Branch))).Accounts!;
        await Change<IStockDocumentService>(test,x=>x.ConfigureAsync(f.Owner,f.Branch,settings with { FxGainAccountId=gain,FxLossAccountId=loss }));
        await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,3,10)));
        var sale=await Trade(test,s,Draft(s,StockDocumentKind.Sale,foreignCustomer,new StockLine(s.Product,s.Unit,3,1)) with {Credit=true,CurrencyId=f.OtherCurrency,RateToBase=33.33333333m});
        var opening=Assert.Single(await Debts(test,s));Assert.Equal(3,opening.OriginalAmount);Assert.Equal(100,opening.OriginalBookValue);
        foreach(var expected in new[]{33.33m,33.33m,33.34m})
        {
            var payment=new DebtPayment(Guid.NewGuid(),sale.Id,Date,1,s.Cash,35,1);
            var journal=await With<IStockDocumentService,long>(test,x=>x.SettleAsync(f.Owner,payment));
            var posted=await With<IJournalService,PostedJournal>(test,x=>x.GetAsync(f.Owner,journal));
            var principal=Assert.Single(posted.Lines,x=>x.AccountId==foreignCustomer);
            Assert.Equal(expected,principal.BookBaseAmount);Assert.Equal(33.33333333m,principal.RateToBase);
            Assert.Contains(posted.Lines,x=>x.AccountId==gain && x.Side==Domain.Accounting.JournalSide.Credit);
        }
        var closed=Assert.Single(await Debts(test,s));Assert.Equal(0,closed.RemainingAmount);Assert.Equal(0,closed.RemainingBookValue);
        var balances=await With<IJournalService,IReadOnlyList<LedgerBalance>>(test,x=>x.BalancesAsync(f.Owner,f.Branch,f.OtherCurrency,Date));
        var customer=Assert.Single(balances,x=>x.AccountId==foreignCustomer);
        Assert.Equal(customer.Debit,customer.Credit);Assert.Equal(customer.BaseDebit,customer.BaseCredit);
    }

    [SqlServerFact]
    public async Task Paid_credit_invoice_return_creates_refund_and_payment_voucher()
    {
        await using var test=await SqlTestDatabase.CreateAsync(); var s=await StoreAsync(test);
        await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,4,100)));
        var sale=await Trade(test,s,Draft(s,StockDocumentKind.Sale,s.Customer,new StockLine(s.Product,s.Unit,2,110)) with
            {TaxEnabled=true,TaxRate=0.1m,PricesIncludeTax=true});
        Assert.Equal(200,sale.Movements[0].Net); Assert.Equal(20,sale.Movements[0].Tax);
        await With<IStockDocumentService,long>(test,x=>x.SettleAsync(s.F.Owner,new(Guid.NewGuid(),sale.Id,Date,220,s.Cash)));
        var returned=await Trade(test,s,Draft(s,StockDocumentKind.SalesReturn,s.Customer,new StockLine(s.Product,s.Unit,1,OriginalMovementId:sale.Movements[0].Id)));
        var refund=(await Debts(test,s)).Single(x=>x.DocumentId==returned.Id);
        Assert.True(returned.Draft.TaxEnabled); Assert.Equal(0.1m,returned.Draft.TaxRate);
        Assert.False(refund.Receivable); Assert.Equal(110,refund.RemainingAmount);
        var journal=await With<IStockDocumentService,long>(test,x=>x.SettleAsync(s.F.Owner,new(Guid.NewGuid(),returned.Id,Date,110,s.Cash)));
        Assert.Equal(0,(await Debts(test,s)).Single(x=>x.DocumentId==returned.Id).RemainingAmount);
        var vouchers=await With<IJournalDocumentService,IReadOnlyList<JournalDocument>>(test,x=>x.ListAsync(s.F.Owner,s.F.Branch));
        Assert.Contains(vouchers,x=>x.TypeCode=="PAYMENT_VOUCHER" && x.JournalId==journal);
    }

    [SqlServerFact]
    public async Task Stock_fefo_splits_by_expiry_transfers_value_and_rejects_expired_or_backdated_issue()
    {
        await using var test=await SqlTestDatabase.CreateAsync(); var s=await StoreAsync(test,true);
        Task<long> Batch(string code,int days)=>With<IStockDocumentService,long>(test,x=>x.CreateBatchAsync(s.F.Owner,s.F.Branch,s.Product,s.Warehouse,code,Date.AddDays(days)));
        var late=await Batch("late",10); var early=await Batch("early",1); var expired=await Batch("expired",-1);
        await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,4,100,BatchId:late),
            new StockLine(s.Product,s.Unit,2,100,BatchId:early),new StockLine(s.Product,s.Unit,2,100,BatchId:expired)));
        var transfer=await Trade(test,s,Draft(s,StockDocumentKind.Transfer,0,new StockLine(s.Product,s.Unit,3)));
        var issues=transfer.Movements.Where(x=>x.Quantity<0).ToArray();
        Assert.Equal(early,issues[0].BatchId); Assert.Equal(-2,issues[0].Quantity); Assert.Equal(late,issues[1].BatchId);
        var balances=await Stock(test,s,Date); Assert.Equal(300,balances.Single(x=>x.WarehouseId==s.Target).BookValue);
        Assert.Equal(800,balances.Sum(x=>x.BookValue));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Trade(test,s,Draft(s,StockDocumentKind.Sale,s.Cash,new StockLine(s.Product,s.Unit,4,150))));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,1,150,BatchId:late)) with { Date=Date.AddDays(-1) }));
    }
    [SqlServerFact]
    public async Task New_inventory_settings_default_to_acquisition_value_and_reject_revaluation()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test);
        var settings=(await With<IStockDocumentService,StockLookups>(test,x=>x.LookupsAsync(s.F.Owner,s.F.Branch))).Accounts!;
        Assert.Equal(LastPurchaseBookPolicy.KeepAcquisitionValue,new InventoryAccounts(1,2,3,4,5,6).BookPolicy);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Change<IStockDocumentService>(test,x=>x.ConfigureAsync(s.F.Owner,s.F.Branch,settings with {BookPolicy=LastPurchaseBookPolicy.RevalueAtReceipt})));
        var reloaded=(await With<IStockDocumentService,StockLookups>(test,x=>x.LookupsAsync(s.F.Owner,s.F.Branch))).Accounts!;
        Assert.Equal(settings,reloaded);
    }
    [SqlServerFact]
    public async Task Purchase_prices_remain_distinct_and_reversal_preserves_historical_acquisition_value()
    {
        await using var test=await SqlTestDatabase.CreateAsync(); var s=await StoreAsync(test,method:CostMethod.LastPurchase);
        var first=await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,10,20)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Change<IStockDocumentService>(test,x=>x.ChangeCostMethodAsync(s.F.Owner,s.Product,CostMethod.WeightedAverage)));
        var later=await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,10,30)) with {Date=Date.AddDays(1)});
        var original=await With<IStockDocumentService,StockDocument>(test,x=>x.GetAsync(s.F.Owner,first.Id));
        var second=await With<IStockDocumentService,StockDocument>(test,x=>x.GetAsync(s.F.Owner,later.Id));
        Assert.Equal(20,Assert.Single(original.Draft.Lines).UnitPrice);
        Assert.Equal(30,Assert.Single(second.Draft.Lines).UnitPrice);
        Assert.Equal(200,Assert.Single(original.Movements).Value);
        Assert.Equal(300,Assert.Single(second.Movements).Value);
        var position=Assert.Single(await Stock(test,s,Date.AddDays(1)));
        Assert.Equal(20,position.Quantity); Assert.Equal(30,position.LastPurchasePrice);
        var ledger=await With<IJournalService,IReadOnlyList<LedgerBalance>>(test,x=>x.BalancesAsync(s.F.Owner,s.F.Branch,s.F.Currency,Date.AddDays(1)));
        var inventory=Assert.Single(ledger,x=>x.AccountId==s.F.Debit);
        Assert.Equal(500,inventory.Debit-inventory.Credit);
        Assert.Equal(500,Assert.Single(await Stock(test,s,Date.AddDays(1))).BookValue);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>With<IJournalService,long>(test,j=>j.ReverseAsync(s.F.Owner,later.JournalId!.Value,Date.AddDays(2),Guid.NewGuid())));
        await With<IStockDocumentService,StockDocument>(test,x=>x.ReverseAsync(s.F.Owner,later.Id,Date.AddDays(2),Guid.NewGuid()));
        Assert.Equal(500,Assert.Single(await Stock(test,s,Date.AddDays(1))).BookValue);
        Assert.Equal(200,Assert.Single(await Stock(test,s,Date.AddDays(2))).BookValue);
    }
    [SqlServerFact]
    public async Task Stock_rollback_removes_partial_movements_debt_number_and_journal()
    {
        await using var test=await SqlTestDatabase.CreateAsync(); var s=await StoreAsync(test);
        var d=Draft(s,StockDocumentKind.Purchase,s.Supplier,new StockLine(s.Product,s.Unit,3,100));
        var saved=await With<IStockDocumentService,StockDocument>(test,x=>x.SaveAsync(s.F.Owner,d));
        await using(var db=test.Context()) await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_TestStockAudit ON dbo.AuditLogs AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE OperationType='STOCK_DOCUMENT_POSTED') THROW 51206,'Test failure',1; END");
        await Assert.ThrowsAnyAsync<Exception>(()=>With<IStockDocumentService,StockDocument>(test,x=>x.PostAsync(s.F.Owner,saved.Id,1)));
        Assert.Empty(await Stock(test,s,Date)); Assert.Empty(await Debts(test,s)); Assert.Equal(0,await Count(test,"JournalEntries"));
        var reloaded=await With<IStockDocumentService,StockDocument>(test,x=>x.GetAsync(s.F.Owner,saved.Id)); Assert.Null(reloaded.Number); Assert.Empty(reloaded.Movements);
        await using(var db=test.Context()) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_TestStockAudit");
        var posted=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>With<IStockDocumentService,StockDocument>(test,x=>x.PostAsync(s.F.Owner,saved.Id,1))));
        Assert.Equal(1,posted[0].Number); Assert.Equal(posted[0].JournalId,posted[1].JournalId);
    }
}
