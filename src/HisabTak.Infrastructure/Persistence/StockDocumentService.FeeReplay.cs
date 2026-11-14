using HisabTak.Application.Inventory;
using HisabTak.Domain.Inventory;

namespace HisabTak.Infrastructure.Persistence;

public sealed partial class StockDocumentService
{
    private sealed record CostEvent(long Id,long Document,int Line,int Warehouse,long? Batch,decimal Quantity,
        decimal Value,long? Original,decimal? LastPrice,StockDocumentKind Kind,LastPurchaseBookPolicy? Policy,decimal ExtraValuation);
    private sealed record CostEffect(CostEvent Origin,decimal Amount,bool Valuation);
    private sealed record CostReplay(IReadOnlyList<CostEffect> Effects,IReadOnlyDictionary<int,decimal> Prices);

    private async Task<CostReplay> ReplayFees(int product,int branch,IReadOnlyDictionary<long,decimal> charges,
        CostMethod method,int precision,CancellationToken ct)
    {
        var events=await Rows("""
            SELECT m.MovementId,m.DocumentId,m.LineNumber,m.WarehouseId,m.BatchId,m.Quantity,
                m.Value+COALESCE(f.Cost,0),m.OriginalMovementId,m.LastPurchasePrice,h.Kind,
                h.BookPolicyAtPosting,COALESCE(f.Valuation,0)
            FROM dbo.StockMovements m JOIN dbo.StockDocuments h ON h.DocumentId=m.DocumentId
            OUTER APPLY(SELECT SUM(CASE WHEN x.IsCostRevaluation=0 THEN x.Value ELSE 0 END) Cost,
                SUM(CASE WHEN x.IsCostRevaluation=1 THEN x.Value ELSE 0 END) Valuation
                FROM dbo.StockMovements x JOIN dbo.StockDocuments fh ON fh.DocumentId=x.DocumentId
                WHERE x.OriginalMovementId=m.MovementId AND fh.Kind=11 AND fh.State=2) f
            WHERE h.State=2 AND h.BranchId=@b AND m.ProductId=@p
                AND (h.Kind<>11 OR (m.OriginalMovementId IS NULL AND m.Value<>0))
            ORDER BY m.MovementId
            """,r=>new CostEvent(r.GetInt64(0),r.GetInt64(1),r.GetInt32(2),r.GetInt32(3),
                r.IsDBNull(4)?null:r.GetInt64(4),r.GetDecimal(5),r.GetDecimal(6),
                r.IsDBNull(7)?null:r.GetInt64(7),r.IsDBNull(8)?null:r.GetDecimal(8),
                (StockDocumentKind)r.GetInt32(9),r.IsDBNull(10)?null:(LastPurchaseBookPolicy)r.GetInt32(10),r.GetDecimal(11)),
            ct,("@b",branch),("@p",product));
        var positions=new Dictionary<int,StockPosition>();
        var deltas=new Dictionary<int,StockPosition>();
        var changes=new Dictionary<long,decimal>();
        var originals=events.ToDictionary(x=>x.Id);
        var returned=new Dictionary<long,(decimal Quantity,decimal Original,decimal Adjusted)>();
        var effects=new List<CostEffect>();
        foreach(var group in events.GroupBy(x=>(x.Document,x.Line)))
        {
            var physical=group.Where(x=>x.Quantity!=0).ToArray();
            if(physical.Length==0)
            {
                foreach(var item in group)
                {
                    var before=positions.GetValueOrDefault(item.Warehouse,new(0,0,0));
                    positions[item.Warehouse]=before with {BookValue=before.BookValue+item.Value+item.ExtraValuation};
                }
                continue;
            }
            var first=physical[0];
            var source=physical.Where(x=>x.Warehouse==first.Warehouse).ToArray();
            var beforeSource=positions.GetValueOrDefault(first.Warehouse,new(0,0,0));
            var delta=deltas.GetValueOrDefault(first.Warehouse,new(0,0,0));
            var adjusted=new StockPosition(beforeSource.Quantity,beforeSource.BookValue+delta.BookValue,beforeSource.LastPurchasePrice+delta.LastPurchasePrice);
            var quantity=source.Sum(x=>x.Quantity);
            var value=source.Sum(x=>x.Value);
            decimal amountDelta=source.Sum(x=>charges.GetValueOrDefault(x.Id)),valuationDelta;
            var purchase=first.Kind is StockDocumentKind.Purchase or StockDocumentKind.Opening;
            if(first.Kind is StockDocumentKind.SalesReturn or StockDocumentKind.PurchaseReturn)
            {
                var origin=originals[first.Original!.Value];
                var used=returned.GetValueOrDefault(origin.Id);
                var originalTotal=Math.Abs(origin.Value);
                var adjustedTotal=originalTotal+Math.Sign(origin.Quantity)*changes.GetValueOrDefault(origin.Id);
                var originalPart=StockCalculation.Allocate(originalTotal,Math.Abs(origin.Quantity),Math.Abs(quantity),used.Quantity,used.Original,precision);
                var adjustedPart=StockCalculation.Allocate(adjustedTotal,Math.Abs(origin.Quantity),Math.Abs(quantity),used.Quantity,used.Adjusted,precision);
                returned[origin.Id]=(used.Quantity+Math.Abs(quantity),used.Original+originalPart,used.Adjusted+adjustedPart);
                amountDelta=(adjustedPart-originalPart)*Math.Sign(quantity);
            }
            if(quantity>0)
            {
                var policy=first.Policy ?? LastPurchaseBookPolicy.KeepAcquisitionValue;
                if(first.Policy is null && method==CostMethod.LastPurchase && purchase)
                {
                    // Infer only when the recorded receipt distinguishes the policies.
                    var priorDifference=StockCalculation.Round(beforeSource.Quantity*StockCalculation.Round(value/quantity,8),precision)-beforeSource.BookValue;
                    var revisedDifference=StockCalculation.Round(adjusted.Quantity*StockCalculation.Round((value+amountDelta)/quantity,8),precision)-adjusted.BookValue;
                    if(priorDifference!=0)
                        policy=group.Any(x=>x.Quantity==0 && x.Value!=0)?LastPurchaseBookPolicy.RevalueAtReceipt:LastPurchaseBookPolicy.KeepAcquisitionValue;
                    else if(revisedDifference!=0)
                        throw new InvalidOperationException("تعذر تحديد سياسة تقييم مستند قديم. راجع سياسة آخر شراء التاريخية قبل ترحيل الرسوم.");
                }
                var baseline=StockCalculation.Receive(beforeSource,quantity,value,method,precision,purchase,policy);
                var revised=StockCalculation.Receive(adjusted,quantity,value+amountDelta,method,precision,purchase,policy);
                valuationDelta=revised.Revaluation-baseline.Revaluation;
                deltas[first.Warehouse]=new(0,delta.BookValue+amountDelta+valuationDelta,revised.Position.LastPurchasePrice-baseline.Position.LastPurchasePrice);
            }
            else
            {
                var issueMethod=first.Kind==StockDocumentKind.Transfer?CostMethod.WeightedAverage:method;
                decimal? originalReturn=first.Kind==StockDocumentKind.PurchaseReturn?-value:null;
                decimal? revisedReturn=originalReturn.HasValue?originalReturn.Value-amountDelta:null;
                var baseline=StockCalculation.Issue(beforeSource,-quantity,precision,originalReturn,issueMethod);
                var revised=StockCalculation.Issue(adjusted,-quantity,precision,revisedReturn,issueMethod);
                amountDelta=baseline.Value-revised.Value;
                valuationDelta=revised.Revaluation-baseline.Revaluation;
                deltas[first.Warehouse]=delta with {BookValue=delta.BookValue+amountDelta+valuationDelta};
            }
            decimal usedQuantity=0,usedAmount=0;
            for(var index=0;index<source.Length;index++)
            {
                var item=source[index];
                var part=StockCalculation.Allocate(Math.Abs(amountDelta),Math.Abs(quantity),Math.Abs(item.Quantity),usedQuantity,usedAmount,precision)*Math.Sign(amountDelta);
                usedQuantity+=Math.Abs(item.Quantity);usedAmount+=Math.Abs(part);
                changes[item.Id]=part;
                if(part!=0)effects.Add(new(item,part,false));
                if(first.Kind==StockDocumentKind.Transfer)
                {
                    var target=physical.Where(x=>x.Warehouse!=first.Warehouse).ElementAt(index);
                    var targetDelta=deltas.GetValueOrDefault(target.Warehouse,new(0,0,0));
                    deltas[target.Warehouse]=targetDelta with {BookValue=targetDelta.BookValue-part};
                    changes[target.Id]=-part;
                    if(part!=0)effects.Add(new(target,-part,false));
                }
            }
            if(valuationDelta!=0)effects.Add(new(first,valuationDelta,true));
            foreach(var location in group.GroupBy(x=>x.Warehouse))
            {
                var before=positions.GetValueOrDefault(location.Key,new(0,0,0));
                var last=location.LastOrDefault(x=>x.LastPrice.HasValue);
                var price=last==null?before.LastPurchasePrice:last.Quantity>0?StockCalculation.Round(last.Value/last.Quantity,8):last.LastPrice!.Value;
                positions[location.Key]=new(before.Quantity+location.Sum(x=>x.Quantity),
                    before.BookValue+location.Sum(x=>x.Value+x.ExtraValuation),price);
            }
        }
        return new(effects,deltas.ToDictionary(x=>x.Key,x=>positions[x.Key].LastPurchasePrice+x.Value.LastPurchasePrice));
    }
}
