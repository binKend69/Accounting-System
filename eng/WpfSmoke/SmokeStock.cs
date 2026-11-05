using HisabTak.Application.Inventory;
using HisabTak.Domain.Inventory;

internal sealed class SmokeStock : IStockDocumentService
{
    public StockDocumentDraft? Saved { get; private set; }
    public Task<StockLookups> LookupsAsync(int actor, int branchId, CancellationToken ct = default) => Task.FromResult(new StockLookups([new(1,"المستودع",1)],[new(1,"حبة",null)],null));
    public Task<int> CreateProductAsync(int actor, ProductDefinition product, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetCurrencyPrecisionAsync(int actor,int currencyId,int precision,CancellationToken ct=default) => throw new NotSupportedException();
    public Task ChangeCostMethodAsync(int actor,int productId,CostMethod method,CancellationToken ct=default) => throw new NotSupportedException();
    public Task AddUnitAsync(int actor, int productId, int unitId, decimal factor, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<long> CreateBatchAsync(int actor, int branchId, int productId, int warehouseId, string code, DateOnly expiry, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ConfigureAsync(int actor, int branchId, InventoryAccounts accounts, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ProductInfo>> ProductsAsync(int actor, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProductInfo>>([new(1,"001","صنف اختبار",1,CostMethod.WeightedAverage,false)]);
    public Task<IReadOnlyList<ProductUnitInfo>> UnitsAsync(int actor, int productId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProductUnitInfo>>([new(1,1,1)]);
    public Task<IReadOnlyList<BatchInfo>> BatchesAsync(int actor, int branchId, int warehouseId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BatchInfo>>([]);
    public Task<StockDocument> SaveAsync(int actor, StockDocumentDraft draft, CancellationToken ct = default) => Task.FromResult(new StockDocument(1,Saved=draft,1,1,null,null,null,[]));
    public Task<StockDocument> UpdateAsync(int actor, long id, int version, StockDocumentDraft draft, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<StockDocument> PostAsync(int actor, long id, int version, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<StockDocument> ReverseAsync(int actor, long id, DateOnly date, Guid sourceKey, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<StockDocument> GetAsync(int actor, long id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<StockDocument>> ListAsync(int actor, int branchId, int page = 0, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockDocument>>([]);
    public Task<IReadOnlyList<StockBalance>> BalancesAsync(int actor, int branchId, DateOnly asOf, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StockBalance>>([]);
    public Task<IReadOnlyList<DebtBalance>> DebtsAsync(int actor, int branchId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DebtBalance>>([]);
    public Task<long> SettleAsync(int actor, DebtPayment payment, CancellationToken ct = default) => throw new NotSupportedException();
}
