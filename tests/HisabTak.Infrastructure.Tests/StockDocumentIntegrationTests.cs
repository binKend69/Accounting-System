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
        var settings=new InventoryAccounts(f.Debit,f.Credit,await Account(4),await Account(5),await Account(6),await Account(7));
        await Change<IStockDocumentService>(test,s=>s.ConfigureAsync(f.Owner,f.Branch,settings));
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
