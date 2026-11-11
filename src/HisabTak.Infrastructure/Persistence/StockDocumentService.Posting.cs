using System.Text.Json;
using HisabTak.Application.Accounting;
using HisabTak.Application.Inventory;
using HisabTak.Domain.Accounting;
using HisabTak.Domain.Inventory;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed partial class StockDocumentService
{
    private static bool IsReturn(StockDocumentKind kind) => kind is StockDocumentKind.PurchaseReturn or StockDocumentKind.SalesReturn;
    private static bool IsTrade(StockDocumentKind kind) => IsReturn(kind) || kind is StockDocumentKind.Purchase or StockDocumentKind.Sale;
    private static string TypeCode(StockDocumentDraft d) => d.Kind switch
    {
        StockDocumentKind.Sale => d.Credit?"SALES_CREDIT":"SALES_CASH",
        StockDocumentKind.Purchase => d.Credit?"PURCHASE_CREDIT":"PURCHASE_CASH",
        StockDocumentKind.SalesReturn => d.Credit?"SALES_RETURN_CREDIT":"SALES_RETURN_CASH",
        StockDocumentKind.PurchaseReturn => d.Credit?"PURCHASE_RETURN_CREDIT":"PURCHASE_RETURN_CASH",
        StockDocumentKind.Transfer => "STOCK_TRANSFER", StockDocumentKind.Opening => "OPENING_BALANCE",
        StockDocumentKind.Increase => "STOCK_ADJUST_INCREASE", StockDocumentKind.Decrease => "STOCK_ADJUST_DECREASE",
        _ => throw new ArgumentOutOfRangeException(nameof(d))
    };
    private async Task DemandClass(int account,int branch,int currency,string[] classes,CancellationToken ct)
    {
        bool ok=await (from a in db.Accounts join g in db.AccountGeneralGroups on a.GeneralGroupId equals g.Id
            where a.Id==account && a.BranchId==branch && a.CurrencyId==currency && a.IsActive && !a.IsSuspended && g.IsActive && classes.Contains(g.SystemCode!)
            select a.Id).AnyAsync(ct);
        if(!ok) throw new InvalidOperationException("Counterparty account classification, branch or currency is invalid.");
    }
    private async Task Chronology(int product,int warehouse,DateOnly date,CancellationToken ct)
    {
        if(await sql.ScalarAsync("""
            SELECT COUNT_BIG(*) FROM dbo.StockMovements m JOIN dbo.StockDocuments d ON d.DocumentId=m.DocumentId WHERE
            m.ProductId=@p AND m.WarehouseId=@w AND (d.EntryDate>@d OR d.ReversalDate>@d)
            """,ct,("@p",product),("@w",warehouse),("@d",date))>0) throw new InvalidOperationException("Backdated stock movement.");
    }
    // Include the current transaction's uncommitted movements when posting multiple lines.
    private async Task<StockPosition> Position(int product,int warehouse,CancellationToken ct)
    {
        return (await Rows("""
            SELECT COALESCE(SUM(m.Quantity),0),COALESCE(SUM(m.Value),0),
            COALESCE((SELECT TOP(1) v.LastPurchasePrice FROM dbo.StockMovements v JOIN dbo.StockDocuments h ON h.DocumentId=v.DocumentId
                WHERE h.State<>3 AND v.ProductId=@p AND v.WarehouseId=@w AND v.LastPurchasePrice IS NOT NULL ORDER BY v.MovementId DESC),CAST(0 AS decimal(28,8)))
            FROM dbo.StockMovements m JOIN dbo.StockDocuments d ON d.DocumentId=m.DocumentId WHERE d.State<>3 AND m.ProductId=@p AND m.WarehouseId=@w
            """,r=>new StockPosition(r.GetDecimal(0),r.GetDecimal(1),r.GetDecimal(2)),ct,("@p",product),("@w",warehouse))).Single();
    }
    private async Task AddMovement(long id,int line,int product,int warehouse,long? batch,decimal qty,decimal value,decimal net,decimal tax,long? original,decimal? last,CancellationToken ct,decimal? billed=null)
    {
        StockCalculation.Exact(value,8);
        await sql.ExecuteAsync("""
            INSERT dbo.StockMovements(DocumentId,LineNumber,ProductId,WarehouseId,BatchId,Quantity,Value,Net,Tax,OriginalMovementId,LastPurchasePrice,BilledQuantity)
            VALUES(@i,@l,@p,@w,@b,@q,@v,@n,@t,@o,@last,@billed)
            """,ct,("@i",id),("@l",line),("@p",product),("@w",warehouse),("@b",batch),("@q",qty),("@v",value),("@n",net),("@t",tax),("@o",original),("@last",last),("@billed",billed));
    }
    private async Task<List<(long? Batch,decimal Quantity)>> IssueBatches(int actor,StockDocumentDraft d,StockLine l,bool tracked,decimal quantity,long? historical,CancellationToken ct)
    {
        if(!tracked)
        {
            if(l.BatchId!=null || l.BatchOverrideReason!=null) throw new ArgumentException("Untracked item has no batch selection.");
            return [(null,quantity)];
        }
        if(historical!=null && l.BatchId!=null && l.BatchId!=historical) throw new InvalidOperationException("Return requires the original batch.");
        if(l.BatchId!=null && historical==null)
        {
            await authorization.DemandAsync(actor,PermissionCodes.BatchOverride,ct);
            ArgumentException.ThrowIfNullOrWhiteSpace(l.BatchOverrideReason);
        }
        var selected=historical??l.BatchId;
        var available=await Rows("""
            SELECT b.BatchId,SUM(m.Quantity) FROM dbo.StockBatches b JOIN dbo.StockMovements m ON m.BatchId=b.BatchId
            JOIN dbo.StockDocuments h ON h.DocumentId=m.DocumentId WHERE h.State<>3 AND b.ProductId=@p AND b.WarehouseId=@w
            AND b.Expiry>=@d AND (@b IS NULL OR b.BatchId=@b) GROUP BY b.BatchId,b.Expiry HAVING SUM(m.Quantity)>0 ORDER BY b.Expiry,b.BatchId
            """,r=>(r.GetInt64(0),r.GetDecimal(1)),ct,("@p",l.ProductId),("@w",d.WarehouseId),("@d",d.Date),("@b",selected));
        var result=new List<(long?,decimal)>(); var remaining=quantity;
        foreach(var batch in available)
        {
            var take=Math.Min(remaining,batch.Item2); result.Add((batch.Item1,take)); remaining-=take;
            if(remaining==0) break;
        }
        if(remaining!=0) throw new InvalidOperationException("Insufficient nonexpired batch stock.");
        return result;
    }
    private async Task<long?> TransferBatch(long? source,int target,CancellationToken ct)
    {
        if(source==null) return null;
        return await sql.ScalarAsync("""
            IF EXISTS(SELECT 1 FROM dbo.StockBatches a JOIN dbo.StockBatches b ON a.ProductId=b.ProductId AND a.Code=b.Code
                WHERE a.BatchId=@s AND b.WarehouseId=@w AND a.Expiry<>b.Expiry) THROW 51149,'Destination batch expiry conflicts.',1;
            IF NOT EXISTS(SELECT 1 FROM dbo.StockBatches a JOIN dbo.StockBatches b ON a.ProductId=b.ProductId AND a.Code=b.Code
                WHERE a.BatchId=@s AND b.WarehouseId=@w)
                INSERT dbo.StockBatches(ProductId,WarehouseId,Code,Expiry) SELECT ProductId,@w,Code,Expiry FROM dbo.StockBatches WHERE BatchId=@s;
            SELECT b.BatchId FROM dbo.StockBatches a JOIN dbo.StockBatches b ON a.ProductId=b.ProductId AND a.Code=b.Code WHERE a.BatchId=@s AND b.WarehouseId=@w
            """,ct,("@s",source),("@w",target));
    }
    private async Task<(StockMovement Original,StockDocument Owner,decimal Cost,decimal Net,decimal Tax)> ReturnAmounts(StockDocumentDraft d,StockLine l,decimal qty,int precision,int currencyPrecision,CancellationToken ct)
    {
        var orig=(await Rows("SELECT "+MovementColumns+" FROM dbo.StockMovements m WHERE m.MovementId=@i",Movement,ct,("@i",l.OriginalMovementId))).Single();
        var owner=await Read(orig.DocumentId,ct);
        var required=d.Kind==StockDocumentKind.SalesReturn?StockDocumentKind.Sale:StockDocumentKind.Purchase;
        if(owner.State!=2 || owner.Draft.Kind!=required || owner.Draft.BranchId!=d.BranchId || owner.Draft.Credit!=d.Credit ||
            owner.Draft.CounterpartyAccountId!=d.CounterpartyAccountId || owner.Draft.CurrencyId!=d.CurrencyId || owner.Draft.RateToBase!=d.RateToBase ||
            orig.ProductId!=l.ProductId || orig.WarehouseId!=d.WarehouseId || (orig.Quantity==0 && orig.BilledQuantity==null) || l.BatchId!=null && l.BatchId!=orig.BatchId)
            throw new InvalidOperationException("Return must reference the original invoice movement and party.");
        var used=(await Rows("""
            SELECT COALESCE(SUM(COALESCE(m.BilledQuantity,ABS(m.Quantity))),0),COALESCE(SUM(ABS(m.Value)),0),COALESCE(SUM(m.Net),0),COALESCE(SUM(m.Tax),0)
            FROM dbo.StockMovements m JOIN dbo.StockDocuments h ON h.DocumentId=m.DocumentId WHERE h.State<>3 AND OriginalMovementId=@i
            """,r=>(r.GetDecimal(0),r.GetDecimal(1),r.GetDecimal(2),r.GetDecimal(3)),ct,("@i",orig.Id))).Single();
        decimal Part(decimal total,decimal usedValue,int scale)=>StockCalculation.Allocate(total,orig.BilledQuantity??Math.Abs(orig.Quantity),qty,used.Item1,usedValue,scale);
        return (orig,owner,Part(Math.Abs(orig.Value),used.Item2,precision),Part(orig.Net,used.Item3,currencyPrecision),Part(orig.Tax,used.Item4,currencyPrecision));
    }
    private sealed record LineEffect(decimal Net,decimal Tax,decimal Cost,decimal Revaluation,long? Invoice);
    private async Task<LineEffect> ApplyLine(int actor,StockDocument document,int index,int precision,int currencyPrecision,LastPurchaseBookPolicy bookPolicy,CancellationToken ct)
    {
        var d=document.Draft; var l=d.Lines[index];
        if(!await db.Units.AnyAsync(x=>x.Id==l.UnitId && x.IsActive,ct)) throw new InvalidOperationException("Unit unavailable.");
        var item=(await Rows("""
            SELECT p.CostMethod,p.TrackBatches,u.Factor,CAST(CASE WHEN s.ProductId IS NULL THEN 0 ELSE 1 END AS bit) FROM dbo.StockProducts p JOIN dbo.StockProductUnits u ON p.ProductId=u.ProductId LEFT JOIN dbo.StockServiceItems s ON s.ProductId=p.ProductId WHERE p.ProductId=@p AND u.UnitId=@u
            """,r=>((CostMethod)r.GetInt32(0),r.GetBoolean(1),r.GetDecimal(2),r.GetBoolean(3)),ct,("@p",l.ProductId),("@u",l.UnitId))).Single();
        var qty=StockCalculation.Quantity(l.Quantity,item.Item3);
        var calc=InvoiceCalculation.Calculate(d.CurrencyId,currencyPrecision,[new(l.Quantity,l.UnitPrice,l.Discount)],d.TaxEnabled,d.TaxRate,d.PricesIncludeTax);
        decimal net=calc.Net,tax=calc.Tax;
        decimal? originalCost=null; long? originalInvoice=null; long? originalBatch=null;
        if(IsReturn(d.Kind))
        {
            var ret=await ReturnAmounts(d,l,qty,precision,currencyPrecision,ct);
            originalCost=ret.Cost; originalInvoice=ret.Owner.Id; originalBatch=ret.Original.BatchId; net=ret.Net; tax=ret.Tax;
        }
        if(item.Item4)
        {
            if(d.Kind is not (StockDocumentKind.Sale or StockDocumentKind.SalesReturn) || l.BatchId!=null || l.BatchOverrideReason!=null)
                throw new InvalidOperationException("Services use sales and sales returns without stock movements.");
            await AddMovement(document.Id,index,l.ProductId,d.WarehouseId,null,0,0,net,tax,l.OriginalMovementId,null,ct,qty);
            return new(net,tax,0,0,originalInvoice);
        }
        await Chronology(l.ProductId,d.WarehouseId,d.Date,ct);
        var before=await Position(l.ProductId,d.WarehouseId,ct);
        bool increase=d.Kind is StockDocumentKind.Purchase or StockDocumentKind.Opening or StockDocumentKind.Increase or StockDocumentKind.SalesReturn;
        if(increase)
        {
            if(d.Kind==StockDocumentKind.Opening && await sql.ScalarAsync("SELECT COUNT(*) FROM dbo.StockMovements WHERE WarehouseId=@w AND ProductId=@p AND DocumentId<>@i",ct,("@w",d.WarehouseId),("@p",l.ProductId),("@i",document.Id))>0)
                throw new InvalidOperationException("Opening must be the first movement.");
            var batch=originalBatch??l.BatchId;
            if(item.Item2!=(batch!=null) || batch!=null && await sql.ScalarAsync("SELECT COUNT(*) FROM dbo.StockBatches WHERE BatchId=@b AND ProductId=@p AND WarehouseId=@w",ct,("@b",batch),("@p",l.ProductId),("@w",d.WarehouseId))!=1)
                throw new InvalidOperationException("Invalid incoming batch.");
            var purchase=d.Kind is StockDocumentKind.Purchase or StockDocumentKind.Opening;
            var change=StockCalculation.Receive(before,qty,originalCost??StockCalculation.Round(net*d.RateToBase,precision),item.Item1,precision,purchase,bookPolicy);
            await AddMovement(document.Id,index,l.ProductId,d.WarehouseId,batch,qty,change.Value,net,tax,l.OriginalMovementId,purchase?change.Position.LastPurchasePrice:null,ct);
            if(change.Revaluation!=0) await AddMovement(document.Id,index,l.ProductId,d.WarehouseId,null,0,change.Revaluation,0,0,null,null,ct);
            return new(net,tax,change.Value,change.Revaluation,originalInvoice);
        }
        // Transfers carry their share of local book value and never post a valuation gain.
        var issueMethod=d.Kind==StockDocumentKind.Transfer?CostMethod.WeightedAverage:item.Item1;
        var issue=StockCalculation.Issue(before,qty,precision,originalCost,issueMethod);
        var batches=await IssueBatches(actor,d,l,item.Item2,qty,originalBatch,ct);
        decimal usedQty=0,usedCost=0,usedNet=0,usedTax=0;
        if(d.TargetWarehouseId is int target) await Chronology(l.ProductId,target,d.Date,ct);
        foreach(var (batch,take) in batches)
        {
            decimal Allocate(decimal value,decimal used,int scale)=>StockCalculation.Allocate(value,qty,take,usedQty,used,scale);
            var costPart=Allocate(issue.Value,usedCost,precision); var netPart=Allocate(net,usedNet,currencyPrecision); var taxPart=Allocate(tax,usedTax,currencyPrecision);
            await AddMovement(document.Id,index,l.ProductId,d.WarehouseId,batch,-take,-costPart,netPart,taxPart,l.OriginalMovementId,null,ct);
            if(d.TargetWarehouseId is int dest)
                await AddMovement(document.Id,index,l.ProductId,dest,await TransferBatch(batch,dest,ct),take,costPart,0,0,null,null,ct);
            usedQty+=take; usedCost+=costPart; usedNet+=netPart; usedTax+=taxPart;
        }
        if(issue.Revaluation!=0) await AddMovement(document.Id,index,l.ProductId,d.WarehouseId,null,0,issue.Revaluation,0,0,null,null,ct);
        return new(net,tax,issue.Value,issue.Revaluation,originalInvoice);
    }
    public Task<StockDocument> PostAsync(int actor,long id,int version,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.JournalPost,ct,() => PostCore(actor,id,version,ct));

    private async Task<StockDocument> PostCore(int actor,long id,int version,CancellationToken ct)
    {
            var doc=await Read(id,ct); var d=doc.Draft;
            await Branch(actor,d.BranchId,ct);
            if (IsIntent(d.Kind)) throw new InvalidOperationException("Convert the intent to an invoice before posting.");
            if(doc.State!=1)
            {
                if(version!=doc.Version-doc.State+1) throw new DocumentConflictException();
                return doc;
            }
            if(doc.Version!=version) throw new DocumentConflictException();
            await Validate(actor,d,ct);
            var type=TypeCode(d);
            if(!await db.DocumentTypes.AnyAsync(x=>x.Code==type && x.IsActive,ct)) throw new InvalidOperationException("Document type unavailable.");
            var config=(await Rows("""
                SELECT a.Payload,r.Precision,r.BaseCurrencyId FROM dbo.StockAccountSettings a JOIN dbo.LedgerRoundingSettings r ON r.BranchId=a.BranchId WHERE a.BranchId=@b
                """,r=>(JsonSerializer.Deserialize<InventoryAccounts>(r.GetString(0))!,Convert.ToInt32(r.GetValue(1)),r.GetInt32(2)),ct,("@b",d.BranchId))).Single();
            var a=config.Item1; var precision=config.Item2;
            if(d.Kind==StockDocumentKind.SalesReturn && a.SalesReturnsAccountId==null)
                throw new InvalidOperationException("Configure a separate sales returns account before posting.");
            var baseId=config.Item3;
            if(d.CurrencyId==baseId && d.RateToBase!=1) throw new InvalidOperationException("Base currency requires identity rate.");
            var currencyPrecision=await CurrencyPrecision(d.CurrencyId,baseId,precision,ct);
            if(!IsTrade(d.Kind) && (d.CurrencyId!=baseId || d.RateToBase!=1)) throw new InvalidOperationException("Internal movements are valued in base currency.");
            if(IsTrade(d.Kind))
            {
                bool sale=d.Kind is StockDocumentKind.Sale or StockDocumentKind.SalesReturn;
                await DemandClass(d.CounterpartyAccountId,d.BranchId,d.CurrencyId,d.Credit?[sale?"CUSTOMER":"SUPPLIER"]:["CASH","BANK"],ct);
                if(new[]{a.StockAccountId,a.SalesAccountId,a.CostAccountId,a.AdjustmentAccountId,a.InputTaxAccountId,a.OutputTaxAccountId}.Contains(d.CounterpartyAccountId) || a.SalesReturnsAccountId==d.CounterpartyAccountId)
                    throw new InvalidOperationException("Counterparty cannot also be a mapped stock account.");
            }
            var effects=new List<LineEffect>();
            for(var i=0;i<d.Lines.Count;i++) effects.Add(await ApplyLine(actor,doc,i,precision,currencyPrecision,a.BookPolicy,ct));
            var net=effects.Sum(e=>e.Net); var tax=effects.Sum(e=>e.Tax); var cost=effects.Sum(e=>e.Cost);
            var lines=new List<JournalLineDefinition>();
            void Debit(int account,decimal signed,bool inInvoiceCurrency=false)
            {
                if(signed!=0) lines.Add(new(account,signed>0?JournalSide.Debit:JournalSide.Credit,new(new(Math.Abs(signed),inInvoiceCurrency?d.CurrencyId:baseId),baseId,inInvoiceCurrency?d.RateToBase:1,d.Date)));
            }
            if(d.Kind is StockDocumentKind.Sale or StockDocumentKind.SalesReturn)
            {
                int sign=d.Kind==StockDocumentKind.Sale?1:-1;
                Debit(d.CounterpartyAccountId,sign*(net+tax),true); Debit(d.Kind==StockDocumentKind.SalesReturn?a.SalesReturnsAccountId!.Value:a.SalesAccountId,-sign*net*d.RateToBase); Debit(a.OutputTaxAccountId,-sign*tax*d.RateToBase);
                Debit(a.CostAccountId,sign*cost); Debit(a.StockAccountId,-sign*cost);
            }
            else if(d.Kind is StockDocumentKind.Purchase or StockDocumentKind.PurchaseReturn)
            {
                int sign=d.Kind==StockDocumentKind.Purchase?1:-1;
                if(d.Kind==StockDocumentKind.Purchase)
                    foreach(var effect in effects) Debit(a.StockAccountId,effect.Net*d.RateToBase);
                else
                {
                    Debit(a.StockAccountId,sign*cost);
                    Debit(a.AdjustmentAccountId,sign*(net*d.RateToBase-cost));
                }
                Debit(a.InputTaxAccountId,sign*tax*d.RateToBase); Debit(d.CounterpartyAccountId,-sign*(net+tax),true);
            }
            else
            {
                int sign=d.Kind==StockDocumentKind.Decrease?-1:1;
                Debit(a.StockAccountId,sign*cost); Debit(d.Kind==StockDocumentKind.Transfer?a.StockAccountId:a.AdjustmentAccountId,-sign*cost);
            }
            var diff=effects.Sum(e=>e.Revaluation); Debit(a.StockAccountId,diff); Debit(a.AdjustmentAccountId,-diff);
            var draft=JournalDraft.Create(d.BranchId,new FiscalPeriod("Stock",d.Date,d.Date),d.Date,d.SourceKey,lines);
            var offsets=d.Credit && IsReturn(d.Kind)?await ReturnOffsets(d,effects,baseId,precision,ct):[];
            if(offsets.Count>0 && d.CurrencyId!=baseId)
                await SaveBasis(d.SourceKey,d.CounterpartyAccountId,net+tax,offsets.Sum(x=>x.Book+x.RefundBook),null,offsets,ct);
            var journal=await journals.PostDocumentAsync(actor,draft,ct);
            var number=await sql.ScalarAsync("""
                UPDATE dbo.DocumentNumbers SET LastNumber=LastNumber+1 WHERE BranchId=@b AND TypeCode=@t AND DocumentYear=@y;
                IF @@ROWCOUNT=0 INSERT dbo.DocumentNumbers VALUES(@b,@t,@y,1);
                SELECT LastNumber FROM dbo.DocumentNumbers WHERE BranchId=@b AND TypeCode=@t AND DocumentYear=@y
                """,ct,("@b",d.BranchId),("@t",type),("@y",d.Date.Year));
            if(d.Credit) await RecordDebt(doc,effects,journal,offsets,precision,ct);
            await sql.ExecuteAsync("UPDATE dbo.StockDocuments SET State=2,Version=Version+1,Number=@n,JournalId=@j WHERE DocumentId=@i",ct,("@i",id),("@j",journal),("@n",number));
            await Audit(actor,"STOCK_DOCUMENT_POSTED","StockDocuments",id,ct);
            return await Read(id,ct);
    }
}
