using System.Text.Json;
using HisabTak.Application.Accounting;
using HisabTak.Application.Inventory;
using HisabTak.Application.Security;
using HisabTak.Domain.Inventory;
using HisabTak.Domain.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed partial class StockDocumentService(HisabTakDbContext db, IAuthorizationService authorization, JournalService journals, IJournalDocumentService vouchers) : IStockDocumentService
{
    private readonly SqlLedger sql = new(db);
    private async Task<T> Write<T>(int actor, string permission, CancellationToken ct, Func<Task<T>> action)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await DatabaseLock.AcquireAsync(db, "HisabTak.Accounting.Periods", ct);
        await authorization.DemandAsync(actor, permission, ct);
        var result = await action();
        await tx.CommitAsync(ct);
        return result;
    }
    private async Task<List<T>> Rows<T>(string query, Func<SqlDataReader,T> map, CancellationToken ct, params (string,object?)[] args)
    {
        await using var command = await sql.CommandAsync(query, ct, args);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }
    private Task Branch(int actor, int branch, CancellationToken ct) => AccountingAccess.BranchAsync(db, authorization, actor, branch, ct);
    private async Task Warehouse(int branch, int id, CancellationToken ct)
    {
        if (!await db.Warehouses.AnyAsync(x => x.Id == id && x.BranchId == branch && x.IsActive, ct))
            throw new InvalidOperationException("Warehouse is unavailable in this branch.");
    }
    private async Task Audit(int actor, string action, string entity, long id, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog(actor, action, entity, id, Environment.MachineName));
        await db.SaveChangesAsync(ct);
    }
    private static string Name(string name, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (name.Length > limit) throw new ArgumentOutOfRangeException(nameof(name));
        return name;
    }
    public Task<int> CreateProductAsync(int actor, ProductDefinition product, CancellationToken ct = default) =>
        Write(actor, PermissionCodes.SetupManage, ct, async () =>
        {
            if (!Enum.IsDefined(product.CostMethod) || !await db.Units.AnyAsync(x => x.Id == product.BaseUnitId && x.IsActive, ct))
                throw new ArgumentException("Invalid base unit or cost method.");
            var code=product.Code;
            if(string.IsNullOrWhiteSpace(code))
            {
                do {code="IT-"+(await sql.ScalarAsync("SELECT NEXT VALUE FOR dbo.StockItemCodes",ct)).ToString("D8",System.Globalization.CultureInfo.InvariantCulture);}
                while(await sql.ScalarAsync("SELECT COUNT(*) FROM dbo.StockProducts WHERE Code=@c",ct,("@c",code))!=0);
            }
            var id = checked((int)await sql.ScalarAsync("""
                INSERT dbo.StockProducts(Code,Name,BaseUnitId,CostMethod,TrackBatches) VALUES(@code,@name,@unit,@method,@track);
                SELECT CAST(SCOPE_IDENTITY() AS int);
                """, ct, ("@code",Name(code,40)),("@name",Name(product.Name,200)),("@unit",product.BaseUnitId),("@method",(int)product.CostMethod),("@track",product.TrackBatches)));
            await sql.ExecuteAsync("INSERT dbo.StockProductUnits VALUES(@id,@unit,1)",ct,("@id",id),("@unit",product.BaseUnitId));
            if(!product.DecreaseQtyOnSale)
            {
                if(product.TrackBatches)throw new ArgumentException("Services cannot track batches.");
                await sql.ExecuteAsync("INSERT dbo.StockServiceItems VALUES(@i)",ct,("@i",id));
            }
            await Audit(actor,"PRODUCT_CREATED","StockProducts",id,ct);
            return id;
        });
    private async Task<int> CurrencyPrecision(int currencyId,int baseId,int basePrecision,CancellationToken ct)
    {
        var code=await db.Currencies.Where(x=>x.Id==currencyId && x.IsActive).Select(x=>x.Code).SingleAsync(ct);
        if(currencyId==baseId)return basePrecision;
        if(code is "YER" or "SAR" or "USD")return 2;
        var key="Stock.CurrencyPrecision."+currencyId;
        var value=await db.CustomSettings.Where(x=>x.Key==key).Select(x=>x.Value).SingleOrDefaultAsync(ct);
        return int.TryParse(value,out var precision) && precision is >=0 and <=8?precision:throw new InvalidOperationException("Configure currency precision before use.");
    }
    public Task SetCurrencyPrecisionAsync(int actor,int currencyId,int precision,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.SetupManage,ct,async () =>
        {
            if(precision is <0 or >8)throw new ArgumentOutOfRangeException(nameof(precision));
            var currency=await db.Currencies.SingleAsync(x=>x.Id==currencyId && x.IsActive,ct);
            if(currency.IsBaseCurrency || currency.Code is "YER" or "SAR" or "USD")throw new InvalidOperationException("This currency already has a defined precision.");
            var key="Stock.CurrencyPrecision."+currencyId;
            var existing=await db.CustomSettings.SingleOrDefaultAsync(x=>x.Key==key,ct);
            if(existing!=null && existing.Value!=precision.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Configured currency precision cannot be overwritten.");
            if(existing==null)db.CustomSettings.Add(new Domain.Setup.CustomSetting(key,precision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await Audit(actor,"STOCK_CURRENCY_PRECISION","Currencies",currencyId,ct);
            return currencyId;
        });
    public Task SetCreditLimitAsync(int actor,int branchId,int accountId,decimal limit,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.AccountManage,ct,async () =>
        {
            await Branch(actor,branchId,ct);
            if(limit<0 || limit>=10000000000000000m || decimal.Round(limit,2)!=limit)throw new ArgumentOutOfRangeException(nameof(limit));
            var account=await db.Accounts.SingleAsync(x=>x.Id==accountId,ct);
            await DemandClass(accountId,branchId,account.CurrencyId,["CUSTOMER"],ct);
            await sql.ExecuteAsync("UPDATE dbo.Accounts SET CreditLimit=@limit,ModifiedAt=SYSUTCDATETIME() WHERE AccountId=@a",ct,("@a",accountId),("@limit",limit));
            await Audit(actor,"CUSTOMER_LIMIT_SET","Accounts",accountId,ct);
            return accountId;
        });
    public Task ChangeCostMethodAsync(int actor,int productId,CostMethod method,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.SetupManage,ct,async () =>
        {
            if(!Enum.IsDefined(method)) throw new ArgumentOutOfRangeException(nameof(method));
            if(await sql.ScalarAsync("SELECT COUNT(*) FROM dbo.StockProducts WHERE ProductId=@p",ct,("@p",productId))!=1 ||
                await sql.ScalarAsync("SELECT COUNT(*) FROM dbo.StockMovements WHERE ProductId=@p",ct,("@p",productId))!=0)
                throw new InvalidOperationException("Cost method must be set before the first stock movement.");
            await sql.ExecuteAsync("UPDATE dbo.StockProducts SET CostMethod=@m WHERE ProductId=@p",ct,("@p",productId),("@m",(int)method));
            await Audit(actor,"PRODUCT_COST_METHOD_SET","StockProducts",productId,ct);
            return productId;
        });
    public Task AddUnitAsync(int actor, int productId, int unitId, decimal factor, CancellationToken ct = default) =>
        Write(actor, PermissionCodes.SetupManage, ct, async () =>
        {
            StockCalculation.Quantity(1,factor);
            if (!await db.Units.AnyAsync(x => x.Id == unitId && x.IsActive, ct)) throw new InvalidOperationException("Unit unavailable.");
            await sql.ExecuteAsync("INSERT dbo.StockProductUnits VALUES(@p,@u,@f)",ct,("@p",productId),("@u",unitId),("@f",factor));
            await Audit(actor,"PRODUCT_UNIT_ADDED","StockProducts",productId,ct);
            return productId;
        });
    public Task<long> CreateBatchAsync(int actor,int branchId,int productId,int warehouseId,string code,DateOnly expiry,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.SetupManage,ct,async () =>
        {
            await Branch(actor,branchId,ct);
            await Warehouse(branchId,warehouseId,ct);
            if (await sql.ScalarAsync("SELECT COUNT(*) FROM dbo.StockProducts WHERE ProductId=@p AND TrackBatches=1",ct,("@p",productId)) != 1)
                throw new InvalidOperationException("Product does not track batches.");
            var id = await sql.ScalarAsync("INSERT dbo.StockBatches VALUES(@p,@w,@c,@e); SELECT CAST(SCOPE_IDENTITY() AS bigint)",ct,("@p",productId),("@w",warehouseId),("@c",Name(code,80)),("@e",expiry));
            await Audit(actor,"BATCH_CREATED","StockBatches",id,ct);
            return id;
        });
    public Task ConfigureAsync(int actor,int branchId,InventoryAccounts accounts,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.SetupManage,ct,async () =>
        {
            await Branch(actor,branchId,ct);
            if(!Enum.IsDefined(accounts.BookPolicy))throw new ArgumentException("Invalid valuation policy.");
            if(accounts.BookPolicy==LastPurchaseBookPolicy.RevalueAtReceipt)
            {
                var existing=(await Rows("SELECT Payload FROM dbo.StockAccountSettings WHERE BranchId=@b",r=>JsonSerializer.Deserialize<InventoryAccounts>(r.GetString(0))!,ct,("@b",branchId))).SingleOrDefault();
                if(existing?.BookPolicy!=LastPurchaseBookPolicy.RevalueAtReceipt)
                    throw new InvalidOperationException("New inventory settings must preserve acquisition value.");
            }
            int[] ids = [accounts.StockAccountId,accounts.SalesAccountId,accounts.CostAccountId,accounts.AdjustmentAccountId,accounts.InputTaxAccountId,accounts.OutputTaxAccountId];
            if(accounts.FxGainAccountId.HasValue!=accounts.FxLossAccountId.HasValue) throw new ArgumentException("Configure both FX accounts.");
            if(accounts.FxGainAccountId is int gain && accounts.FxLossAccountId is int loss) ids=[..ids,gain,loss];
            var currency = await db.Companies.Where(x=>x.IsActive).Select(x=>x.BaseCurrencyId).SingleAsync(ct);
            if (ids.Distinct().Count()!=ids.Length || currency is null || await db.Accounts.CountAsync(x=>ids.Contains(x.Id) && x.BranchId==branchId && x.CurrencyId==currency && x.IsActive && !x.IsSuspended,ct)!=ids.Length)
                throw new InvalidOperationException("Choose six distinct active branch accounts in base currency.");
            if(await sql.ScalarAsync("SELECT COUNT(*) FROM dbo.StockDocuments WHERE BranchId=@b AND State>=2",ct,("@b",branchId))>0)
            {
                var previous=(await Rows("SELECT Payload FROM dbo.StockAccountSettings WHERE BranchId=@b",r=>JsonSerializer.Deserialize<InventoryAccounts>(r.GetString(0))!,ct,("@b",branchId))).Single();
                if((previous with { FxGainAccountId=null,FxLossAccountId=null,BookPolicy=LastPurchaseBookPolicy.Unspecified })!=(accounts with { FxGainAccountId=null,FxLossAccountId=null,BookPolicy=LastPurchaseBookPolicy.Unspecified }) ||
                    previous.FxGainAccountId!=null && (previous.FxGainAccountId!=accounts.FxGainAccountId || previous.FxLossAccountId!=accounts.FxLossAccountId) ||
                    previous.BookPolicy!=LastPurchaseBookPolicy.Unspecified && previous.BookPolicy!=accounts.BookPolicy)
                    throw new InvalidOperationException("Stock account mappings are fixed after first posting.");
            }
            await sql.ExecuteAsync("UPDATE dbo.StockAccountSettings SET Payload=@a WHERE BranchId=@b; IF @@ROWCOUNT=0 INSERT dbo.StockAccountSettings VALUES(@b,@a)",ct,("@b",branchId),("@a",JsonSerializer.Serialize(accounts)));
            await Audit(actor,"STOCK_ACCOUNTS_CONFIGURED","Branches",branchId,ct);
            return branchId;
        });
    public async Task<StockLookups> LookupsAsync(int actor,int branchId,CancellationToken ct=default)
    {
        await authorization.DemandAsync(actor,PermissionCodes.JournalRead,ct);
        await Branch(actor,branchId,ct);
        var warehouses=await db.Warehouses.Where(x=>x.IsActive && x.BranchId==branchId).OrderBy(x=>x.NameAr)
            .Select(x=>new InventoryReference(x.Id,x.NameAr,x.BranchId)).ToListAsync(ct);
        var units=await db.Units.Where(x=>x.IsActive).OrderBy(x=>x.NameAr)
            .Select(x=>new InventoryReference(x.Id,x.NameAr,null)).ToListAsync(ct);
        var settings=(await Rows("SELECT Payload FROM dbo.StockAccountSettings WHERE BranchId=@b",r=>JsonSerializer.Deserialize<InventoryAccounts>(r.GetString(0)),ct,("@b",branchId))).SingleOrDefault();
        return new(warehouses,units,settings);
    }
    public async Task<IReadOnlyList<ProductInfo>> ProductsAsync(int actor,CancellationToken ct=default)
    {
        await authorization.DemandAsync(actor,PermissionCodes.JournalRead,ct);
        return await Rows("SELECT p.ProductId,p.Code,p.Name,p.BaseUnitId,p.CostMethod,p.TrackBatches,CAST(CASE WHEN s.ProductId IS NULL THEN 1 ELSE 0 END AS bit) FROM dbo.StockProducts p LEFT JOIN dbo.StockServiceItems s ON s.ProductId=p.ProductId ORDER BY p.Code",r=>new ProductInfo(r.GetInt32(0),r.GetString(1),r.GetString(2),r.GetInt32(3),(CostMethod)r.GetInt32(4),r.GetBoolean(5),r.GetBoolean(6)),ct);
    }
    public async Task<IReadOnlyList<ProductUnitInfo>> UnitsAsync(int actor,int productId,CancellationToken ct=default)
    {
        await authorization.DemandAsync(actor,PermissionCodes.JournalRead,ct);
        return await Rows("SELECT ProductId,UnitId,Factor FROM dbo.StockProductUnits WHERE ProductId=@p ORDER BY UnitId",r=>new ProductUnitInfo(r.GetInt32(0),r.GetInt32(1),r.GetDecimal(2)),ct,("@p",productId));
    }
    public async Task<IReadOnlyList<BatchInfo>> BatchesAsync(int actor,int branchId,int warehouseId,CancellationToken ct=default)
    {
        await authorization.DemandAsync(actor,PermissionCodes.JournalRead,ct);
        await Branch(actor,branchId,ct);
        await Warehouse(branchId,warehouseId,ct);
        return await Rows("SELECT BatchId,ProductId,WarehouseId,Code,Expiry FROM dbo.StockBatches WHERE WarehouseId=@w ORDER BY Expiry,BatchId",r=>new BatchInfo(r.GetInt64(0),r.GetInt32(1),r.GetInt32(2),r.GetString(3),DateOnly.FromDateTime(r.GetDateTime(4))),ct,("@w",warehouseId));
    }
    private static StockMovement Movement(SqlDataReader r) => new(r.GetInt64(0),r.GetInt64(1),r.GetInt32(2),r.GetInt32(3),r.GetInt32(4),
        r.IsDBNull(5)?null:r.GetInt64(5),r.GetDecimal(6),r.GetDecimal(7),r.GetDecimal(8),r.GetDecimal(9),r.IsDBNull(10)?null:r.GetInt64(10),r.IsDBNull(11)?null:r.GetDecimal(11));
    private const string MovementColumns="m.MovementId,m.DocumentId,m.LineNumber,m.ProductId,m.WarehouseId,m.BatchId,m.Quantity,m.Value,m.Net,m.Tax,m.OriginalMovementId,m.BilledQuantity";
    private async Task<StockDocument> Read(long id,CancellationToken ct)
    {
        var header=(await Rows("SELECT Payload,Version,State,Number,JournalId,ReversalId FROM dbo.StockDocuments WHERE DocumentId=@i",
            r=>new StockDocument(id,JsonSerializer.Deserialize<StockDocumentDraft>(r.GetString(0))!,r.GetInt32(1),r.GetInt32(2),
                r.IsDBNull(3)?null:r.GetInt64(3),r.IsDBNull(4)?null:r.GetInt64(4),r.IsDBNull(5)?null:r.GetInt64(5),[]),ct,("@i",id))).Single();
        return header with { Movements=await Rows("SELECT "+MovementColumns+" FROM dbo.StockMovements m WHERE DocumentId=@i ORDER BY MovementId",Movement,ct,("@i",id)) };
    }
    private async Task Validate(int actor,StockDocumentDraft d,CancellationToken ct)
    {
        await Branch(actor,d.BranchId,ct);
        await Warehouse(d.BranchId,d.WarehouseId,ct);
        if(d.TargetWarehouseId is int target) await Warehouse(d.BranchId,target,ct);
        if(d.SourceKey==Guid.Empty || !Enum.IsDefined(d.Kind) || d.Lines.Count is <1 or >500 || d.CurrencyId<=0 || d.RateToBase<=0 ||
            (d.Kind==StockDocumentKind.Transfer)!=(d.TargetWarehouseId!=null) || d.TargetWarehouseId==d.WarehouseId)
            throw new ArgumentException("Invalid stock document.");
        bool returns=d.Kind is StockDocumentKind.SalesReturn or StockDocumentKind.PurchaseReturn;
        bool trade=returns || d.Kind is StockDocumentKind.Purchase or StockDocumentKind.Sale;
        if(!trade && (d.Credit || d.TaxEnabled) || d.TaxRate is <0 or >1 || !d.TaxEnabled && (d.TaxRate!=0 || d.PricesIncludeTax))
            throw new ArgumentException("Invalid tax or credit selection.");
        foreach(var l in d.Lines)
        {
            if(l.ProductId<=0 || l.UnitId<=0 || l.Quantity<=0 || l.UnitPrice<0 || l.Discount<0 || (l.OriginalMovementId!=null)!=returns ||
                (l.BatchOverrideReason?.Length??0)>300 || returns && (l.UnitPrice!=0 || l.Discount!=0))
                throw new ArgumentException("Invalid stock line or original reference.");
            StockCalculation.Exact(l.Quantity,8); StockCalculation.Exact(l.UnitPrice,8); StockCalculation.Exact(l.Discount,8);
        }
    }
    private async Task<StockDocumentDraft> ReturnHeader(int actor,StockDocumentDraft draft,CancellationToken ct)
    {
        if(!IsReturn(draft.Kind))return draft;
        await Branch(actor,draft.BranchId,ct);
        StockDocumentDraft? original=null;
        var expected=draft.Kind==StockDocumentKind.SalesReturn?StockDocumentKind.Sale:StockDocumentKind.Purchase;
        foreach(var movement in draft.Lines.Select(x=>x.OriginalMovementId).Distinct())
        {
            var id=await sql.ScalarAsync("""
                SELECT h.DocumentId FROM dbo.StockMovements m JOIN dbo.StockDocuments h ON h.DocumentId=m.DocumentId
                WHERE m.MovementId=@m AND (m.Quantity<>0 OR m.BilledQuantity IS NOT NULL) AND h.State=2 AND h.BranchId=@b AND h.Kind=@kind AND h.EntryDate<=@date
                """,ct,("@m",movement),("@b",draft.BranchId),("@kind",(int)expected),("@date",draft.Date));
            if(id==0)throw new InvalidOperationException("Return needs an original posted movement in this branch.");
            var invoice=(await Read(id,ct)).Draft;
            if(original!=null && (original.CurrencyId!=invoice.CurrencyId || original.RateToBase!=invoice.RateToBase || original.TaxRate!=invoice.TaxRate ||
                original.TaxEnabled!=invoice.TaxEnabled || original.PricesIncludeTax!=invoice.PricesIncludeTax || original.Credit!=invoice.Credit ||
                original.WarehouseId!=invoice.WarehouseId || original.CounterpartyAccountId!=invoice.CounterpartyAccountId))
                throw new InvalidOperationException("Return lines require compatible original invoices.");
            original=invoice;
        }
        if(original==null)throw new ArgumentException("Return requires at least one original movement.");
        return draft with { CurrencyId=original.CurrencyId,RateToBase=original.RateToBase,TaxRate=original.TaxRate,TaxEnabled=original.TaxEnabled,
            PricesIncludeTax=original.PricesIncludeTax,Credit=original.Credit,WarehouseId=original.WarehouseId,CounterpartyAccountId=original.CounterpartyAccountId };
    }
    private async Task FreeSource(Guid key,CancellationToken ct)
    {
        if(key==Guid.Empty || await sql.ScalarAsync("""
            SELECT (SELECT COUNT(*) FROM dbo.JournalEntries WHERE SourceKey=@k)+(SELECT COUNT(*) FROM dbo.JournalDocuments WHERE SourceKey=@k)
                +(SELECT COUNT(*) FROM dbo.StockDocuments WHERE SourceKey=@k)+(SELECT COUNT(*) FROM dbo.StockDebtAllocations WHERE SourceKey=@k)
            """,ct,("@k",key))!=0) throw new InvalidOperationException("Source key already reserved.");
    }
    public Task<StockDocument> SaveAsync(int actor,StockDocumentDraft draft,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.JournalPost,ct,async () =>
        {
            draft=await ReturnHeader(actor,draft,ct);
            await Validate(actor,draft,ct);
            var payload=StockPayload.Encode(draft);
            var id=await sql.ScalarAsync("SELECT DocumentId FROM dbo.StockDocuments WHERE SourceKey=@k",ct,("@k",draft.SourceKey));
            if(id!=0)
            {
                var existing=await Read(id,ct);
                if(StockPayload.Encode(existing.Draft)!=payload) throw new InvalidOperationException("Source key reused with different content.");
                return existing;
            }
            await FreeSource(draft.SourceKey,ct);
            id=await sql.ScalarAsync("INSERT dbo.StockDocuments(BranchId,SourceKey,Kind,EntryDate,Payload,CreatedBy) VALUES(@b,@k,@t,@d,@p,@a); SELECT CAST(SCOPE_IDENTITY() AS bigint)",
                ct,("@b",draft.BranchId),("@k",draft.SourceKey),("@t",(int)draft.Kind),("@d",draft.Date),("@p",payload),("@a",actor));
            await Audit(actor,"STOCK_DRAFT_SAVED","StockDocuments",id,ct);
            return await Read(id,ct);
        });
    public Task<StockDocument> UpdateAsync(int actor,long id,int version,StockDocumentDraft draft,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.JournalPost,ct,async () =>
        {
            draft=await ReturnHeader(actor,draft,ct);
            await Validate(actor,draft,ct);
            var old=await Read(id,ct);
            if(old.State!=1 || old.Draft.BranchId!=draft.BranchId || old.Draft.SourceKey!=draft.SourceKey || old.Draft.Kind!=draft.Kind)
                throw new InvalidOperationException("Document identity or posted history cannot change.");
            var payload=StockPayload.Encode(draft);
            if(old.Version==version+1 && StockPayload.Encode(old.Draft)==payload) return old;
            if(old.Version!=version) throw new DocumentConflictException();
            await sql.ExecuteAsync("UPDATE dbo.StockDocuments SET EntryDate=@d,Payload=@p,Version=Version+1 WHERE DocumentId=@i",ct,("@i",id),("@d",draft.Date),("@p",payload));
            await Audit(actor,"STOCK_DRAFT_UPDATED","StockDocuments",id,ct);
            return await Read(id,ct);
        });
    public async Task<StockDocument> GetAsync(int actor,long id,CancellationToken ct=default)
    {
        await authorization.DemandAsync(actor,PermissionCodes.JournalRead,ct);
        var doc=await Read(id,ct); await Branch(actor,doc.Draft.BranchId,ct); return doc;
    }
    public async Task<IReadOnlyList<StockDocument>> ListAsync(int actor,int branchId,int page=0,CancellationToken ct=default)
    {
        await authorization.DemandAsync(actor,PermissionCodes.JournalRead,ct); await Branch(actor,branchId,ct);
        if(page is <0 or >1000000) throw new ArgumentOutOfRangeException(nameof(page));
        var ids=await Rows("SELECT DocumentId FROM dbo.StockDocuments WHERE BranchId=@b ORDER BY DocumentId DESC OFFSET @o ROWS FETCH NEXT 50 ROWS ONLY",r=>r.GetInt64(0),ct,("@b",branchId),("@o",page*50));
        var docs=new List<StockDocument>(); foreach(var id in ids) docs.Add(await Read(id,ct)); return docs;
    }
}
