using CodeCoverage.Entities;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

[Register(typeof(ISparkVisibility), ServiceLifetime.Scoped)]
public partial class SparkVisibility : ISparkVisibility
{
    [Inject] private readonly IGitHubAccessService gitHubAccess;
    [Inject] private readonly IAsyncDocumentSession session;

    // Task-memoized so concurrent awaiters within the request share one computation.
    private Task<string[]>? owners;
    private Task<string[]>? visibleRepositoryIds;

    public Task<string[]> GetAllowedOwnersAsync()
        => owners ??= gitHubAccess.GetAllowedOwnersAsync();

    public Task<string[]> GetVisibleRepositoryIdsAsync()
        => visibleRepositoryIds ??= QueryVisibleRepositoryIdsAsync();

    public async Task<bool> CanManageOwnerAsync(string ownerLogin)
        => (await GetAllowedOwnersAsync()).Contains(ownerLogin, StringComparer.OrdinalIgnoreCase);

    private async Task<string[]> QueryVisibleRepositoryIdsAsync()
    {
        var allowed = await GetAllowedOwnersAsync();
        var ids = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(RepositoryVisibility.Filter(allowed))
            .Select(r => r.Id)
            .ToListAsync();
        return [.. ids.OfType<string>()];
    }
}
