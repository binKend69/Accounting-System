using System.Text.Json;
using HisabTak.Application.Inventory;
using HisabTak.Domain.Accounting;
using HisabTak.Domain.Security;
using HisabTak.Domain.Inventory;
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
    private sealed record ReturnOffset(long Invoice,decimal Principal,decimal Book,decimal Refund,decimal RefundBook);
    private async Task<List<ReturnOffset>> ReturnOffsets(StockDocumentDraft d,List<LineEffect> effects,int baseId,int precision,CancellationToken ct)
    {
        var result=new List<ReturnOffset>();
        var debts=await ReadDebts(d.BranchId,ct);
        foreach(var group in effects.GroupBy(x=>x.Invoice!.Value))
        {
            var debt=debts.Single(x=>x.DocumentId==group.Key);
            var total=group.Sum(x=>x.Net+x.Tax);var principal=Math.Min(total,debt.RemainingAmount);
            var book=principal==0?0:SettlementAllocation.Allocate(new(debt.OriginalAmount,debt.CurrencyId),new(debt.OriginalBookValue,baseId),
                new(debt.RemainingAmount,debt.CurrencyId),new(principal,debt.CurrencyId),new(debt.RemainingBookValue,baseId),precision).Amount;
            result.Add(new(group.Key,principal,book,total-principal,StockCalculation.Round((total-principal)*d.RateToBase,precision)));
        }
        return result;
    }
    private async Task SaveBasis(Guid source,int account,decimal amount,decimal book,byte[]? signature,List<ReturnOffset> origins,CancellationToken ct)
    {
        await sql.ExecuteAsync("INSERT dbo.StockBookBasis VALUES(@k,@a,@p,@b,@h)",ct,("@k",source),("@a",account),("@p",amount),("@b",book),("@h",signature));
        foreach(var origin in origins)
            await sql.ExecuteAsync("INSERT dbo.StockBasisOrigins VALUES(@k,@i,@a,@b,@r,@rb)",ct,("@k",source),("@i",origin.Invoice),
                ("@a",origin.Principal),("@b",origin.Book),("@r",origin.Refund),("@rb",origin.RefundBook));
    }
    private async Task RecordDebt(StockDocument doc,List<LineEffect> effects,long journal,List<ReturnOffset> offsets,int precision,CancellationToken ct)
    {
        var d=doc.Draft; var total=d.Customs?.Charges.Sum(f=>f.Amount) ?? effects.Sum(x=>x.Net+x.Tax);
        if(!IsReturn(d.Kind))
        {
            if(d.Kind==StockDocumentKind.Sale)
            {
                var limit=await db.Accounts.Where(x=>x.Id==d.CounterpartyAccountId).Select(x=>x.CreditLimit).SingleAsync(ct);
                if(limit is null)throw new InvalidOperationException("Set a customer credit limit before posting a credit sale.");
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
            await sql.ExecuteAsync("INSERT dbo.StockDebts VALUES(@i,@a,@c,@r,@v,@book)",ct,("@i",doc.Id),("@a",d.CounterpartyAccountId),("@c",d.CurrencyId),("@r",d.Kind==StockDocumentKind.Sale),("@v",total),("@book",StockCalculation.Round(total*d.RateToBase,precision)));
            return;
        }
        foreach(var offset in offsets.Where(x=>x.Principal>0))
            await sql.ExecuteAsync("""
                INSERT dbo.StockDebtAllocations(InvoiceId,SourceKey,ReturnDocumentId,JournalId,Amount,BookValue,Payload)
                VALUES(@i,NEWID(),@return,@j,@v,@book,@p)
                """,ct,("@i",offset.Invoice),("@return",doc.Id),("@j",journal),("@v",offset.Principal),("@book",offset.Book),("@p",JsonSerializer.Serialize(new { doc.Id })));
        var refund=offsets.Sum(x=>x.Refund);
        if(refund>0)
            await sql.ExecuteAsync("INSERT dbo.StockDebts VALUES(@i,@a,@c,@r,@v,@book)",ct,("@i",doc.Id),("@a",d.CounterpartyAccountId),
                ("@c",d.CurrencyId),("@r",d.Kind==StockDocumentKind.PurchaseReturn),("@v",refund),("@book",offsets.Sum(x=>x.RefundBook)));
    }
    public Task<long> SettleAsync(int actor,DebtPayment payment,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.JournalPost,ct,async () =>
        {
            var invoice=await Read(payment.InvoiceId,ct); var d=invoice.Draft;
            await Branch(actor,d.BranchId,ct);
            var payload=StockPayload.Encode(payment);
            var existing=await Rows("SELECT JournalId,Payload FROM dbo.StockDebtAllocations WHERE SourceKey=@k",r=>(r.GetInt64(0),r.GetString(1)),ct,("@k",payment.SourceKey));
            if(existing.Count>0)
            {
                if(StockPayload.Encode(JsonSerializer.Deserialize<DebtPayment>(existing[0].Item2))!=payload) throw new InvalidOperationException("Payment source reused with different content.");
                return existing[0].Item1;
            }
            await FreeSource(payment.SourceKey,ct);
            var debt=(await ReadDebts(d.BranchId,ct)).Single(x=>x.DocumentId==invoice.Id);
            if(payment.Date<d.Date || payment.Amount<=0 || payment.Amount>debt.RemainingAmount) throw new InvalidOperationException("Invalid settlement date or amount.");
            var config=(await Rows("""
                SELECT r.BaseCurrencyId,r.Precision,a.Payload FROM dbo.LedgerRoundingSettings r JOIN dbo.StockAccountSettings a ON a.BranchId=r.BranchId WHERE a.BranchId=@b
                """,r=>(r.GetInt32(0),r.GetInt32(1),JsonSerializer.Deserialize<InventoryAccounts>(r.GetString(2))!),ct,("@b",d.BranchId))).Single();
            var (baseId,precision,settings)=config;
            StockCalculation.Exact(payment.Amount,await CurrencyPrecision(debt.CurrencyId,baseId,precision,ct));
            var cashCurrency=await db.Accounts.Where(x=>x.Id==payment.CashAccountId).Select(x=>x.CurrencyId).SingleAsync(ct);
            await DemandClass(payment.CashAccountId,d.BranchId,cashCurrency,["CASH","BANK"],ct);
            var tender=payment.CashAmount??(cashCurrency==debt.CurrencyId?payment.Amount:throw new ArgumentException("Specify the actual cash amount."));
            var cashRate=payment.CashRateToBase??(cashCurrency==baseId?1:throw new ArgumentException("Specify the payment date rate."));
            StockCalculation.Exact(tender,await CurrencyPrecision(cashCurrency,baseId,precision,ct));
            var book=SettlementAllocation.Allocate(new(debt.OriginalAmount,debt.CurrencyId),new(debt.OriginalBookValue,baseId),
                new(debt.RemainingAmount,debt.CurrencyId),new(payment.Amount,debt.CurrencyId),new(debt.RemainingBookValue,baseId),precision);
            var principal=new BaseCurrencyValuation(new(payment.Amount,debt.CurrencyId),baseId,d.RateToBase,payment.Date);
            var cash=new BaseCurrencyValuation(new(tender,cashCurrency),baseId,cashRate,payment.Date);
            var exchange=new ExchangeSettlementCalculation(new(new(debt.RemainingAmount,debt.CurrencyId),baseId,d.RateToBase,payment.Date),
                principal.Original,cash.ExactBaseAmount,debt.Receivable?DebtDirection.Receivable:DebtDirection.Payable);
            var lines=new List<JournalLineDefinition>{new(payment.CashAccountId,debt.Receivable?JournalSide.Debit:JournalSide.Credit,cash),
                new(debt.AccountId,debt.Receivable?JournalSide.Credit:JournalSide.Debit,principal)};
            if(exchange.Gain.Amount>0) lines.Add(new(settings.FxGainAccountId??throw new InvalidOperationException("Configure FX gain account."),JournalSide.Credit,new(exchange.Gain,baseId,1,payment.Date)));
            if(exchange.Loss.Amount>0) lines.Add(new(settings.FxLossAccountId??throw new InvalidOperationException("Configure FX loss account."),JournalSide.Debit,new(exchange.Loss,baseId,1,payment.Date)));
            var proposal=JournalDraft.Create(d.BranchId,new FiscalPeriod("Settlement",payment.Date,payment.Date),payment.Date,payment.SourceKey,lines);
            await SaveBasis(payment.SourceKey,debt.AccountId,payment.Amount,book.Amount,JournalDocumentStrategy.SettlementSignature(proposal),[new(invoice.Id,payment.Amount,book.Amount,0,0)],ct);
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
            if(await sql.ScalarAsync("""
                SELECT COUNT(*) FROM dbo.StockMovements r JOIN dbo.StockMovements o ON r.OriginalMovementId=o.MovementId
                JOIN dbo.StockDocuments h ON h.DocumentId=r.DocumentId WHERE o.DocumentId=@i AND h.State=2
                """,ct,("@i",id))!=0)throw new InvalidOperationException("Reverse original-linked returns first.");
            foreach(var position in doc.Movements.Where(m=>m.BilledQuantity==null).GroupBy(m=>(m.ProductId,m.WarehouseId)))
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
                WHERE m.BilledQuantity IS NULL AND h.BranchId=@b AND h.State>=2 AND h.EntryDate<=@d AND (h.ReversalDate IS NULL OR h.ReversalDate>@d))
            SELECT m.ProductId,m.WarehouseId,SUM(m.Quantity),SUM(m.Value),COALESCE((SELECT TOP(1) s.LastPurchasePrice FROM active s WHERE
                s.ProductId=m.ProductId AND s.WarehouseId=m.WarehouseId AND s.LastPurchasePrice IS NOT NULL ORDER BY s.MovementId DESC),CAST(0 AS decimal(28,8)))
            FROM active m GROUP BY m.ProductId,m.WarehouseId ORDER BY m.ProductId,m.WarehouseId
            """,r=>new StockBalance(r.GetInt32(0),r.GetInt32(1),r.GetDecimal(2),r.GetDecimal(3),r.GetDecimal(4)),ct,("@b",branchId),("@d",asOf));
    }
}
