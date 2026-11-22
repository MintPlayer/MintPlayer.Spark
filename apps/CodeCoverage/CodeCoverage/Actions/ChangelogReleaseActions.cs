using System.Linq.Expressions;
using CodeCoverage.Entities;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents.Linq;

namespace CodeCoverage.Actions;

/// <summary>
/// The changelog on the repository page (#693 M11). A release is visible iff its repository is, like a versioning run, and
/// security.json grants only <c>QueryRead</c>: the copies are written by <see cref="ChangelogRecipient"/>, never through Spark.
/// </summary>
public partial class ChangelogReleaseActions : DefaultPersistentObjectActions<ChangelogRelease>
{
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly ISparkSession session;

    public override async Task<Expression<Func<ChangelogRelease, bool>>?> GetRowFilterAsync(string action)
    {
        // Raven's .In() — see RepositoryActions.GetRowFilterAsync for why not Contains.
        var repoIds = await visibility.GetVisibleRepositoryIdsAsync();
        return r => r.Repository.In(repoIds);
    }

    /// <summary>
    /// Custom query: a repository's released versions, newest first. Source: "Custom.Repository_Changelog". Read by the
    /// repository's id prefix through <see cref="ISparkSession"/>, so it builds no index (D16).
    /// </summary>
    public async Task<IQueryable<ChangelogRelease>> Repository_Changelog(CustomQueryArgs args)
    {
        args.EnsureParent("Repository");
        if (await session.LoadAsync<Repository>(args.Parent!.Id) is not { } repository) return Enumerable.Empty<ChangelogRelease>().AsQueryable();
        return (await ChangelogRecipient.ForRepositoryAsync(session, repository)).AsQueryable();
    }
}
