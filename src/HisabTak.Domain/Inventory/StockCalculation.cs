namespace HisabTak.Domain.Inventory;

public enum CostMethod { WeightedAverage = 1, LastPurchase = 2 }
public sealed record StockPosition(decimal Quantity, decimal BookValue, decimal LastPurchasePrice);
public sealed record StockChange(StockPosition Position, decimal Value, decimal Revaluation);

public static class StockCalculation
{
    public static decimal Quantity(decimal quantity, decimal factor)
    {
        if (quantity <= 0 || factor <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        var value = checked(quantity * factor);
        Exact(value, 8); return value;
    }
    public static void Exact(decimal value, int scale)
    {
        if (decimal.Abs(value) >= 100000000000000000000m || decimal.Round(value, scale) != value)
            throw new ArgumentOutOfRangeException(nameof(value), "Value exceeds storage precision.");
    }
    public static decimal Round(decimal value, int precision) => decimal.Round(value, precision, MidpointRounding.AwayFromZero);
    public static StockChange Receive(StockPosition before, decimal quantity, decimal value, CostMethod method, int precision, bool purchase)
    {
        Validate(before, quantity, precision);
        if (value < 0 || !Enum.IsDefined(method)) throw new ArgumentOutOfRangeException(nameof(value));
        Exact(value, precision);
        var price = purchase ? Round(value / quantity, 8) : before.LastPurchasePrice;
        // Last-purchase valuation changes the remaining stock at receipt time, never past issues.
        var revaluation = purchase && method == CostMethod.LastPurchase ? Round(before.Quantity * price, precision) - before.BookValue : 0m;
        var after = new StockPosition(checked(before.Quantity + quantity), checked(before.BookValue + value + revaluation), price);
        Exact(after.Quantity, 8); Exact(after.BookValue, precision);
        return new(after, value, revaluation);
    }
    public static StockChange Issue(StockPosition before, decimal quantity, int precision, decimal? historicalValue = null, CostMethod method = CostMethod.WeightedAverage)
    {
        Validate(before, quantity, precision);
        if (quantity > before.Quantity) throw new InvalidOperationException("Insufficient stock.");
        var carryingShare = quantity == before.Quantity ? before.BookValue : Round(before.BookValue * quantity / before.Quantity, precision);
        var value = historicalValue ?? (method == CostMethod.LastPurchase && before.LastPurchasePrice > 0
            ? Round(before.LastPurchasePrice * quantity, precision) : carryingShare);
        if (value < 0) throw new InvalidOperationException("Invalid historical issue value.");
        Exact(value, precision);
        // A historical return can exhaust units at a different price than the remaining book value.
        // Expose the difference as its own valuation entry instead of leaving value behind at zero quantity.
        var carryingIssue = quantity == before.Quantity || value > before.BookValue ? carryingShare : value;
        var adjustment = value - carryingIssue;
        return new(new(before.Quantity - quantity, before.BookValue - carryingIssue, before.LastPurchasePrice), value, adjustment);
    }
    public static decimal Allocate(decimal originalValue, decimal originalQuantity, decimal returnedQuantity, decimal alreadyQuantity, decimal alreadyValue, int precision)
    {
        if (originalQuantity <= 0 || returnedQuantity <= 0 || alreadyQuantity < 0 || alreadyValue < 0 || alreadyValue > originalValue || returnedQuantity + alreadyQuantity > originalQuantity)
            throw new InvalidOperationException("Return exceeds original quantity or value.");
        return returnedQuantity + alreadyQuantity == originalQuantity ? originalValue - alreadyValue
            : Math.Min(originalValue - alreadyValue, Round(originalValue * returnedQuantity / originalQuantity, precision));
    }
    private static void Validate(StockPosition before, decimal quantity, int precision)
    {
        if (quantity <= 0 || before.Quantity < 0 || before.BookValue < 0 || before.LastPurchasePrice < 0 || precision is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        Exact(quantity, 8);
    }
}
