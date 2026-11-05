using System.Text.Json;
using HisabTak.Application.Inventory;
using HisabTak.Domain.Accounting;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed partial class StockDocumentService
{
    private async Task<List<DebtBalance>> ReadDebts(int branch,CancellationToken ct)
    {
        return await Rows("""
            SELECT s.DocumentId,s.AccountId,s.CurrencyId,s.Receivable,s.Amount,s.Amount-COALESCE(t.Amount,0),s.BookValue,s.BookValue-COALESCE(t.BookValue,0)
            FROM dbo.StockDebts s JOIN dbo.StockDocuments d ON d.DocumentId=s.DocumentId
            OUTER APPLY (SELECT SUM(a.Amount) Amount,SUM(a.BookValue) BookValue FROM dbo.StockDebtAllocations a
                WHERE a.InvoiceId=s.DocumentId AND NOT EXISTS(SELECT 1 FROM dbo.JournalEntries r WHERE r.ReversesEntryId=a.JournalId AND r.PostedAt IS NOT NULL)) t
            WHERE d.BranchId=@b AND d.State=2 ORDER BY s.DocumentId
            """,r=>new DebtBalance(r.GetInt64(0),r.GetInt32(1),r.GetInt32(2),r.GetBoolean(3),r.GetDecimal(4),r.GetDecimal(5),r.GetDecimal(6),r.GetDecimal(7)),ct,("@b",branch));
    }
    public async Task<IReadOnlyList<DebtBalance>> DebtsAsync(int actor,int branchId,CancellationToken ct=default)
    {
        await authorization.DemandAsync(actor,PermissionCodes.JournalRead,ct); await Branch(actor,branchId,ct);
        return await ReadDebts(branchId,ct);
    }
    private async Task RecordDebt(StockDocument doc,List<LineEffect> effects,long journal,CancellationToken ct)
    {
        var d=doc.Draft; var total=effects.Sum(x=>x.Net+x.Tax);
        if(!IsReturn(d.Kind))
        {
            if(d.Kind==StockDocumentKind.Sale)
            {
                var limit=await db.Accounts.Where(x=>x.Id==d.CounterpartyAccountId).Select(x=>x.CreditLimit).SingleAsync(ct);
                if(limit is decimal max)
                {
                    // The current invoice has already posted within this transaction. Include existing journal and opening balances.
                    var exposure=(await Rows("""
                        SELECT COALESCE(SUM(CASE WHEN l.Side=1 THEN l.Amount ELSE -l.Amount END),0) FROM dbo.JournalLines l
                        JOIN dbo.JournalEntries h ON h.JournalEntryId=l.JournalEntryId WHERE h.PostedAt IS NOT NULL
                        AND l.AccountId=@a AND h.BranchId=@b AND l.CurrencyId=@c
                        """,r=>r.GetDecimal(0),ct,("@a",d.CounterpartyAccountId),("@b",d.BranchId),("@c",d.CurrencyId))).Single();
                    if(exposure>max) throw new InvalidOperationException("Credit limit exceeded.");
                }
            }
            await sql.ExecuteAsync("INSERT dbo.StockDebts VALUES(@i,@a,@c,@r,@v,@v)",ct,("@i",doc.Id),("@a",d.CounterpartyAccountId),("@c",d.CurrencyId),("@r",d.Kind==StockDocumentKind.Sale),("@v",total));
            return;
        }
        decimal refundable=0;
        foreach(var group in effects.GroupBy(x=>x.Invoice!.Value))
        {
            var debt=(await ReadDebts(d.BranchId,ct)).Single(x=>x.DocumentId==group.Key);
            var value=group.Sum(x=>x.Net+x.Tax);
            if(value<=0) throw new InvalidOperationException("Return amount must be positive.");
            var offset=Math.Min(value,debt.RemainingAmount);
            refundable+=value-offset;
            if(offset==0) continue;
            await sql.ExecuteAsync("""
                INSERT dbo.StockDebtAllocations(InvoiceId,SourceKey,ReturnDocumentId,JournalId,Amount,BookValue,Payload)
                VALUES(@i,NEWID(),@return,@j,@v,@v,@p)
                """,ct,("@i",group.Key),("@return",doc.Id),("@j",journal),("@v",offset),("@p",JsonSerializer.Serialize(new { doc.Id })));
        }
        // A paid invoice can still be returned. The excess is now owed in the opposite direction.
        if(refundable>0)
            await sql.ExecuteAsync("INSERT dbo.StockDebts VALUES(@i,@a,@c,@r,@v,@v)",ct,("@i",doc.Id),("@a",d.CounterpartyAccountId),
                ("@c",d.CurrencyId),("@r",d.Kind==StockDocumentKind.PurchaseReturn),("@v",refundable));
    }
    public Task<long> SettleAsync(int actor,DebtPayment payment,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.JournalPost,ct,async () =>
        {
            var invoice=await Read(payment.InvoiceId,ct); var d=invoice.Draft;
            await Branch(actor,d.BranchId,ct);
            var payload=JsonSerializer.Serialize(payment);
            var existing=await Rows("SELECT JournalId,Payload FROM dbo.StockDebtAllocations WHERE SourceKey=@k",r=>(r.GetInt64(0),r.GetString(1)),ct,("@k",payment.SourceKey));
            if(existing.Count>0)
            {
                if(existing[0].Item2!=payload) throw new InvalidOperationException("Payment source reused with different content.");
                return existing[0].Item1;
            }
            await FreeSource(payment.SourceKey,ct);
            var debt=(await ReadDebts(d.BranchId,ct)).Single(x=>x.DocumentId==invoice.Id);
            if(payment.Date<d.Date || payment.Amount<=0 || payment.Amount>debt.RemainingAmount) throw new InvalidOperationException("Invalid settlement date or amount.");
            var precision=checked((int)await sql.ScalarAsync("SELECT Precision FROM dbo.LedgerRoundingSettings WHERE BranchId=@b",ct,("@b",d.BranchId)));
            Domain.Inventory.StockCalculation.Exact(payment.Amount,precision);
            await DemandClass(payment.CashAccountId,d.BranchId,debt.CurrencyId,["CASH","BANK"],ct);
            var book=SettlementAllocation.Allocate(new(debt.OriginalAmount,debt.CurrencyId),new(debt.OriginalBookValue,debt.CurrencyId),
                new(debt.RemainingAmount,debt.CurrencyId),new(payment.Amount,debt.CurrencyId),new(debt.RemainingBookValue,debt.CurrencyId),precision);
            var v=new BaseCurrencyValuation(new(payment.Amount,debt.CurrencyId),debt.CurrencyId,1,payment.Date);
            var proposal=JournalDraft.Create(d.BranchId,new FiscalPeriod("Settlement",payment.Date,payment.Date),
                payment.Date,payment.SourceKey,[new(payment.CashAccountId,debt.Receivable?JournalSide.Debit:JournalSide.Credit,v),
                new(debt.AccountId,debt.Receivable?JournalSide.Credit:JournalSide.Debit,v)]);
            var voucher=await vouchers.SaveVoucherAsync(actor,proposal,debt.Receivable?"RECEIPT_VOUCHER":"PAYMENT_VOUCHER",ct);
            var posted=await vouchers.PostVersionAsync(actor,voucher.Id,voucher.Version,ct);
            var journal=posted.JournalId!.Value;
            await sql.ExecuteAsync("INSERT dbo.StockDebtAllocations(InvoiceId,SourceKey,JournalId,Amount,BookValue,Payload) VALUES(@i,@k,@j,@v,@b,@p)",
                ct,("@i",invoice.Id),("@k",payment.SourceKey),("@j",journal),("@v",payment.Amount),("@b",book.Amount),("@p",payload));
            await Audit(actor,"INVOICE_SETTLED","StockDocuments",invoice.Id,ct); return journal;
        });
    public Task<StockDocument> ReverseAsync(int actor,long id,DateOnly date,Guid sourceKey,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.JournalReverse,ct,async () =>
        {
            var doc=await Read(id,ct); var d=doc.Draft;
            await Branch(actor,d.BranchId,ct);
            if(doc.State==3) return doc;
            if(doc.State!=2 || date<d.Date) throw new InvalidOperationException("Invalid reversal state or date.");
            await FreeSource(sourceKey,ct);
            if(await sql.ScalarAsync("""
                SELECT COUNT(*) FROM dbo.StockDebtAllocations a WHERE a.InvoiceId=@i AND NOT EXISTS(
                    SELECT 1 FROM dbo.JournalEntries r WHERE r.ReversesEntryId=a.JournalId AND r.PostedAt IS NOT NULL)
                """,ct,("@i",id))!=0) throw new InvalidOperationException("Reverse settlements and returns before invoice reversal.");
            foreach(var position in doc.Movements.GroupBy(m=>(m.ProductId,m.WarehouseId)))
            {
                await Chronology(position.Key.ProductId,position.Key.WarehouseId,date,ct);
                if(await sql.ScalarAsync("""
                    SELECT COUNT(*) FROM dbo.StockMovements m JOIN dbo.StockDocuments d ON d.DocumentId=m.DocumentId WHERE d.State=2
                    AND m.ProductId=@p AND m.WarehouseId=@w AND m.MovementId>@last
                    """,ct,("@p",position.Key.ProductId),("@w",position.Key.WarehouseId),("@last",position.Max(m=>m.Id)))!=0)
                    throw new InvalidOperationException("Reverse later movements first to preserve historical costing.");
            }
            var reversal=await journals.ReverseDocumentAsync(actor,doc.JournalId!.Value,date,sourceKey,ct);
            await sql.ExecuteAsync("UPDATE dbo.StockDocuments SET State=3,Version=Version+1,ReversalId=@j,ReversalDate=@d WHERE DocumentId=@i",ct,("@i",id),("@j",reversal),("@d",date));
            await Audit(actor,"STOCK_DOCUMENT_REVERSED","StockDocuments",id,ct); return await Read(id,ct);
        });
    public async Task<IReadOnlyList<StockBalance>> BalancesAsync(int actor,int branchId,DateOnly asOf,CancellationToken ct=default)
    {
        await authorization.DemandAsync(actor,PermissionCodes.JournalRead,ct); await Branch(actor,branchId,ct);
        return await Rows("""
            WITH active AS (SELECT m.* FROM dbo.StockMovements m JOIN dbo.StockDocuments h ON h.DocumentId=m.DocumentId
                WHERE h.BranchId=@b AND h.State>=2 AND h.EntryDate<=@d AND (h.ReversalDate IS NULL OR h.ReversalDate>@d))
            SELECT m.ProductId,m.WarehouseId,SUM(m.Quantity),SUM(m.Value),COALESCE((SELECT TOP(1) s.LastPurchasePrice FROM active s WHERE
                s.ProductId=m.ProductId AND s.WarehouseId=m.WarehouseId AND s.LastPurchasePrice IS NOT NULL ORDER BY s.MovementId DESC),CAST(0 AS decimal(28,8)))
            FROM active m GROUP BY m.ProductId,m.WarehouseId ORDER BY m.ProductId,m.WarehouseId
            """,r=>new StockBalance(r.GetInt32(0),r.GetInt32(1),r.GetDecimal(2),r.GetDecimal(3),r.GetDecimal(4)),ct,("@b",branchId),("@d",asOf));
    }
}
