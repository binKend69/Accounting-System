using HisabTak.Domain.Inventory;

namespace HisabTak.Application.Inventory;

public enum StockDocumentKind { Opening = 1, Purchase = 2, Sale = 3, PurchaseReturn = 4, SalesReturn = 5, Transfer = 6, Increase = 7, Decrease = 8 }
public sealed record ProductDefinition(string Code, string Name, int BaseUnitId, CostMethod CostMethod, bool TrackBatches);
public sealed record ProductInfo(int Id, string Code, string Name, int BaseUnitId, CostMethod CostMethod, bool TrackBatches);
public sealed record ProductUnitInfo(int ProductId, int UnitId, decimal Factor);
public sealed record BatchInfo(long Id, int ProductId, int WarehouseId, string Code, DateOnly Expiry);
public sealed record StockLine(int ProductId, int UnitId, decimal Quantity, decimal UnitPrice = 0m,
    decimal Discount = 0m, long? BatchId = null, string? BatchOverrideReason = null, long? OriginalMovementId = null);
public sealed record StockDocumentDraft(Guid SourceKey, int BranchId, StockDocumentKind Kind, DateOnly Date,
    int WarehouseId, int? TargetWarehouseId, int CurrencyId, decimal RateToBase, int CounterpartyAccountId,
    bool Credit, IReadOnlyList<StockLine> Lines, bool TaxEnabled = false, decimal TaxRate = 0m, bool PricesIncludeTax = false);
public sealed record InventoryAccounts(int StockAccountId, int SalesAccountId, int CostAccountId,
    int AdjustmentAccountId, int InputTaxAccountId, int OutputTaxAccountId, int? FxGainAccountId = null, int? FxLossAccountId = null, LastPurchaseBookPolicy BookPolicy = LastPurchaseBookPolicy.Unspecified);
public sealed record StockMovement(long Id, long DocumentId, int LineNumber, int ProductId, int WarehouseId,
    long? BatchId, decimal Quantity, decimal Value, decimal Net, decimal Tax, long? OriginalMovementId);
public sealed record StockDocument(long Id, StockDocumentDraft Draft, int Version, int State, long? Number,
    long? JournalId, long? ReversalId, IReadOnlyList<StockMovement> Movements);
public sealed record StockBalance(int ProductId, int WarehouseId, decimal Quantity, decimal BookValue, decimal LastPurchasePrice);
public sealed record DebtBalance(long DocumentId, int AccountId, int CurrencyId, bool Receivable,
    decimal OriginalAmount, decimal RemainingAmount, decimal OriginalBookValue, decimal RemainingBookValue);
public sealed record DebtPayment(Guid SourceKey, long InvoiceId, DateOnly Date, decimal Amount, int CashAccountId, decimal? CashAmount = null, decimal? CashRateToBase = null);

public sealed record StockLookups(IReadOnlyList<InventoryReference> Warehouses, IReadOnlyList<InventoryReference> Units, InventoryAccounts? Accounts);

public interface IStockDocumentService
{
    Task<StockLookups> LookupsAsync(int actor, int branchId, CancellationToken ct = default);
    Task<int> CreateProductAsync(int actor, ProductDefinition product, CancellationToken ct = default);
    Task SetCurrencyPrecisionAsync(int actor, int currencyId, int precision, CancellationToken ct = default);
    Task SetBarcodeAsync(int actor,int productId,int unitId,string barcode,CancellationToken ct=default);
    Task<ProductUnitInfo?> ScanAsync(int actor,string barcode,CancellationToken ct=default);
    Task SetPriceAsync(int actor,int productId,int unitId,int currencyId,string tier,decimal price,CancellationToken ct=default);
    Task<decimal?> PriceAsync(int actor,int productId,int unitId,int currencyId,string tier,CancellationToken ct=default);
    Task SetCreditLimitAsync(int actor, int branchId, int accountId, decimal limit, CancellationToken ct = default);
    Task ChangeCostMethodAsync(int actor, int productId, CostMethod method, CancellationToken ct = default);
    Task AddUnitAsync(int actor, int productId, int unitId, decimal factor, CancellationToken ct = default);
    Task<long> CreateBatchAsync(int actor, int branchId, int productId, int warehouseId, string code, DateOnly expiry, CancellationToken ct = default);
    Task ConfigureAsync(int actor, int branchId, InventoryAccounts accounts, CancellationToken ct = default);
    Task<IReadOnlyList<ProductInfo>> ProductsAsync(int actor, CancellationToken ct = default);
    Task<IReadOnlyList<ProductUnitInfo>> UnitsAsync(int actor, int productId, CancellationToken ct = default);
    Task<IReadOnlyList<BatchInfo>> BatchesAsync(int actor, int branchId, int warehouseId, CancellationToken ct = default);
    Task<StockDocument> SaveAsync(int actor, StockDocumentDraft draft, CancellationToken ct = default);
    Task<StockDocument> UpdateAsync(int actor, long id, int version, StockDocumentDraft draft, CancellationToken ct = default);
    Task<StockDocument> PostAsync(int actor, long id, int version, CancellationToken ct = default);
    Task<StockDocument> ReverseAsync(int actor, long id, DateOnly date, Guid sourceKey, CancellationToken ct = default);
    Task<StockDocument> GetAsync(int actor, long id, CancellationToken ct = default);
    Task<IReadOnlyList<StockDocument>> ListAsync(int actor, int branchId, int page = 0, CancellationToken ct = default);
    Task<IReadOnlyList<StockBalance>> BalancesAsync(int actor, int branchId, DateOnly asOf, CancellationToken ct = default);
    Task<IReadOnlyList<DebtBalance>> DebtsAsync(int actor, int branchId, CancellationToken ct = default);
    Task<long> SettleAsync(int actor, DebtPayment payment, CancellationToken ct = default);
}
