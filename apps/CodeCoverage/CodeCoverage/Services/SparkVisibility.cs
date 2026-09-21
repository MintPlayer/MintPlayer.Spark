using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

[Register(typeof(ISparkVisibility), ServiceLifetime.Scoped)]
public partial class SparkVisibility : ISparkVisibility
{
    [Inject] private readonly IForgeIntegrationResolver forges;
    [Inject] private readonly IAsyncDocumentSession session;

    // Task-memoized so concurrent awaiters within the request share one computation.
    private Task<string[]>? owners;
    private Task<string[]>? visibleRepositoryIds;

    public Task<string[]> GetAllowedOwnersAsync()
        => owners ??= QueryAllowedOwnersAsync();

    /// <summary>
    /// Asks the forge services for the viewer's owners and flattens them to bare logins.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The flattening is temporary and only safe while GitHub is the sole provider.</b>
    /// Consumers still compare these strings against the unqualified <c>OwnerLogin</c> /
    /// <c>Login</c> values currently in the database, so qualifying them here would match nothing.
    /// The stored values become <c>provider:login</c> in the same migration that re-keys the
    /// documents, and at that point this method returns <see cref="ForgeOwner"/> and the
    /// <c>.Login</c> unwrapping below disappears. Until then, adding a second provider would make
    /// two identically-named accounts on different forges indistinguishable here — which is exactly
    /// why no second provider is registered yet.
    /// </remarks>
    private async Task<string[]> QueryAllowedOwnersAsync()
    {
        var providers = await forges.GetLinkedProvidersAsync();

        var all = new List<string>();
        foreach (var provider in providers)
        {
            var service = forges.For(provider);
            if (service is null)
                continue;

            var forgeOwners = await service.GetAllowedOwnersAsync();
            // ⚠️ The key, not the bare login. This is the row-filter path: a bare login here
            // unions forges, so a GitLab owner named the same as a GitHub one would inherit its
            // private repositories — and because the filter grants rather than denies, the failure
            // produces no error and no empty page.
            all.AddRange(forgeOwners.Select(owner => owner.ToString()));
        }

        return [.. all.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public Task<string[]> GetVisibleRepositoryIdsAsync()
        => visibleRepositoryIds ??= QueryVisibleRepositoryIdsAsync();

    /// <param name="ownerKey">A <c>provider:login</c> key — <see cref="Entities.Repository.OwnerKey"/>.</param>
    public async Task<bool> CanManageOwnerAsync(string ownerKey)
        => (await GetAllowedOwnersAsync()).Contains(ownerKey, StringComparer.OrdinalIgnoreCase);

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
