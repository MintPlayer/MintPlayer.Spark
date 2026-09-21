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

    public async Task<MyAccountsResult> GetAsync(CancellationToken cancellationToken, bool waitForNonStaleResults = false)
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
        var reauthRequired = false;
        foreach (var provider in await forges.GetLinkedProvidersAsync(cancellationToken))
        {
            if (forges.For(provider) is not { } forge) continue;
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
                var displayLogin = ForgeOwner.TryParse(owner, out var parsed) ? parsed.Value.Login : owner;

                return byKey.TryGetValue(owner, out var account)
                    ? new MyAccountRow(account.Login, account.Login, account.Type, account.AvatarUrl,
                        ownerRepos.Count, aggregate, account.InstallationId is not null)
                    : new MyAccountRow(displayLogin, displayLogin, "User", null, ownerRepos.Count, aggregate, false);
            })
            .OrderBy(a => a.Login, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MyAccountsResult(appUrl, rows, reauthRequired);
    }
}
