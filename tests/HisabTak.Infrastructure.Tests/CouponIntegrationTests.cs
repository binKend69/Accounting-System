using HisabTak.Application.Accounting;
using HisabTak.Application.Inventory;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Tests;
public sealed partial class LedgerIntegrationTests
{
    private static CouponDefinition Coupon(Store s,string code="TEST",int uses=1)=>
        new(code,s.F.Currency,false,10,0,new(2020,1,1),new(2099,12,31),uses);
    private static Task<long> CreateCoupon(SqlTestDatabase test,Store s,CouponDefinition d)=>
        With<ICouponService,long>(test,c=>c.CreateCouponAsync(s.F.Owner,s.F.Branch,d));
    private static Task<StockDocumentDraft> ApplyCoupon(SqlTestDatabase test,Store s,StockDocumentDraft d,string code="TEST")=>
        With<ICouponService,StockDocumentDraft>(test,c=>c.ApplyCouponAsync(s.F.Owner,d,code));
    private static async Task<int> CouponUses(SqlTestDatabase test,Store s)=>
        (await With<ICouponService,IReadOnlyList<CouponInfo>>(test,c=>c.CouponsAsync(s.F.Owner,s.F.Branch))).Sum(c=>c.Used);

    [SqlServerFact]
    public async Task Coupon_discount_posts_once_under_concurrent_pos_payments_and_returns_net_value()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test);
        await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,10,20)));
        await CreateCoupon(test,s,Coupon(s));
        var draft=await ApplyCoupon(test,s,Draft(s,StockDocumentKind.Sale,s.Cash,new StockLine(s.Product,s.Unit,2,30)));
        Assert.Equal(10,Assert.Single(draft.Lines).Discount);
        await With<IPosSlots,PosSlot>(test,p=>p.SaveSlotAsync(s.F.Owner,s.F.Branch,1,0,draft));
        Assert.Equal(0,await CouponUses(test,s));
        Task<StockDocument> Pay()=>With<IPosSlots,StockDocument>(test,p=>p.PayAsync(s.F.Owner,s.F.Branch,1,1));
        var paid=await Task.WhenAll(Pay(),Pay());Assert.Equal(paid[0].Id,paid[1].Id);
        Assert.Equal(1,await CouponUses(test,s));Assert.Equal(50,paid[0].Movements.Sum(m=>m.Net));
        var returned=await Trade(test,s,Draft(s,StockDocumentKind.SalesReturn,s.Cash,new StockLine(s.Product,s.Unit,1,OriginalMovementId:paid[0].Movements[0].Id)));
        Assert.Equal(25,returned.Movements.Sum(m=>m.Net));Assert.Equal(20,returned.Movements.Sum(m=>m.Value));
        await With<IStockDocumentService,StockDocument>(test,p=>p.ReverseAsync(s.F.Owner,returned.Id,Date,Guid.NewGuid()));
        await With<IStockDocumentService,StockDocument>(test,p=>p.ReverseAsync(s.F.Owner,paid[0].Id,Date,Guid.NewGuid()));
        Assert.Equal(1,await CouponUses(test,s));
    }

    [SqlServerFact]
    public async Task Coupon_limit_allows_one_of_two_different_concurrent_sales_and_rolls_back_loser()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test);
        await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,10,20)));
        await CreateCoupon(test,s,Coupon(s));
        var draft=await ApplyCoupon(test,s,Draft(s,StockDocumentKind.Sale,s.Cash,new StockLine(s.Product,s.Unit,1,30)));
        var first=await With<IStockDocumentService,StockDocument>(test,p=>p.SaveAsync(s.F.Owner,draft));
        var second=await With<IStockDocumentService,StockDocument>(test,p=>p.SaveAsync(s.F.Owner,draft with {SourceKey=Guid.NewGuid()}));
        async Task<bool> PostCoupon(StockDocument d)
        {
            try{await With<IStockDocumentService,StockDocument>(test,p=>p.PostAsync(s.F.Owner,d.Id,d.Version));return true;}
            catch(CouponException){return false;}
        }
        var results=await Task.WhenAll(PostCoupon(first),PostCoupon(second));Assert.Single(results.Where(x=>x));
        Assert.Equal(1,await CouponUses(test,s));Assert.Equal(9,Assert.Single(await Stock(test,s,Date)).Quantity);
        Assert.Equal(2,await Count(test,"JournalEntries"));
    }

    [SqlServerFact]
    public async Task Coupon_rejects_stacking_tampering_expiry_currency_customer_and_disabled_drafts()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test);
        var definition=Coupon(s) with {Percentage=true,Value=10,MinimumAmount=50,CustomerAccountId=s.Customer};
        var id=await CreateCoupon(test,s,definition);
        var sale=Draft(s,StockDocumentKind.Sale,s.Customer,new StockLine(s.Product,s.Unit,2,30));
        await Assert.ThrowsAsync<CouponException>(()=>ApplyCoupon(test,s,sale with {Lines=[sale.Lines[0] with {Discount=1}]}));
        await Assert.ThrowsAsync<CouponException>(()=>ApplyCoupon(test,s,sale with {CurrencyId=s.F.OtherCurrency}));
        await Assert.ThrowsAsync<CouponException>(()=>ApplyCoupon(test,s,sale with {CounterpartyAccountId=s.Cash}));
        await Assert.ThrowsAsync<CouponException>(()=>ApplyCoupon(test,s,sale with {Date=new(2100,1,1)}));
        await Assert.ThrowsAsync<CouponException>(()=>ApplyCoupon(test,s,sale with {Lines=[sale.Lines[0] with {Quantity=1}]}));
        var applied=await ApplyCoupon(test,s,sale);Assert.Equal(6,applied.Lines[0].Discount);
        await Assert.ThrowsAsync<CouponException>(()=>With<IStockDocumentService,StockDocument>(test,p=>p.SaveAsync(s.F.Owner,applied with {Lines=[applied.Lines[0] with {Discount=7}]})));
        var saved=await With<IStockDocumentService,StockDocument>(test,p=>p.SaveAsync(s.F.Owner,applied));
        await Change<ICouponService>(test,c=>c.DisableCouponAsync(s.F.Owner,s.F.Branch,id));
        await Assert.ThrowsAsync<CouponException>(()=>With<IStockDocumentService,StockDocument>(test,p=>p.PostAsync(s.F.Owner,saved.Id,saved.Version)));
        Assert.Equal(0,await Count(test,"JournalEntries"));Assert.Equal(0,await CouponUses(test,s));
    }

    [SqlServerFact]
    public async Task Coupon_usage_rolls_back_with_failed_post_and_inclusive_tax_uses_discounted_price()
    {
        await using var test=await SqlTestDatabase.CreateAsync();var s=await StoreAsync(test);
        await Trade(test,s,Draft(s,StockDocumentKind.Purchase,s.Cash,new StockLine(s.Product,s.Unit,10,20)));
        await CreateCoupon(test,s,Coupon(s) with {Value=11});
        var draft=await ApplyCoupon(test,s,Draft(s,StockDocumentKind.Sale,s.Cash,new StockLine(s.Product,s.Unit,1,110)) with {TaxEnabled=true,TaxRate=.1m,PricesIncludeTax=true});
        var saved=await With<IStockDocumentService,StockDocument>(test,p=>p.SaveAsync(s.F.Owner,draft));
        await using(var db=test.Context())await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER dbo.TR_TestCouponAudit ON dbo.AuditLogs AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE OperationType='STOCK_DOCUMENT_POSTED') THROW 51222,'Test failure',1; END");
        await Assert.ThrowsAnyAsync<Exception>(()=>With<IStockDocumentService,StockDocument>(test,p=>p.PostAsync(s.F.Owner,saved.Id,saved.Version)));
        Assert.Equal(0,await CouponUses(test,s));Assert.Equal(10,Assert.Single(await Stock(test,s,Date)).Quantity);
        await using(var db=test.Context())await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TR_TestCouponAudit");
        var posted=await With<IStockDocumentService,StockDocument>(test,p=>p.PostAsync(s.F.Owner,saved.Id,saved.Version));
        Assert.Equal(90,posted.Movements.Sum(m=>m.Net));Assert.Equal(9,posted.Movements.Sum(m=>m.Tax));
        Assert.Equal(1,await CouponUses(test,s));
    }
}
