using System.Linq.Expressions;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Actions;

/// <summary>
/// The versioning history on the repository page (#153 M12). A run is visible iff its repository is, like a commit, and
/// security.json grants only <c>QueryRead</c>: the runs are written by the versioning endpoint, never through Spark.
/// </summary>
public partial class VersioningRunActions : DefaultPersistentObjectActions<VersioningRun>
{
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession session;

    public override async Task<Expression<Func<VersioningRun, bool>>?> GetRowFilterAsync(string action)
    {
        // Raven's .In() — see RepositoryActions.GetRowFilterAsync for why not Contains.
        var repoIds = await visibility.GetVisibleRepositoryIdsAsync();
        return r => r.Repository.In(repoIds);
    }

    /// <summary>
    /// Custom query: a repository's runs, newest first. Source: "Custom.Repository_VersioningRuns". Streamed by the
    /// repository's id prefix, so it builds no index (D-O22's rule for this app).
    /// </summary>
    public async Task<IQueryable<VersioningRun>> Repository_VersioningRuns(CustomQueryArgs args)
    {
        args.EnsureParent("Repository");
        if (await session.LoadAsync<Repository>(args.Parent!.Id) is not { } repository) return Enumerable.Empty<VersioningRun>().AsQueryable();

        var runs = new List<VersioningRun>();
        await using (var stream = await session.Advanced.StreamAsync<VersioningRun>(VersioningRun.ForRepository(repository.Provider, repository.GitHubId)))
        {
            while (await stream.MoveNextAsync())
                if (stream.Current.Document is { } run && run.Repository == repository.Id) runs.Add(run);
        }
        return runs.OrderByDescending(r => r.StartedAtUtc).AsQueryable();
    }
}
