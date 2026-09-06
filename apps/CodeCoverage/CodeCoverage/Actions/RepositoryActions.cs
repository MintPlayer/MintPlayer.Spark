using System.Linq.Expressions;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Actions;

/// <summary>
/// Row security for the generic /spark read surface — same semantics as
/// BrowseController.ResolveVisibleRepository: anonymous viewers see public
/// repositories; authenticated viewers additionally the repos of owners GitHub
/// grants them. Writes are never granted in security.json, so the WITH CHECK
/// path is unreachable and no machine-principal branch is needed.
/// </summary>
public partial class RepositoryActions : DefaultPersistentObjectActions<Repository>
{
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>
    /// Withholds <c>DeleteData</c> on a repository that is still connected.
    /// <para>
    /// The right is granted to every signed-in user in security.json, and it has to be: rights
    /// here are group-level and there is no group per GitHub owner, so the right can express
    /// "may delete coverage data at all" and nothing narrower. Whether THIS repository may be
    /// deleted is a property of the row — it must be disconnected — and the actions catalogue at
    /// <c>GET /spark/actions/{objectTypeId}</c> is per type, so it cannot answer that.
    /// </para>
    /// <para>
    /// This is the place that can: the entity is in hand, so the answer travels back on the object
    /// itself and the browser simply never renders the button. <c>DeleteDataAction</c> still
    /// refuses independently — withholding an affordance is not a permission check, and the
    /// endpoint stays reachable — but a user is no longer offered an irreversible red button on a
    /// healthy repository that only admits it will refuse after the confirmation prompt.
    /// </para>
    /// </summary>
    public override async Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)
    {
        var obj = await base.OnLoadAsync(id, parent);
        if (obj is null)
            return obj;

        var repository = await session.LoadAsync<Repository>(id);
        if (repository is null || repository.Connection != RepositoryConnection.Disconnected)
            obj.DisableActions("DeleteData");

        return obj;
    }

    public override async Task<Expression<Func<Repository, bool>>?> GetRowFilterAsync(string action)
    {
        // Writes are denied at the type level (no Edit/New/Delete right in
        // security.json), and since Spark#244 the per-row `can` block intersects
        // type-level rights, so no write-action special-casing is needed here.
        // Empty for anonymous viewers → the filter reduces to "public only".
        var owners = await visibility.GetAllowedOwnersAsync();

        // The one place the two rules diverge. "Query" is the grid — a listing, which must stop
        // advertising a repository we have lost access to. "Read" is the detail page, which is
        // where /r/{owner}/{name} lands, so it has to keep resolving for a disconnected repository
        // or every shared report link and every README badge dies with the transfer.
        return action == "Query"
            ? RepositoryVisibility.ListingFilter(owners)
            : RepositoryVisibility.Filter(owners);
    }

    /// <summary>BadgeToken grants badge access on private repos — managers only.</summary>
    public override async Task<IReadOnlyCollection<string>?> GetProtectedAttributesAsync(string action, Repository entity)
        => await visibility.CanManageOwnerAsync(entity.OwnerLogin)
            ? null
            : [nameof(Repository.BadgeToken)];

    public override IReadOnlyCollection<string>? GetDefaultIncludes() => [nameof(Repository.Account)];

    /// <summary>
    /// Custom query: repositories of an account, parent-scoped. Source:
    /// "Custom.Account_Repositories". A Custom.* source because Database.*
    /// queries drop parentId upstream (Spark#242); the framework still applies
    /// the row filter and sorting on top.
    /// </summary>
    public IRavenQueryable<Repository> Account_Repositories(CustomQueryArgs args)
    {
        args.EnsureParent("Account");
        return session.Query<Repository, Indexes.Repositories_Overview>()
            .Where(r => r.Account == args.Parent!.Id);
    }
}
