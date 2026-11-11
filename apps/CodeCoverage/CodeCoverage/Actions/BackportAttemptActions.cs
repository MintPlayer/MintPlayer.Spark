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
/// The backports on the repository page (release lines recovery plan, segment B). An attempt is visible iff its repository
/// is, like a versioning run, and security.json grants only <c>QueryRead</c>: the backport recipients write them, never Spark.
/// </summary>
public partial class BackportAttemptActions : DefaultPersistentObjectActions<BackportAttempt>
{
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession session;

    public override async Task<Expression<Func<BackportAttempt, bool>>?> GetRowFilterAsync(string action)
    {
        // Raven's .In() — see RepositoryActions.GetRowFilterAsync for why not Contains.
        var repoIds = await visibility.GetVisibleRepositoryIdsAsync();
        return a => a.Repository.In(repoIds);
    }

    /// <summary>
    /// Custom query: a repository's backports, newest first. Source: "Custom.Repository_BackportAttempts". Streamed by the
    /// repository's id prefix, so it builds no index (D-O22's rule for this app).
    /// </summary>
    public async Task<IQueryable<BackportAttempt>> Repository_BackportAttempts(CustomQueryArgs args)
    {
        args.EnsureParent("Repository");
        if (await session.LoadAsync<Repository>(args.Parent!.Id) is not { } repository) return Enumerable.Empty<BackportAttempt>().AsQueryable();

        var attempts = new List<BackportAttempt>();
        await using (var stream = await session.Advanced.StreamAsync<BackportAttempt>(BackportAttempt.ForRepository(repository.Provider, repository.GitHubId)))
        {
            while (await stream.MoveNextAsync())
                if (stream.Current.Document is { } attempt && attempt.Repository == repository.Id) attempts.Add(attempt);
        }
        return attempts.OrderByDescending(a => a.AttemptedAtUtc).AsQueryable();
    }
}
