namespace HisabTak.Application.Accounting;

public interface IFiscalPeriodService
{
    Task CloseAsync(int actorId, int periodId, CancellationToken cancellationToken = default);
    Task ReopenAsync(int actorId, int periodId, CancellationToken cancellationToken = default);
}
