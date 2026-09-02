using System.Linq.Expressions;
using CodeCoverage.Entities;
using CodeCoverage.Indexes;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Actions;

/// <summary>
/// A commit is visible iff its repository is. Commits carry no owner/privacy
/// fields, so the filter pushes down as an IN over the viewer's visible
/// repository ids (one memoized Raven query per request).
/// </summary>
public partial class CommitActions : DefaultPersistentObjectActions<Commit>
{
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession session;

    public override async Task<Expression<Func<Commit, bool>>?> GetRowFilterAsync(string action)
    {
        // Raven's .In() — see RepositoryActions.GetRowFilterAsync for why not Contains.
        var repoIds = await visibility.GetVisibleRepositoryIdsAsync();
        return c => c.Repository!.In(repoIds);
    }

    public override IReadOnlyCollection<string>? GetDefaultIncludes() => [nameof(Commit.Repository)];

    /// <summary>
    /// Custom query: commits of a repository, parent-scoped. Source:
    /// "Custom.Repository_Commits". A Custom.* source because Database.*
    /// queries drop parentId upstream (Spark#242).
    /// </summary>
    public async Task<IQueryable<Commit>> Repository_Commits(CustomQueryArgs args)
    {
        args.EnsureParent("Repository");

        // Ordered through the index, whose AuthoredAt field is coalesced with
        // FirstSeenAtUtc — upload-only commits (no webhook timestamp) land in
        // chronological place instead of clustering at one end.
        var commits = await session.Query<Commits_ByRepository.Result, Commits_ByRepository>()
            .Customize(x => x.NoTracking())
            .Where(r => r.Repository == args.Parent!.Id)
            .OrderByDescending(r => r.AuthoredAt)
            .OfType<Commit>()
            .ToListAsync();

        // In-memory from here: the framework materializes every custom query in
        // full before paging anyway, so this costs nothing extra — but it means
        // the query's sortColumns may name computed properties (Date). If this
        // ever returns a Raven queryable again, the sort must move back to an
        // indexed field. The Δ columns are persisted on the documents (stamped
        // by the assembler against the git parent and the default branch), so
        // nothing here depends on neighbouring rows any more.
        return commits.AsQueryable();
    }
}
