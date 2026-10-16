using System.Text.Json;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using Microsoft.AspNetCore.Mvc;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace CodeCoverage.Controllers;

// The dependency updater's read side (dependency-updates PRD §8, §13.8 N9): the account's fleet view and the repository's
// panel. Read like the rest of /api/browse: the repositories are exactly those GetAccountRepos lists for this viewer
// (#453), every document is loaded by an id derived from one of them, and nothing of a repository outside that list is
// read, so nothing of it can show — the account's red versions included, which DependencyFleetView filters to the list.
public partial class BrowseController
{
    /// <summary>The account's "Dependency updates" view: per repository, per package, and readiness per platform major.</summary>
    [HttpGet("accounts/{provider}/{login}/dependency-updates")]
    public async Task<ActionResult<DependencyFleetResponse>> GetDependencyFleet(string provider, string login, CancellationToken cancellationToken)
    {
        var listing = await scope.ListAccountAsync(provider, login, cancellationToken);
        if (listing is null) return Refuse();
        var visible = listing.Repositories.Where(r => r.Id is not null).ToList();
        var account = visible.FirstOrDefault(r => r.Account is not null)?.Account is { } accountId
            ? await session.LoadAsync<Account>(accountId, cancellationToken)
            : await session.Query<Account, Indexes.Accounts_Overview>().Where(a => a.OwnerKey == listing.OwnerKey).FirstOrDefaultAsync(cancellationToken);
        var input = await DependencyInputAsync(visible, account, cancellationToken);
        return Ok(DependencyFleetView.Build(input));
    }

    /// <summary>The repository's dependency-update status: the rolling pull request, the last run, the next slot, holds.</summary>
    [HttpGet("repos/{provider}/{owner}/{name}/dependency-updates")]
    public async Task<ActionResult<DependencyRepoView>> GetRepoDependencyUpdates(string provider, string owner, string name, CancellationToken cancellationToken)
    {
        var repository = await scope.ResolveAsync(provider, owner, name, cancellationToken);
        if (repository?.Id is null) return Refuse();
        var account = repository.Account is { } accountId ? await session.LoadAsync<Account>(accountId, cancellationToken) : null;
        var input = await DependencyInputAsync([repository], account, cancellationToken);
        return Ok(DependencyFleetView.Repo(input, repository));
    }

    private async Task<DependencyFleetInput> DependencyInputAsync(IReadOnlyList<Repository> visible, Account? account, CancellationToken cancellationToken)
    {
        var ids = visible.Select(r => r.Id!).ToList();
        if (ids.Count == 0)
            return new DependencyFleetInput { Repositories = [], Account = account, Now = DateTimeOffset.UtcNow };

        // Every document by an id derived from a listed repository: one round trip each, no query, no RQL.
        var runs = await session.LoadAsync<DependencyUpdateRun>(ids.Select(DependencyUpdateRun.ForRepository), cancellationToken);
        var states = await session.LoadAsync<DependencyUpdateState>(ids.Select(DependencyUpdateState.ForRepository), cancellationToken);
        var manifests = await session.LoadAsync<RepositoryManifest>(ids.Select(RepositoryManifest.ForRepository), cancellationToken);
        // The run data holds the lookup report too (large); only the report is read, as a server-side projection.
        var dataIds = ids.Select(DependencyUpdateRunData.ForRepository).ToList();
        var reports = await session.Query<DependencyUpdateRunData>()
            .Where(d => d.Id.In(dataIds))
            .Select(d => new ReportOnly { Id = d.Id, ReportJson = d.ReportJson })
            .ToListAsync(cancellationToken);
        var fleet = account?.Id is { } accountId ? await session.LoadAsync<DependencyFleetState>(DependencyFleetState.ForAccount(accountId), cancellationToken) : null;

        var byData = reports.Where(r => r.Id is not null && r.ReportJson is not null).ToDictionary(r => r.Id!, r => r.ReportJson!, StringComparer.OrdinalIgnoreCase);
        var reportByRepo = new Dictionary<string, DependencyRunReport>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!byData.TryGetValue(DependencyUpdateRunData.ForRepository(id), out var json))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<DependencyRunReport>(json, JsonSerializerOptions.Web) is { } report)
                    reportByRepo[id] = report;
            }
            catch (JsonException)
            {
                // A report the reader cannot read is shown as no report, never as an error page.
            }
        }

        return new DependencyFleetInput
        {
            Repositories = visible,
            Account = account,
            Runs = Pick(ids, runs, DependencyUpdateRun.ForRepository),
            States = Pick(ids, states, DependencyUpdateState.ForRepository),
            Manifests = Pick(ids, manifests, RepositoryManifest.ForRepository),
            Reports = reportByRepo,
            Fleet = fleet,
            Now = DateTimeOffset.UtcNow,
        };
    }

    private static Dictionary<string, T> Pick<T>(IEnumerable<string> ids, Dictionary<string, T> loaded, Func<string, string> idOf) where T : class
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (loaded.GetValueOrDefault(idOf(id)) is { } document)
                result[id] = document;
        return result;
    }

    private sealed class ReportOnly
    {
        public string? Id { get; set; }
        public string? ReportJson { get; set; }
    }
}
