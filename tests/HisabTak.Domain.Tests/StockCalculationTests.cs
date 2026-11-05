using HisabTak.Domain.Inventory;

namespace HisabTak.Domain.Tests;

public sealed class StockCalculationTests
{
    [Fact]
    public void Historical_return_clears_remaining_units_and_exposes_the_book_difference()
    {
        var result=StockCalculation.Issue(new(1,120,125),1,2,100);
        Assert.Equal(100,result.Value);
        Assert.Equal(-20,result.Revaluation);
        Assert.Equal(0,result.Position.Quantity);
        Assert.Equal(0,result.Position.BookValue);
    }
    [Fact]
    public void Last_purchase_price_does_not_choose_a_book_revaluation_policy()
    {
        var before=new StockPosition(10,200,20);
        Assert.Throws<InvalidOperationException>(()=>StockCalculation.Receive(before,10,300,CostMethod.LastPurchase,2,true));
        var carry=StockCalculation.Receive(before,10,300,CostMethod.LastPurchase,2,true,LastPurchaseBookPolicy.KeepAcquisitionValue);
        var revalue=StockCalculation.Receive(before,10,300,CostMethod.LastPurchase,2,true,LastPurchaseBookPolicy.RevalueAtReceipt);
        Assert.Equal(30,carry.Position.LastPurchasePrice);Assert.Equal(500,carry.Position.BookValue);Assert.Equal(0,carry.Revaluation);
        Assert.Equal(30,revalue.Position.LastPurchasePrice);Assert.Equal(600,revalue.Position.BookValue);Assert.Equal(100,revalue.Revaluation);
    }
    [Fact]
    public void Unit_conversion_rejects_silent_quantity_rounding()
    {
        Assert.Equal(25,StockCalculation.Quantity(2.5m,10));
        Assert.Throws<ArgumentOutOfRangeException>(()=>StockCalculation.Quantity(0.00000001m,0.5m));
    }
    [Fact]
    public void Last_purchase_and_average_price_have_distinct_issue_values()
    {
        var mixed=new StockPosition(10,1000,120);
        Assert.Equal(200,StockCalculation.Issue(mixed,2,2).Value);
        Assert.Equal(240,StockCalculation.Issue(mixed,2,2,method:CostMethod.LastPurchase).Value);
    }
}
