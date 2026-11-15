namespace HisabTak.Application.Inventory;

public sealed record CouponDefinition(string Code, int CurrencyId, bool Percentage, decimal Value,
    decimal MinimumAmount, DateOnly StartsOn, DateOnly EndsOn, int MaximumUses, int? CustomerAccountId = null);
public sealed record CouponInfo(long Id, CouponDefinition Definition, bool Enabled, int Used);
public interface ICouponService
{
    Task<long> CreateCouponAsync(int actor, int branch, CouponDefinition definition, CancellationToken ct = default);
    Task<IReadOnlyList<CouponInfo>> CouponsAsync(int actor, int branch, CancellationToken ct = default);
    Task DisableCouponAsync(int actor, int branch, long id, CancellationToken ct = default);
    Task<StockDocumentDraft> ApplyCouponAsync(int actor, StockDocumentDraft draft, string code, CancellationToken ct = default);
}

public sealed class CouponException(string message) : InvalidOperationException(message);
