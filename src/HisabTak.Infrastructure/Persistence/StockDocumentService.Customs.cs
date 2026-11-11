using HisabTak.Application.Accounting;
using HisabTak.Application.Inventory;
using HisabTak.Domain.Accounting;
using HisabTak.Domain.Inventory;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed partial class StockDocumentService
{
    private Task<List<CustomsFeeRule>> FeeRules(int branch, CancellationToken ct) =>
        Rows("SELECT AccountId,Treatment FROM dbo.CustomsFeeRules WHERE BranchId=@b", r =>
            new CustomsFeeRule(r.GetInt32(0),(FeeTreatment)r.GetInt32(1)), ct, ("@b",branch));

    public Task ConfigureFeeAsync(int actor, int branch, int account, FeeTreatment treatment, CancellationToken ct = default) =>
        Write(actor,PermissionCodes.AccountManage,ct,async () =>
        {
            await Branch(actor,branch,ct);
            if (!Enum.IsDefined(treatment) || !await db.Accounts.AnyAsync(x =>
                    x.Id == account && x.BranchId == branch && x.IsActive && !x.IsSuspended,ct))
                throw new ArgumentException("Invalid customs fee account.");
            await sql.ExecuteAsync("""
                UPDATE dbo.CustomsFeeRules SET Treatment=@t WHERE BranchId=@b AND AccountId=@a;
                IF @@ROWCOUNT=0 INSERT dbo.CustomsFeeRules VALUES(@b,@a,@t);
                """,ct,("@b",branch),("@a",account),("@t",(int)treatment));
            await Audit(actor,"CUSTOMS_FEE_CONFIGURED","Accounts",account,ct);
            return true;
        });

    private async Task PostClearance(StockDocument doc, InventoryAccounts accounts, int precision, int currencyPrecision,
        int baseId, List<JournalLineDefinition> journal, CancellationToken ct)
    {
        var d = doc.Draft;
        var declaration = d.Customs!;
        var rules = (await FeeRules(d.BranchId,ct)).ToDictionary(r => r.AccountId);
        if (declaration.Charges.Count == 0 || declaration.Charges.Any(f => !rules.TryGetValue(f.AccountId,out var rule) || f.Treatment != rule.Treatment))
            throw new InvalidOperationException("Review customs fee rules.");
        await DemandClass(d.CounterpartyAccountId,d.BranchId,d.CurrencyId,["CASH","BANK"],ct);
        void Add(int account, decimal amount, JournalSide side, bool foreign)
        {
            if (amount > 0) journal.Add(new(account,side,new(new(amount,foreign ? d.CurrencyId : baseId),baseId,foreign ? d.RateToBase : 1,d.Date)));
        }

        StockDocument? purchase = null;
        if (declaration.PurchaseId is long id)
        {
            purchase = await Read(id,ct);
            if (purchase.State != 2 || purchase.Draft.Kind != StockDocumentKind.Purchase || purchase.Draft.BranchId != d.BranchId ||
                purchase.Draft.WarehouseId != d.WarehouseId || purchase.Draft.Date > d.Date ||
                purchase.Draft.Lines.Count != d.Lines.Count ||
                !purchase.Draft.Lines.SequenceEqual(d.Lines.Select(x => x with { ShippingWeight = 0 })))
                throw new InvalidOperationException("Invalid clearance purchase.");
        }
        decimal[] landed = new decimal[d.Lines.Count];
        foreach (var fee in declaration.Charges)
        {
            StockCalculation.Exact(fee.Amount,currencyPrecision);
            if (fee.AccountId == d.CounterpartyAccountId) throw new InvalidOperationException("Fee account cannot be its payment account.");
            if (fee.Treatment is FeeTreatment.InventoryByValue or FeeTreatment.InventoryByWeight)
            {
                if (purchase == null || declaration.DeclarationType != 1) throw new InvalidOperationException("Capitalized fees require an import purchase.");
                var basis = fee.Treatment == FeeTreatment.InventoryByWeight ? d.Lines.Select(l => l.ShippingWeight).ToArray() :
                    d.Lines.Select((_,i) => purchase.Movements.Where(m => m.LineNumber == i).Sum(m => m.Net)).ToArray();
                if (basis.Any(w => w < 0 || fee.Treatment == FeeTreatment.InventoryByWeight && w == 0) || basis.Sum() <= 0)
                    throw new InvalidOperationException("Missing clearance allocation basis.");
                var total = StockCalculation.Round(fee.Amount*d.RateToBase,precision);
                decimal usedBasis = 0, usedAmount = 0;
                var totalBasis = basis.Sum();
                for (var i=0; i<basis.Length; i++)
                {
                    if (basis[i] == 0) continue;
                    var part = StockCalculation.Allocate(total,totalBasis,basis[i],usedBasis,usedAmount,precision);
                    landed[i] += part;
                    usedBasis += basis[i];
                    usedAmount += part;
                }
                Add(accounts.StockAccountId,total,JournalSide.Debit,false);
            }
            else
            {
                if (fee.AccountId == accounts.StockAccountId || fee.Treatment == FeeTreatment.RecoverableTax && fee.AccountId != accounts.InputTaxAccountId)
                    throw new InvalidOperationException("Check the expense or recoverable tax account.");
                var currency = await db.Accounts.Where(x=>x.Id==fee.AccountId && x.IsActive && !x.IsSuspended).Select(x=>x.CurrencyId).SingleAsync(ct);
                bool foreign = currency == d.CurrencyId;
                if (!foreign && currency != baseId) throw new InvalidOperationException("Fee account currency mismatch.");
                Add(fee.AccountId,foreign ? fee.Amount : StockCalculation.Round(fee.Amount*d.RateToBase,precision),JournalSide.Debit,foreign);
            }
        }
        Add(d.CounterpartyAccountId,declaration.Charges.Sum(f => f.Amount),JournalSide.Credit,true);

        if (purchase == null) return;
        var receipts = purchase.Movements.Where(m => m.Quantity > 0).ToDictionary(m => m.LineNumber);
        foreach (var group in receipts.Values.GroupBy(m => m.ProductId))
        {
            if (!group.Any(m => landed[m.LineNumber] > 0)) continue;
            var latest = group.MaxBy(m => m.Id)!;
            await Chronology(group.Key,d.WarehouseId,d.Date,ct);
            // A later issue consumed historical cost. It requires a separate cost adjustment across sold and remaining units.
            if (await sql.ScalarAsync("""
                SELECT COUNT_BIG(*) FROM dbo.StockMovements m JOIN dbo.StockDocuments h ON h.DocumentId=m.DocumentId
                WHERE m.ProductId=@p AND m.WarehouseId=@w AND h.State=2 AND m.MovementId>@last AND h.DocumentId<>@purchase
                AND (h.Kind<>11 OR TRY_CONVERT(bigint,JSON_VALUE(h.Payload,'$.Customs.PurchaseId'))<>@purchase)
                """,ct,("@p",group.Key),("@w",d.WarehouseId),("@last",latest.Id),("@purchase",purchase.Id)) != 0)
                throw new InvalidOperationException("Later movements require a landed cost adjustment.");
            var position = await Position(group.Key,d.WarehouseId,ct);
            var lastPrice = position.LastPurchasePrice + StockCalculation.Round(landed[latest.LineNumber]/latest.Quantity,8);
            foreach (var movement in group)
            {
                if (landed[movement.LineNumber] == 0) continue;
                await AddMovement(doc.Id,movement.LineNumber,group.Key,d.WarehouseId,movement.BatchId,0,landed[movement.LineNumber],0,0,
                    movement.Id,movement.Id==latest.Id ? lastPrice : null,ct);
            }
            var method=(CostMethod)await sql.ScalarAsync("SELECT CostMethod FROM dbo.StockProducts WHERE ProductId=@p",ct,("@p",group.Key));
            if (method == CostMethod.LastPurchase && accounts.BookPolicy == LastPurchaseBookPolicy.RevalueAtReceipt)
            {
                var difference = StockCalculation.Round(position.Quantity*lastPrice,precision) - position.BookValue - group.Sum(m=>landed[m.LineNumber]);
                if (difference == 0) continue;
                await AddMovement(doc.Id,latest.LineNumber,group.Key,d.WarehouseId,null,0,difference,0,0,null,null,ct);
                Add(accounts.StockAccountId,Math.Abs(difference),difference > 0 ? JournalSide.Debit : JournalSide.Credit,false);
                Add(accounts.AdjustmentAccountId,Math.Abs(difference),difference > 0 ? JournalSide.Credit : JournalSide.Debit,false);
            }
        }
    }
}
