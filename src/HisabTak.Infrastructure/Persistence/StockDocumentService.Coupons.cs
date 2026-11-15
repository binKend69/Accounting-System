using HisabTak.Application.Inventory;
using HisabTak.Domain.Inventory;
using HisabTak.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace HisabTak.Infrastructure.Persistence;

public sealed partial class StockDocumentService : ICouponService
{
    private static string CouponCode(string code)
    {
        var value=code.Trim().ToUpperInvariant();
        if(value.Length is <1 or >40 || value.Any(c=>!char.IsAsciiLetterOrDigit(c) && c!='-' && c!='_'))
            throw new ArgumentException("رمز الكوبون: حروف إنجليزية وأرقام وشرطة، حتى 40 حرفًا.");
        return value;
    }
    public Task<long> CreateCouponAsync(int actor,int branch,CouponDefinition definition,CancellationToken ct=default) =>
        Write(actor,PermissionCodes.SetupManage,ct,async()=>
        {
            await Branch(actor,branch,ct);
            var d=definition with {Code=CouponCode(definition.Code)};
            if(d.Value<=0 || d.Percentage && d.Value>=100 || d.MinimumAmount<0 || d.MaximumUses<=0 || d.StartsOn>d.EndsOn ||
                !await db.Currencies.AnyAsync(x=>x.Id==d.CurrencyId && x.IsActive,ct))
                throw new ArgumentException("تحقق من قيمة الكوبون وعملته وتواريخه وحد الاستخدام؛ النسبة أكبر من صفر وأقل من 100.");
            StockCalculation.Exact(d.Value,8);StockCalculation.Exact(d.MinimumAmount,8);
            if(d.CustomerAccountId is int customer) await DemandClass(customer,branch,d.CurrencyId,["CUSTOMER"],ct);
            var id=await sql.ScalarAsync("""
                INSERT dbo.SalesCoupons(BranchId,Code,CurrencyId,Percentage,Value,MinimumAmount,StartsOn,EndsOn,MaximumUses,CustomerAccountId)
                VALUES(@b,@c,@currency,@percent,@value,@min,@start,@end,@max,@customer); SELECT CAST(SCOPE_IDENTITY() AS bigint)
                """,ct,("@b",branch),("@c",d.Code),("@currency",d.CurrencyId),("@percent",d.Percentage),("@value",d.Value),
                ("@min",d.MinimumAmount),("@start",d.StartsOn),("@end",d.EndsOn),("@max",d.MaximumUses),("@customer",d.CustomerAccountId));
            await Audit(actor,"COUPON_CREATED","SalesCoupons",id,ct);return id;
        });
    public async Task<IReadOnlyList<CouponInfo>> CouponsAsync(int actor,int branch,CancellationToken ct=default)
    {
        await authorization.DemandAsync(actor,PermissionCodes.JournalRead,ct);await Branch(actor,branch,ct);
        return await ReadCoupons(branch,ct);
    }
    private Task<List<CouponInfo>> ReadCoupons(int branch,CancellationToken ct)=>Rows("""
        SELECT c.CouponId,c.Code,c.CurrencyId,c.Percentage,c.Value,c.MinimumAmount,c.StartsOn,c.EndsOn,c.MaximumUses,c.CustomerAccountId,c.Enabled,
        (SELECT COUNT(*) FROM dbo.CouponRedemptions u WHERE u.CouponId=c.CouponId)
        FROM dbo.SalesCoupons c WHERE c.BranchId=@b ORDER BY c.CouponId DESC
        """,r=>new CouponInfo(r.GetInt64(0),new(r.GetString(1),r.GetInt32(2),r.GetBoolean(3),r.GetDecimal(4),r.GetDecimal(5),
            DateOnly.FromDateTime(r.GetDateTime(6)),DateOnly.FromDateTime(r.GetDateTime(7)),r.GetInt32(8),r.IsDBNull(9)?null:r.GetInt32(9)),r.GetBoolean(10),r.GetInt32(11)),ct,("@b",branch));
    public Task DisableCouponAsync(int actor,int branch,long id,CancellationToken ct=default)=>
        Write(actor,PermissionCodes.SetupManage,ct,async()=>
        {
            await Branch(actor,branch,ct);
            if(!(await ReadCoupons(branch,ct)).Any(x=>x.Id==id))throw new ArgumentException("الكوبون غير موجود في الفرع.");
            await sql.ExecuteAsync("UPDATE dbo.SalesCoupons SET Enabled=0 WHERE CouponId=@i",ct,("@i",id));
            await Audit(actor,"COUPON_DISABLED","SalesCoupons",id,ct);return true;
        });
    public Task<StockDocumentDraft> ApplyCouponAsync(int actor,StockDocumentDraft draft,string code,CancellationToken ct=default)=>
        Write(actor,PermissionCodes.JournalPost,ct,async()=>
        {
            await Branch(actor,draft.BranchId,ct);
            if(draft.CouponCode!=null || draft.Lines.Any(x=>x.Discount!=0))
                throw new CouponException("أزل الكوبون أو الخصم اليدوي قبل تطبيق كوبون جديد؛ لا يسمح بالجمع.");
            await Validate(actor,draft,ct);
            var (result,_)=await CouponDraft(draft with {CouponCode=CouponCode(code)},ct);
            return result;
        });
    private async Task<(StockDocumentDraft Draft,long Id)> CouponDraft(StockDocumentDraft draft,CancellationToken ct)
    {
        if(draft.Kind!=StockDocumentKind.Sale || draft.Lines.Count==0)
            throw new CouponException("الكوبون مخصص لفاتورة بيع تحتوي على أصناف.");
        var code=CouponCode(draft.CouponCode!);
        var coupon=(await ReadCoupons(draft.BranchId,ct)).SingleOrDefault(x=>x.Definition.Code==code)
            ?? throw new CouponException("الكوبون غير موجود في الفرع.");
        var d=coupon.Definition;
        var today=(await Rows("SELECT CONVERT(date,SYSDATETIME())",r=>DateOnly.FromDateTime(r.GetDateTime(0)),ct)).Single();
        if(!coupon.Enabled || coupon.Used>=d.MaximumUses || draft.CurrencyId!=d.CurrencyId ||
            draft.Date<d.StartsOn || draft.Date>d.EndsOn || today<d.StartsOn || today>d.EndsOn ||
            d.CustomerAccountId.HasValue && draft.CounterpartyAccountId!=d.CustomerAccountId)
            throw new CouponException("الكوبون معطل أو منتهي أو غير مستحق أو بلغ حد الاستخدام.");
        var gross=draft.Lines.Select(x=>checked(x.Quantity*x.UnitPrice)).ToArray();
        foreach(var amount in gross) StockCalculation.Exact(amount,8);
        var total=gross.Sum();
        if(total<=0 || total<d.MinimumAmount)throw new CouponException("قيمة الفاتورة أقل من الحد الأدنى للكوبون.");
        var config=(await Rows("SELECT BaseCurrencyId,Precision FROM dbo.LedgerRoundingSettings WHERE BranchId=@b",r=>(r.GetInt32(0),Convert.ToInt32(r.GetValue(1))),ct,("@b",draft.BranchId))).Single();
        var precision=await CurrencyPrecision(draft.CurrencyId,config.Item1,config.Item2,ct);
        var discount=StockCalculation.Round(d.Percentage?total*d.Value/100:d.Value,precision);
        if(discount<=0 || discount>=total)throw new CouponException("خصم الكوبون يجب أن يكون أكبر من صفر وأقل من قيمة الفاتورة.");
        decimal usedBasis=0,usedAmount=0;
        var lines=draft.Lines.Select((line,i)=>
        {
            var part=gross[i]==0?0:StockCalculation.Allocate(discount,total,gross[i],usedBasis,usedAmount,8);
            usedBasis+=gross[i];usedAmount+=part;return line with {Discount=part};
        }).ToArray();
        return (draft with {CouponCode=d.Code,Lines=lines},coupon.Id);
    }
    private async Task ValidateCoupon(StockDocumentDraft draft,CancellationToken ct)
    {
        if(draft.CouponCode==null)return;
        var (expected,_)=await CouponDraft(draft,ct);
        if(draft.CouponCode!=expected.CouponCode || !draft.Lines.SequenceEqual(expected.Lines))
            throw new CouponException("تغيرت سطور الفاتورة أو الخصم. أزل الكوبون ثم أعد تطبيقه؛ لا يسمح بخصم يدوي إضافي.");
    }
    private async Task RedeemCoupon(StockDocument doc,CancellationToken ct)
    {
        if(doc.Draft.CouponCode==null)return;
        var (_,id)=await CouponDraft(doc.Draft,ct);
        await sql.ExecuteAsync("INSERT dbo.CouponRedemptions(CouponId,DocumentId) VALUES(@c,@d)",ct,("@c",id),("@d",doc.Id));
    }
}
