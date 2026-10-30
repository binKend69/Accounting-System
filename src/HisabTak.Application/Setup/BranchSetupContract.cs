namespace HisabTak.Application.Setup;

public interface IBranchSetupService
{
    Task<int> CreateAsync(int actorId, string branchName, CancellationToken cancellationToken = default);
}
