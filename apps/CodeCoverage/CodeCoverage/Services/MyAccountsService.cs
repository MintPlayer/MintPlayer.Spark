using CodeCoverage.Entities;
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
    [Inject] private readonly IGitHubAccessService gitHubAccess;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly IWebHostEnvironment environment;

    /// <summary>
    /// Repositories are fetched for every owner in one query rather than per owner: this runs
    /// inside a RavenDB session whose request budget is 30, and the Spark query path shares that
    /// budget with everything else the request already did.
    /// </summary>
    private const int MaxRepositories = 4096;

    public async Task<MyAccountsResult> GetAsync(CancellationToken cancellationToken)
    {
        var appSlug = configuration[$"GitHub:{environment.EnvironmentName}:AppSlug"];
        if (string.IsNullOrEmpty(appSlug))
            appSlug = environment.IsDevelopment() ? "coveragedevelopment" : "coverageproduction";
        var appUrl = $"https://github.com/apps/{appSlug}";

        var visibility = await gitHubAccess.GetVisibilityAsync(cancellationToken);
        var owners = visibility.Owners;
        var reauthRequired = visibility.TokenState == GitHubTokenState.ReauthRequired;
        if (owners.Length == 0)
            return new MyAccountsResult(appUrl, [], reauthRequired);

        var known = await session.Query<Account, Indexes.Accounts_Overview>()
            .Where(a => a.Login.In(owners))
            .ToListAsync(cancellationToken);

        var repos = await session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.OwnerLogin.In(owners))
            .Take(MaxRepositories)
            .ToListAsync(cancellationToken);
        var reposByOwner = repos.ToLookup(r => r.OwnerLogin, StringComparer.OrdinalIgnoreCase);

        var byLogin = known.ToDictionary(a => a.Login, StringComparer.OrdinalIgnoreCase);

        var rows = owners
            .Select(owner =>
            {
                var ownerRepos = reposByOwner[owner].ToList();
                var covered = ownerRepos.Sum(r => r.LatestCoverage?.LinesCovered ?? 0);
                var coverable = ownerRepos.Sum(r => r.LatestCoverage?.LinesCoverable ?? 0);
                var aggregate = coverable > 0 ? Math.Round(covered * 100.0 / coverable, 1) : (double?)null;
                return byLogin.TryGetValue(owner, out var account)
                    ? new MyAccountRow(account.Login, account.Login, account.Type, account.AvatarUrl,
                        ownerRepos.Count, aggregate, account.InstallationId is not null)
                    : new MyAccountRow(owner, owner, "User", null, ownerRepos.Count, aggregate, false);
            })
            .OrderBy(a => a.Login, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MyAccountsResult(appUrl, rows, reauthRequired);
    }
}
