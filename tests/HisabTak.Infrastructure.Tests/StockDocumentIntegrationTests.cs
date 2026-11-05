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
        var settings=new InventoryAccounts(f.Debit,f.Credit,await Account(4),await Account(5),await Account(6),await Account(7),BookPolicy:LastPurchaseBookPolicy.RevalueAtReceipt);
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
    public async Task Last_purchase_receipt_revalues_remaining_stock_and_reversal_preserves_as_of_balances()
    {
        await using var test=await SqlTestDatabase.CreateAsync(); var s=await StoreAsync(test,method:CostMethod.LastPurchase);
        await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,10,100)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Change<IStockDocumentService>(test,x=>x.ChangeCostMethodAsync(s.F.Owner,s.Product,CostMethod.WeightedAverage)));
        var later=await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,2,120)) with {Date=Date.AddDays(1)});
        Assert.Equal(1440,Assert.Single(await Stock(test,s,Date.AddDays(1))).BookValue);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>With<IJournalService,long>(test,j=>j.ReverseAsync(s.F.Owner,later.JournalId!.Value,Date.AddDays(2),Guid.NewGuid())));
        await With<IStockDocumentService,StockDocument>(test,x=>x.ReverseAsync(s.F.Owner,later.Id,Date.AddDays(2),Guid.NewGuid()));
        Assert.Equal(1440,Assert.Single(await Stock(test,s,Date.AddDays(1))).BookValue);
        Assert.Equal(1000,Assert.Single(await Stock(test,s,Date.AddDays(2))).BookValue);
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
