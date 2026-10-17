using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Dependencies.Onboarding;

/// <summary>
/// <c>GET api/dependency-updates/onboarding</c>: the onboarding page's header (dependency-updates PRD R20, R36, D-O14). The rows
/// themselves are the Spark query <c>DependencyOnboardingRows</c>; this says only what the grid cannot.
/// </summary>
/// <param name="Login">The viewer's verified forge login (never a claim); null when there is none.</param>
/// <param name="CredentialState"><c>Ok</c>, <c>ReauthRequired</c> or <c>Unavailable</c>.</param>
/// <param name="Linked">The viewer has a sign-in of a forge that can list installations at all.</param>
/// <param name="Narrowed">The setup redirect named an installation, so the grid is narrowed to it.</param>
/// <param name="SetupAction"><c>install</c>, <c>update</c>, <c>request</c>, or null.</param>
/// <param name="Repositories">How many repositories the viewer manages here, the syncing ones included.</param>
/// <param name="Syncing">How many of them are not in this app yet (no Repository document), so not rows of the grid.</param>
public sealed record DependencyOnboardingSummary(string? Login, string CredentialState, bool Linked, bool Narrowed, string? SetupAction,
    int Repositories, int Syncing)
{
    public static DependencyOnboardingSummary From(DependencyOnboardingResponse listing) => new(
        listing.Login, listing.CredentialState, listing.Linked, listing.Narrowed, listing.SetupAction,
        listing.Repositories.Count, listing.Repositories.Count(r => r.RepositoryId is null));
}

/// <summary>
/// The onboarding page's scope (D-O14): the setup redirect's query string, carried to the Spark query as the id of its
/// virtual parent <see cref="TypeName"/>, and read here exactly as the header endpoint reads <c>Request.Query</c>.
/// </summary>
/// <remarks>
/// The raw query string rather than an installation id, so the page names no forge parameter (R32): which key narrows to
/// which installation is <see cref="IForgeSetupRedirect.Read"/>'s business. A repeated key is comma-joined, never
/// first-wins, as ASP.NET joins it for the endpoint, so a duplicated <c>installation_id</c> is malformed on both paths (D-O13).
/// The scope only ever narrows the viewer's own list; it is never trusted beyond that.
/// </remarks>
public static class DependencyOnboardingScope
{
    /// <summary>The virtual parent type of the onboarding query (App_Data/Model/DependencyOnboardingScope.json).</summary>
    public const string TypeName = "DependencyOnboardingScope";

    private static readonly IReadOnlyDictionary<string, string?> None = new Dictionary<string, string?>();

    public static IReadOnlyDictionary<string, string?> Parse(string? raw)
        => string.IsNullOrEmpty(raw)
            ? None
            : Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(raw)
                .ToDictionary(q => q.Key, q => (string?)q.Value.ToString(), StringComparer.Ordinal);
}

/// <summary>The viewer's listing, as both the header endpoint and the query read it.</summary>
/// <param name="Login">The viewer's verified forge login the list was made for (never a claim); null when there is none.</param>
/// <param name="CredentialState">Why the list may be incomplete: <c>Ok</c>, <c>ReauthRequired</c> or <c>Unavailable</c>.</param>
/// <param name="Linked">The viewer has a sign-in of a forge that can list installations at all.</param>
/// <param name="Narrowed">The list is narrowed to the installation the setup redirect named ("all my accounts →" drops it).</param>
/// <param name="SetupAction">The redirect's hint (<c>install</c>, <c>update</c>, <c>request</c>), null without one. <c>request</c>
/// means "waiting for an organization owner's approval" (R36).</param>
public sealed record DependencyOnboardingResponse(string? Login, string CredentialState, bool Linked, bool Narrowed, string? SetupAction,
    IReadOnlyList<DependencyOnboardingEntry> Repositories);

