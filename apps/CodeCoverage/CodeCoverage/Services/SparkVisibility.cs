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
    /// Asks the forge services for the viewer's owners, as <c>provider:login</c> KEYS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>Every consumer must compare these against an <c>OwnerKey</c>, never a bare
    /// <c>OwnerLogin</c> or <c>Login</c>.</b> <c>"acme"</c> is not <c>"github:acme"</c>, so a bare
    /// login matches nothing at all.
    /// </para>
    /// <para>
    /// This remark used to say the opposite — that the set was flattened to bare logins, pending the
    /// re-key migration. That migration ran in #422 and this method changed with it; the remark did
    /// not, and four call sites were written against it. Token creation, repository-scoped token
    /// saves, token revocation and delete-data were all refused for every user until 2026-09-21.
    /// Every one failed <em>closed</em>, which is why nothing alerted and no test caught it — the
    /// feature simply stopped working.
    /// </para>
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
