using HisabTak.Application.Accounting;
using HisabTak.Application.Inventory;
using HisabTak.Application.Setup;
using HisabTak.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Tests;

public sealed partial class LedgerIntegrationTests
{
    private static Task<StockHistoryPage> History(SqlTestDatabase test,Store s,StockHistoryFilter filter)=>
        With<IStockDocumentService,StockHistoryPage>(test,x=>x.HistoryAsync(s.F.Owner,s.F.Branch,filter));
    private static async Task MatchStockLedger(SqlTestDatabase test,Store s,DateOnly date,decimal quantity,decimal value)
    {
        var stock=await Stock(test,s,date);
        Assert.Equal(quantity,stock.Sum(x=>x.Quantity));Assert.Equal(value,stock.Sum(x=>x.BookValue));
        var ledger=await With<IJournalService,IReadOnlyList<LedgerBalance>>(test,x=>x.BalancesAsync(s.F.Owner,s.F.Branch,s.F.Currency,date));
        Assert.Equal(value,ledger.Where(x=>x.AccountId==s.F.Debit).Sum(x=>x.Debit-x.Credit));
    }
    [SqlServerFact]
    public async Task Rising_and_falling_costs_reconcile_partial_full_sales_and_original_returns()
    {
        foreach(var method in new[]{CostMethod.WeightedAverage,CostMethod.LastPurchase})
        foreach(var secondPrice in new[]{10m,30m})
        {
            await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test,method:method);
            var first=await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,10,20)));
            await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,10,secondPrice)));
            var book=200+10*secondPrice;
            await MatchStockLedger(test,s,Date,20,book);
            var unitCost=method==CostMethod.WeightedAverage?(20+secondPrice)/2:secondPrice;
            var sale=await Trade(test,s,Draft(s,StockDocumentKind.Sale,s.Cash,new StockLine(s.Product,s.Unit,3,50)));
            Assert.Equal(-3*unitCost,Assert.Single(sale.Movements).Value);book-=3*unitCost;
            await MatchStockLedger(test,s,Date,17,book);
            var returned=await Trade(test,s,Draft(s,StockDocumentKind.SalesReturn,s.Cash,new StockLine(s.Product,s.Unit,1,OriginalMovementId:sale.Movements[0].Id)));
            Assert.Equal(unitCost,Assert.Single(returned.Movements).Value);book+=unitCost;
            await MatchStockLedger(test,s,Date,18,book);
            await Trade(test,s,Draft(s,StockDocumentKind.PurchaseReturn,s.Cash,new StockLine(s.Product,s.Unit,2,OriginalMovementId:first.Movements[0].Id)));
            book-=40;await MatchStockLedger(test,s,Date,16,book);
            var final=await Trade(test,s,Draft(s,StockDocumentKind.Sale,s.Cash,new StockLine(s.Product,s.Unit,16,50)));
            var expectedCost=method==CostMethod.WeightedAverage?book:16*secondPrice;
            Assert.Equal(-expectedCost,final.Movements.Where(x=>x.Quantity<0).Sum(x=>x.Value));
            Assert.Equal(expectedCost-book,final.Movements.Where(x=>x.Quantity==0).Sum(x=>x.Value));
            await MatchStockLedger(test,s,Date,0,0);
            var report=Assert.Single((await History(test,s,new(Date,Date,Kind:StockDocumentKind.Sale))).Rows,x=>x.DocumentId==final.Id);
            Assert.Equal(-expectedCost,report.HistoricalCost);Assert.Equal(expectedCost-book,report.ValuationDifference);
            Assert.Equal(-book,report.StockValue);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>With<IStockDocumentService,StockDocument>(test,x=>x.ReverseAsync(s.F.Owner,first.Id,Date.AddDays(1),Guid.NewGuid())));
            await With<IStockDocumentService,StockDocument>(test,x=>x.ReverseAsync(s.F.Owner,final.Id,Date.AddDays(1),Guid.NewGuid()));
            await MatchStockLedger(test,s,Date,0,0);await MatchStockLedger(test,s,Date.AddDays(1),16,book);
        }
    }
    [SqlServerFact]
    public async Task Fractional_units_keep_final_rounding_residual_and_history_uses_invoice_units()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test);
        await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Box,0.125m,80)));
        await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Box,0.175m,120)));
        await MatchStockLedger(test,s,Date,3,31);
        await Trade(test,s,Draft(s,StockDocumentKind.Sale,s.Cash,new StockLine(s.Product,s.Unit,1,50)));
        await MatchStockLedger(test,s,Date,2,20.67m);
        await Trade(test,s,Draft(s,StockDocumentKind.Sale,s.Cash,new StockLine(s.Product,s.Unit,2,50)));
        await MatchStockLedger(test,s,Date,0,0);
        var history=await History(test,s,new(Date,Date,Kind:StockDocumentKind.Purchase));
        Assert.Equal(new[]{0.175m,0.125m},history.Rows.Select(x=>x.Quantity));
        Assert.Equal(new[]{1.75m,1.25m},history.Rows.Select(x=>x.BaseQuantity));
        Assert.Equal(31,history.Rows.Sum(x=>x.Net));
    }
    [SqlServerFact]
    public async Task Separate_sales_returns_account_can_be_added_to_legacy_settings_once_without_rewriting_sales()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test);
        var settings=(await With<IStockDocumentService,StockLookups>(test,x=>x.LookupsAsync(s.F.Owner,s.F.Branch))).Accounts!;
        await Change<IStockDocumentService>(test,x=>x.ConfigureAsync(s.F.Owner,s.F.Branch,settings with{SalesReturnsAccountId=null}));
        await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,10,20)));
        var sale=await Trade(test,s,Draft(s,StockDocumentKind.Sale,s.Cash,new StockLine(s.Product,s.Unit,3,30)));
        var draft=await With<IStockDocumentService,StockDocument>(test,x=>x.SaveAsync(s.F.Owner,Draft(s,StockDocumentKind.SalesReturn,s.Cash,new StockLine(s.Product,s.Unit,1,OriginalMovementId:sale.Movements[0].Id))));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>With<IStockDocumentService,StockDocument>(test,x=>x.PostAsync(s.F.Owner,draft.Id,draft.Version)));
        await MatchStockLedger(test,s,Date,7,140);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Change<IStockDocumentService>(test,x=>x.ConfigureAsync(s.F.Owner,s.F.Branch,settings with{SalesReturnsAccountId=s.F.Credit})));
        await Change<IStockDocumentService>(test,x=>x.ConfigureAsync(s.F.Owner,s.F.Branch,settings));
        await With<IStockDocumentService,StockDocument>(test,x=>x.PostAsync(s.F.Owner,draft.Id,draft.Version));
        var balances=await With<IJournalService,IReadOnlyList<LedgerBalance>>(test,x=>x.BalancesAsync(s.F.Owner,s.F.Branch,s.F.Currency,Date));
        Assert.Equal(90,Assert.Single(balances,x=>x.AccountId==settings.SalesAccountId).Credit);
        Assert.Equal(30,Assert.Single(balances,x=>x.AccountId==settings.SalesReturnsAccountId).Debit);
        await MatchStockLedger(test,s,Date,8,160);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Change<IStockDocumentService>(test,x=>x.ConfigureAsync(s.F.Owner,s.F.Branch,settings with{SalesReturnsAccountId=null})));
    }
    [SqlServerFact]
    public async Task Trade_history_keeps_dates_prices_tax_original_links_and_reversed_documents_with_filters()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test);
        var first=await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,10,20)));
        var later=await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,10,30)) with{Date=Date.AddDays(1)});
        var sale=await Trade(test,s,Draft(s,StockDocumentKind.Sale,s.Cash,new StockLine(s.Product,s.Unit,2,50,10)) with{Date=Date.AddDays(2),TaxEnabled=true,TaxRate=.1m});
        var ret=await Trade(test,s,Draft(s,StockDocumentKind.SalesReturn,s.Cash,new StockLine(s.Product,s.Unit,1,OriginalMovementId:sale.Movements[0].Id)) with{Date=Date.AddDays(3)});
        await With<IStockDocumentService,StockDocument>(test,x=>x.ReverseAsync(s.F.Owner,ret.Id,Date.AddDays(4),Guid.NewGuid()));
        var rows=(await History(test,s,new(Date,Date.AddDays(4),s.Product,s.Warehouse))).Rows;
        Assert.Equal(new[]{ret.Id,sale.Id,later.Id,first.Id},rows.Select(x=>x.DocumentId));
        var r=rows[0];Assert.Equal(3,r.State);Assert.Equal(Date.AddDays(4),r.ReversalDate);
        Assert.Equal(sale.Id,r.OriginalDocumentId);Assert.Equal(Date.AddDays(2),r.OriginalDate);Assert.Equal(50,r.OriginalUnitPrice);Assert.Equal(10,r.OriginalDiscount);
        Assert.Equal(45,r.Net);Assert.Equal(4.5m,r.Tax);Assert.Equal(25,r.StockValue);
        Assert.Equal(10,rows[1].Discount);Assert.Equal(90,rows[1].Net);Assert.Equal(9,rows[1].Tax);
        Assert.Equal(new[]{30m,20m},rows.Skip(2).Select(x=>x.UnitPrice));
        Assert.Single((await History(test,s,new(Date.AddDays(1),Date.AddDays(1)))).Rows);
        Assert.Empty((await History(test,s,new(Date,Date.AddDays(4),WarehouseId:s.Target))).Rows);
        var other=await With<IBranchSetupService,int>(test,x=>x.CreateAsync(s.F.Owner,"Other"));
        Assert.Empty((await With<IStockDocumentService,StockHistoryPage>(test,x=>x.HistoryAsync(s.F.Owner,other,new(Date,Date.AddDays(4))))).Rows);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>With<IStockDocumentService,StockHistoryPage>(test,x=>x.HistoryAsync(int.MaxValue,s.F.Branch,new(Date,Date))));
    }
    [SqlServerFact]
    public async Task History_pages_lines_without_loss_or_double_counting_and_excludes_drafts()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test);
        await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,Enumerable.Range(1,51).Select(i=>new StockLine(s.Product,s.Unit,1,i)).ToArray()));
        await With<IStockDocumentService,StockDocument>(test,x=>x.SaveAsync(s.F.Owner,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,1,999))));
        var one=await History(test,s,new(Date,Date));var two=await History(test,s,new(Date,Date,Page:1));
        Assert.True(one.HasMore);Assert.False(two.HasMore);Assert.Equal(50,one.Rows.Count);Assert.Single(two.Rows);
        Assert.Equal(Enumerable.Range(1,51),one.Rows.Concat(two.Rows).Select(x=>x.LineNumber));
        Assert.Equal(1326,one.Rows.Concat(two.Rows).Sum(x=>x.Net));
    }
}