/// <param name="RepositoryId">The <c>Repository</c> document id, the id the Spark query and its custom action take; null while
/// the repository is not in this app yet (<paramref name="Syncing"/>).</param>
/// <param name="ManagedBy">Why the viewer may enable it: <c>owner</c> (manages the account: an active owner of the organization,
/// or the personal account's own user, R39) or <c>repo-admin</c> (administers this repository on the forge, R33: the top role
/// only; a read or write member of the organization is neither, D-O12). Re-checked when the action executes, never taken from here.</param>
/// <param name="Setting">The repository's own choice: <c>Inherit</c>, <c>Enabled</c>, <c>Disabled</c>.</param>
/// <param name="Enabled">The setting as it resolves, the account's default included.</param>
/// <param name="Onboarding"><c>None</c>, <c>PullRequestOpened</c>, <c>Done</c>, <c>Blocked</c>.</param>
/// <param name="Syncing">No default branch known yet (or no document): the installation's webhook is still being processed.</param>
/// <param name="OnboardingMergeQueue">R26: whether its onboarding pull request would add the merge queue, as it resolves.</param>
/// <param name="MergeQueueInFile">R26: <c>coverage.yml</c>'s merge queue as last read, null until read.</param>
/// <param name="LastRunOutcome">D-O20: the last run's outcome (<c>nothing-planned</c>, <c>writer-unavailable</c>, ...), null before the first.</param>
/// <param name="LastRunMessage">D-O20: why, in the run's own words.</param>
public sealed record DependencyOnboardingEntry(
    string? RepositoryId, string Provider, string FullName, string AccountLogin, bool IsPrivate, string ManagedBy,
    string Setting, bool Enabled, bool AccountDefault, string Onboarding, string? OnboardingNote, DependencyPullRequestLink? OnboardingPullRequest,
    bool Syncing, bool OnboardingMergeQueue, string? MergeQueueInFile, string? LastRunOutcome = null, string? LastRunMessage = null);

/// <summary>The rows of one viewer, and the execution-time check of the same (R21: shown, re-checked, never trusted).</summary>
public interface IDependencyOnboardingService
{
    /// <summary>The page's rows: the repositories the viewer manages, narrowed to the installation the setup redirect in
    /// <paramref name="query"/> names (an unknown or foreign one gives none: no existence oracle).</summary>
    Task<DependencyOnboardingResponse> ListAsync(IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken = default);

    /// <summary>
    /// For each repository, why the viewer may change its dependency updates now, or null when they may not: a manager of
    /// its account, or (asked of the forge with the App's credential, now) an administrator of the repository.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> CheckAsync(IReadOnlyList<Repository> repositories, CancellationToken cancellationToken = default);
}

[Register(typeof(IDependencyOnboardingService), ServiceLifetime.Scoped)]
public partial class DependencyOnboardingService : IDependencyOnboardingService
{
    [Inject] private readonly IEnumerable<IForgeRepositoryAdministration> administrations;
    [Inject] private readonly IEnumerable<IForgeSetupRedirect> redirects;
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The viewer manages the repository's account (<see cref="ISparkVisibility.CanManageOwnerAsync"/>, R39).</summary>
    public const string Owner = "owner";

    /// <summary>The viewer administers this one repository on its forge (R33), without managing the account.</summary>
    public const string RepoAdmin = "repo-admin";

