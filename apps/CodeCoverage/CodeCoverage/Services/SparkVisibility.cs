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
    [Inject] private readonly IEnumerable<IForgeRepositoryAdministration> administrations;

    // Task-memoized so concurrent awaiters within the request share one computation.
    private Task<string[]>? owners;
    private Task<string[]>? visibleRepositoryIds;
    private Task<string[]>? managedOwners;
    private Task<string[]>? administeredIds;
    private readonly Dictionary<string, Task<bool>> managesOwner = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IReadOnlySet<string>>> administeredByAccount = new(StringComparer.Ordinal);

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
    /// <remarks>
    /// <para>
    /// ⚠️ <b>This used to be membership of the owner set</b>, i.e. of <c>GET /user/installations</c>, which GitHub answers
    /// with every installation the user can reach, read access to one repository included. Every read-only member of an
    /// organization therefore managed the whole account: its settings, its Edit, its tokens, and the dependency updater's
    /// enable (D-O3). It is now the forge's own answer for the owner (R39, D-O12); the owner set stays the precondition, so
    /// nothing outside what the viewer can see costs a forge call.
    /// </para>
    /// </remarks>
    public Task<bool> CanManageOwnerAsync(string ownerKey)
    {
        var key = ownerKey.ToLowerInvariant();
        if (!managesOwner.TryGetValue(key, out var answer))
            managesOwner[key] = answer = QueryManagesOwnerAsync(ownerKey);
        return answer;
    }

    private async Task<bool> QueryManagesOwnerAsync(string ownerKey)
    {
        if (!ForgeOwner.TryParse(ownerKey, out var owner))
            return false;
        if (!(await GetAllowedOwnersAsync()).Contains(ownerKey, StringComparer.OrdinalIgnoreCase))
            return false;
        return forges.For(owner.Value.Provider) is { } forge && await forge.ManagesOwnerAsync(owner.Value);
    }

    public Task<string[]> GetManagedOwnersAsync() => managedOwners ??= QueryManagedOwnersAsync();

    private async Task<string[]> QueryManagedOwnersAsync()
    {
        var managed = new List<string>();
        foreach (var owner in await GetAllowedOwnersAsync())
            if (await CanManageOwnerAsync(owner))
                managed.Add(owner);
        return [.. managed];
    }

    public async Task<bool> CanManageRepositoryAsync(Repository repository)
    {
        if (await CanManageOwnerAsync(repository.OwnerKey))
            return true;
        // A repository admin has explicit access, so the account's installation is in the owner set: anything outside it
        // costs no forge call.
        if (repository.Id is null || repository.Account is null
            || !(await GetAllowedOwnersAsync()).Contains(repository.OwnerKey, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        return (await AdministeredInAccountAsync(repository.Provider, repository.Account)).Contains(repository.Id);
    }

    public Task<string[]> GetAdministeredRepositoryIdsAsync() => administeredIds ??= QueryAdministeredRepositoryIdsAsync();

    private async Task<string[]> QueryAdministeredRepositoryIdsAsync()
    {
        var allowed = await GetAllowedOwnersAsync();
        var managed = await GetManagedOwnersAsync();
        var unmanaged = allowed.Except(managed, StringComparer.OrdinalIgnoreCase).ToArray();
        if (unmanaged.Length == 0)
            return [];

        var accounts = await session.Query<Account, Indexes.Accounts_Overview>()
            .Where(a => a.OwnerKey.In(unmanaged))
            .ToListAsync();
        var ids = new List<string>();
        foreach (var account in accounts.Where(a => a.Id is not null))
            ids.AddRange(await AdministeredInAccountAsync(account.Provider, account.Id!));
        return [.. ids];
    }

    /// <summary>
    /// The repositories of one account the viewer administers, asked of its forge once per request (and cached there per
    /// viewer and account, with the owner set's lifetime).
    /// </summary>
    private Task<IReadOnlySet<string>> AdministeredInAccountAsync(EForgeProvider provider, string accountId)
    {
        if (!administeredByAccount.TryGetValue(accountId, out var answer))
            administeredByAccount[accountId] = answer = QueryAdministeredInAccountAsync(provider, accountId);
        return answer;
    }

    private async Task<IReadOnlySet<string>> QueryAdministeredInAccountAsync(EForgeProvider provider, string accountId)
    {
        if (administrations.FirstOrDefault(a => a.Provider == provider) is not { } administration)
            return Empty;
        if (await session.LoadAsync<Account>(accountId) is not { } account)
            return Empty;
        var repositories = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.Account == accountId)
            .Take(MaxRepositoriesPerAccount)
            .ToListAsync();
        return await administration.GetAdministeredRepositoriesAsync(account, repositories);
    }

    private const int MaxRepositoriesPerAccount = 1024;
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);

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
