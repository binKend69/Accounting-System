namespace HisabTak.Application.Setup;

public interface ICompanySetupService
{
    Task InitializeAsync(int actorId, string companyName, string branchName, CancellationToken cancellationToken = default);
}