    public async Task<DependencyOnboardingResponse> ListAsync(IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken = default)
    {
        var redirect = redirects.Select(r => r.Read(query)).FirstOrDefault(r => r is not null);
        var rows = new List<DependencyOnboardingEntry>();
        string? login = null;
        var linked = false;
        var state = EForgeCredentialState.Ok;
        foreach (var administration in administrations)
        {
            // A redirect from one forge narrows that forge to its installation and leaves the others out.
            if (redirect is not null && redirect.Provider != administration.Provider) continue;
            var listing = await administration.ListViewerRepositoriesAsync(redirect?.InstallationKey, cancellationToken);
            login ??= listing.Login;
            linked |= listing.Login is not null;
            if (listing.State != EForgeCredentialState.Ok) state = listing.State;
            if (listing.Repositories.Count == 0) continue;

            var provider = administration.Provider;
            var repositoryIds = listing.Repositories.Select(r => Repository.DocumentId(provider, r.RepositoryForgeId)).Distinct().ToList();
            var accountIds = listing.Repositories.Select(r => Account.DocumentId(provider, r.AccountForgeId)).Distinct().ToList();
            var repositories = await session.LoadAsync<Repository>(repositoryIds, cancellationToken);
            var accounts = await session.LoadAsync<Account>(accountIds, cancellationToken);
            var runs = await session.LoadAsync<DependencyUpdateRun>(repositoryIds.Select(DependencyUpdateRun.ForRepository), cancellationToken);
            var managed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var listed in listing.Repositories.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase))
            {
                var ownerKey = new ForgeOwner(provider, listed.AccountLogin).ToString();
                if (!managed.TryGetValue(ownerKey, out var isManager))
                    managed[ownerKey] = isManager = await visibility.CanManageOwnerAsync(ownerKey);
                // Filtered to what the viewer manages first; only then shown (R20).
                var reason = isManager ? Owner : listed.IsAdmin ? RepoAdmin : null;
                if (reason is null) continue;

                var id = Repository.DocumentId(provider, listed.RepositoryForgeId);
                var repository = repositories.GetValueOrDefault(id);
                var account = accounts.GetValueOrDefault(Account.DocumentId(provider, listed.AccountForgeId));
                var run = runs.GetValueOrDefault(DependencyUpdateRun.ForRepository(id));
                rows.Add(new DependencyOnboardingEntry(
                    repository?.Id, provider.ToCanonicalString(), listed.FullName, listed.AccountLogin, listed.IsPrivate, reason,
                    (repository?.DependencyUpdates ?? EDependencyUpdatesPolicy.Inherit).ToString(),
                    repository is not null && Repository.ResolveDependencyUpdates(repository, account),
                    account?.DependencyUpdates ?? false,
                    (run?.Onboarding ?? EDependencyOnboarding.None).ToString(), run?.OnboardingNote,
                    run?.OnboardingPullRequestNumber is { } number && repository is not null
                        ? new DependencyPullRequestLink(number, DependencyFleetView.PullRequestUrl(repository, number), false)
                        : null,
                    Syncing: repository?.DefaultBranch is null,
                    Repository.ResolveDependencyOnboardingMergeQueue(repository, account),
                    run?.FileMergeQueue,
                    run?.Outcome, run?.OutcomeMessage));
            }
        }
        return new DependencyOnboardingResponse(login, state.ToString(), linked, redirect is not null,
            redirect?.Action is { } action and not EForgeSetupAction.Unknown ? action.ToString().ToLowerInvariant() : null, rows);
    }

    public async Task<IReadOnlyDictionary<string, string?>> CheckAsync(IReadOnlyList<Repository> repositories, CancellationToken cancellationToken = default)
    {
        var answer = new Dictionary<string, string?>(StringComparer.Ordinal);
        var unresolved = new List<Repository>();
        foreach (var repository in repositories.Where(r => r.Id is not null))
        {
            if (await visibility.CanManageOwnerAsync(repository.OwnerKey))
                answer[repository.Id!] = Owner;
            else
            {
                answer[repository.Id!] = null;
                unresolved.Add(repository);
            }
        }

        // R21: the forge-admin reason is asked of the forge now, with the App's credential, for the viewer's verified login.
        foreach (var byForge in unresolved.GroupBy(r => r.Provider))
        {
            if (administrations.FirstOrDefault(a => a.Provider == byForge.Key) is not { } administration) continue;
            if ((await administration.GetViewerLoginAsync(cancellationToken)).Login is not { } login) continue;
            foreach (var byAccount in byForge.Where(r => r.Account is not null).GroupBy(r => r.Account!))
            {
                if (await session.LoadAsync<Account>(byAccount.Key, cancellationToken) is not { } account) continue;
                var admins = await administration.CheckAdministratorAsync(account, [.. byAccount], login, cancellationToken);
                foreach (var (id, isAdmin) in admins)
                    if (isAdmin) answer[id] = RepoAdmin;
            }
        }
        return answer;
    }
}
