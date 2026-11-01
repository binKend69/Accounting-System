namespace HisabTak.Application.Accounting;

public interface IFiscalPeriodService
{
    Task<int> CreateAsync(int actorId, string name, DateOnly start, DateOnly end, CancellationToken cancellationToken = default);
    Task CloseAsync(int actorId, int periodId, CancellationToken cancellationToken = default);
    Task ReopenAsync(int actorId, int periodId, CancellationToken cancellationToken = default);
}
