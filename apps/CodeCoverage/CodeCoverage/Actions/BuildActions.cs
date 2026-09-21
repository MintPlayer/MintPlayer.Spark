using CodeCoverage.Forge;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Actions;

/// <summary>
/// A build is visible iff its commit's repository is. Builds carry no owner
/// fields; the repo is recovered from the Commit reference's id shape
/// (Commits/{repoGitHubId}/{sha}) — not expressible as a pushdown expression,
/// so this stays the per-row predicate. The only I/O behind it is the
/// per-request memoized repo-id list, so per-row evaluation is in-memory.
/// </summary>
public partial class BuildActions : DefaultPersistentObjectActions<Build>
{
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession session;

    public override async Task<bool> IsAllowedAsync(string action, Build entity)
    {
        var repoId = RepositoryIdFromCommitId(entity.Commit);
        if (repoId is null) return false;
        var visible = await visibility.GetVisibleRepositoryIdsAsync();
        return visible.Contains(repoId, StringComparer.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// ⚠️ This used to parse <c>parts[1]</c> as a <c>long</c> directly. With the forge segment that
    /// reads <c>"github"</c>, the parse fails, this returns null, and
    /// <see cref="IsAllowedAsync"/> denies <em>every build in the system</em> — silently, because
    /// it fails closed and the grid simply goes blank. The shape-aware parse lives on
    /// <see cref="Commit.TryParseRepository"/> so the id format has one reader.
    /// </remarks>
    private static string? RepositoryIdFromCommitId(string? commitId)
        => Commit.TryParseRepository(commitId, out var provider, out var repositoryId)
            ? Repository.DocumentId(provider, repositoryId)
            : null;

    public override IReadOnlyCollection<string>? GetDefaultIncludes() => [nameof(Build.Commit)];

    /// <summary>
    /// Custom query: builds of a commit, parent-scoped. Source: "Custom.Commit_Builds".
    /// A Custom.* source because Database.* queries drop parentId upstream (Spark#242).
    /// Build.Commit holds the exact commit document id, so equality suffices (no
    /// prefix filtering — FileCoverage/BuildTreeSummary share the id prefix but are
    /// different collections and never enter this query).
    /// </summary>
    public IRavenQueryable<Build> Commit_Builds(CustomQueryArgs args)
    {
        args.EnsureParent("Commit");
        return session.Query<Build, Indexes.Builds_Overview>()
            .Where(b => b.Commit == args.Parent!.Id);
    }
}
