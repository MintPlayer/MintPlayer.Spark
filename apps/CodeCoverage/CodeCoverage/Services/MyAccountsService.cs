using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Services;

/// <inheritdoc cref="IMyAccountsService"/>
[Register(typeof(IMyAccountsService), ServiceLifetime.Scoped)]
public partial class MyAccountsService : IMyAccountsService
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IForgeIntegrationResolver forges;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly IWebHostEnvironment environment;

    /// <summary>
    /// Repositories are fetched for every owner in one query rather than per owner: this runs
    /// inside a RavenDB session whose request budget is 30, and the Spark query path shares that
    /// budget with everything else the request already did.
    /// </summary>
    private const int MaxRepositories = 4096;

    /// <summary>How long a non-stale read may wait before giving up and answering anyway.</summary>
    private static readonly TimeSpan NonStaleTimeout = TimeSpan.FromSeconds(5);

    public async Task<MyAccountsResult> GetAsync(
        CancellationToken cancellationToken,
        bool waitForNonStaleResults = false,
        EForgeProvider? provider = null)
    {
        var appSlug = configuration[$"GitHub:{environment.EnvironmentName}:AppSlug"];
        if (string.IsNullOrEmpty(appSlug))
            appSlug = environment.IsDevelopment() ? "coveragedevelopment" : "coverageproduction";
        var appUrl = $"https://github.com/apps/{appSlug}";

        // Fanned out across every forge the viewer is signed in to, so the account list is
        // theirs rather than one forge's. Reauth is reported if ANY forge needs it: the
        // banner asks the viewer to reconnect, and staying silent because one other forge is
        // healthy would leave rows missing with nothing explaining why.
        var owners = await forges.GetAllowedOwnerKeysAsync(cancellationToken);
        if (provider is { } scope)
        {
            // Filtered by PARSING each key rather than by prefix match: "github" is a prefix of
            // nothing else today, but a StartsWith on a provider name is the kind of comparison
            // that stops being true quietly when a forge is added.
            owners = [.. owners.Where(o =>
                ForgeOwner.TryParse(o, out var parsed) && parsed.Value.Provider == scope)];
        }

        var reauthRequired = false;
        foreach (var linked in await forges.GetLinkedProvidersAsync(cancellationToken))
        {
            // ⚠ Scoped to the asked-for forge. Reporting GitLab's dead token on the GitHub page
            // would show a "reconnect" banner above a list that is complete and correct, and the
            // only action it offers reconnects the wrong forge.
            if (provider is { } only && linked != only) continue;
            if (forges.For(linked) is not { } forge) continue;
            var visibility = await forge.GetVisibilityAsync(cancellationToken);
            if (visibility.State == EForgeCredentialState.ReauthRequired) reauthRequired = true;
        }
        if (owners.Length == 0)
            return new MyAccountsResult(appUrl, [], reauthRequired);

        var known = await session.Query<Account, Indexes.Accounts_Overview>()
            .Customize(q => { if (waitForNonStaleResults) q.WaitForNonStaleResults(NonStaleTimeout); })
            .Where(a => a.OwnerKey.In(owners))
            .ToListAsync(cancellationToken);

        // Every owner here is one the caller manages, so ListingFilter would admit all of them;
        // the disconnected ones are excluded explicitly instead, because this drives the headline
        // "Repos" and aggregate-coverage numbers, and counting repositories we can no longer reach
        // makes those numbers quietly wrong.
        var repos = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Customize(q => { if (waitForNonStaleResults) q.WaitForNonStaleResults(NonStaleTimeout); })
            .Where(r => r.OwnerKey.In(owners) && r.Connection != RepositoryConnection.Disconnected)
            .Take(MaxRepositories)
            .ToListAsync(cancellationToken);
        var reposByOwner = repos.ToLookup(r => r.OwnerKey, StringComparer.OrdinalIgnoreCase);

        var byKey = known.ToDictionary(a => a.OwnerKey, StringComparer.OrdinalIgnoreCase);

        var rows = owners
            .Select(owner =>
            {
                var ownerRepos = reposByOwner[owner].ToList();
                var covered = ownerRepos.Sum(r => r.LatestCoverage?.LinesCovered ?? 0);
                var coverable = ownerRepos.Sum(r => r.LatestCoverage?.LinesCoverable ?? 0);
                var aggregate = coverable > 0 ? Math.Round(covered * 100.0 / coverable, 1) : (double?)null;
                // ⚠️ `owner` is a provider:login KEY, which is what every lookup above is now
                // keyed by. It must not reach the row: these fields are displayed, and a user whose
                // account page called them "github:pieterjan" is the symptom a test caught here.
                var isOwnerKey = ForgeOwner.TryParse(owner, out var parsed);
                var displayLogin = isOwnerKey ? parsed!.Value.Login : owner;
                // The canonical provider spelling, for the row's link. A key that does not parse
                // cannot be attributed to a forge, and an empty provider makes the renderer draw
                // plain text rather than a link into the wrong forge's namespace.
                var provider = isOwnerKey ? parsed!.Value.Provider.ToCanonicalString() : string.Empty;

                return byKey.TryGetValue(owner, out var account)
                    // ⚠ `InstallationId is not null` is GitHub's answer to "is this account
                    // connected", evaluated here in the neutral layer because there is no per-forge
                    // predicate yet. A second forge cannot answer it — it has no installation — so
                    // this is the account-level half of the connection-state gap M8 records, and it
                    // moves onto the forge seam with the repository-level half, not before.
                    ? new MyAccountRow(owner, account.Login, provider, account.Type, account.AvatarUrl,
                        ownerRepos.Count, aggregate, account.InstallationId is not null)
                    : new MyAccountRow(owner, displayLogin, provider, "User", null, ownerRepos.Count, aggregate, false);
            })
            .OrderBy(a => a.Login, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MyAccountsResult(appUrl, rows, reauthRequired);
    }
}
