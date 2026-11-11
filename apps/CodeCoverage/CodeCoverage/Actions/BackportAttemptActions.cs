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
    [Inject] private readonly IEnumerable<IForgeAccessService> accounts;

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

    /// <summary>
    /// Custom query: the held backports that wait for the signed-in user (segment F, PRD Q12): those of pull requests they
    /// authored, and every one in a repository they administer or whose account they manage. Source:
    /// "Custom.BackportAttempts_Held", linked from the hold comment as <c>/query/held-backports</c>.
    /// </summary>
    /// <remarks>
    /// Filtered here by the forge's current answers, on top of the row filter (visible repositories). Streams the attempts'
    /// id prefix, so no index is built (D-O22's rule); a backport is held rarely, and an attempt is one small document per
    /// backported pull request and line.
    /// </remarks>
    public async Task<IQueryable<BackportAttempt>> BackportAttempts_Held(CustomQueryArgs args)
    {
        var visible = new HashSet<string>(await visibility.GetVisibleRepositoryIdsAsync(), StringComparer.Ordinal);
        if (visible.Count == 0) return Enumerable.Empty<BackportAttempt>().AsQueryable();

        var held = new List<BackportAttempt>();
        await using (var stream = await session.Advanced.StreamAsync<BackportAttempt>(BackportAttempt.Prefix))
        {
            while (await stream.MoveNextAsync())
                if (stream.Current.Document is { Outcome: BackportAttempt.Held } attempt && visible.Contains(attempt.Repository)) held.Add(attempt);
        }
        if (held.Count == 0) return Enumerable.Empty<BackportAttempt>().AsQueryable();

        var administered = new HashSet<string>(await visibility.GetAdministeredRepositoryIdsAsync(), StringComparer.Ordinal);
        var managed = new HashSet<string>(await visibility.GetManagedOwnersAsync(), StringComparer.OrdinalIgnoreCase);
        var repositories = await session.LoadAsync<Repository>(held.Select(a => a.Repository).Distinct());
        var logins = new Dictionary<CodeCoverage.Forge.EForgeProvider, string?>();

        var mine = new List<BackportAttempt>();
        foreach (var attempt in held)
        {
            if (!repositories.TryGetValue(attempt.Repository, out var repository) || repository is null) continue;
            if (administered.Contains(attempt.Repository) || managed.Contains(repository.OwnerKey))
            {
                mine.Add(attempt);
                continue;
            }
            if (attempt.SourceAuthor is null) continue;
            if (!logins.TryGetValue(repository.Provider, out var login))
            {
                // Who the forge says the viewer is (T1), never a name from the profile.
                login = accounts.FirstOrDefault(a => a.Provider == repository.Provider) is { } access ? (await access.GetViewerAccountAsync()).Login : null;
                logins[repository.Provider] = login;
            }
            if (login is not null && string.Equals(login, attempt.SourceAuthor, StringComparison.OrdinalIgnoreCase)) mine.Add(attempt);
        }
        return mine.OrderByDescending(a => a.AttemptedAtUtc).AsQueryable();
    }
}
