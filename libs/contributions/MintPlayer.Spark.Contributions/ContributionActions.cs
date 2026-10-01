using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Contributions;

/// <summary>
/// The actions of a generated contribution type (registered per declaration as
/// <c>IPersistentObjectActions&lt;{Target}{Property}Contribution&gt;</c>): the default pipeline, plus the
/// custom query that serves the generated <c>{Target}{Property}Contributions</c> query (PRD Q7).
/// </summary>
/// <remarks>
/// ⚠️ An app class named <c>{Target}{Property}ContributionActions</c> takes precedence (Spark resolves
/// actions by name first) and must then serve <see cref="ContributionDescriptor.ContributionsQueryMethod"/>
/// itself.
/// </remarks>
internal sealed class ContributionActions<TContribution>(
    IEntityMapper entityMapper,
    ContributionCatalog catalog,
    IModelLoader modelLoader,
    IAsyncDocumentSession session,
    IServiceProvider services,
    IHttpContextAccessor? httpContextAccessor = null)
    : DefaultPersistentObjectActions<TContribution>(entityMapper, httpContextAccessor)
    where TContribution : class, IContribution
{
    /// <summary>
    /// <c>Custom.SparkContributionsOfTarget</c>: the contributions of the query's <b>parent</b> — the
    /// target, which the query endpoint resolved and row-checked (<c>parentId</c>/<c>parentType</c>) —
    /// with <see cref="IContribution.ContributorName"/> filled. Without a parent of the target type there
    /// are no rows: the history of a target is only reachable through a target the caller may read.
    /// </summary>
    /// <remarks>
    /// A prefix load (no index, so never stale), served in memory: Spark composes row security, the
    /// soft-delete filter (hidden ones only with <c>ViewDeleted</c> and <c>deleted=include|only</c>), the
    /// column filters (the slot of the history link), search, sort (<c>UpdatedAt</c> descending by
    /// default) and paging over it.
    /// </remarks>
    public async Task<IQueryable<TContribution>> SparkContributionsOfTarget(CustomQueryArgs args)
    {
        var handler = catalog.ForContribution(typeof(TContribution));
        if (handler is null || args.Parent is not { Id: { Length: > 0 } targetId } parent)
            return Enumerable.Empty<TContribution>().AsQueryable();

        var parentType = modelLoader.GetEntityType(parent.ObjectTypeId);
        if (parentType is null || !string.Equals(parentType.ClrType, handler.Descriptor.TargetType.FullName, StringComparison.Ordinal))
            return Enumerable.Empty<TContribution>().AsQueryable();

        var rows = await handler.ContributionsOfAsync(targetId, session, services);
        return rows.Cast<TContribution>().AsQueryable();
    }
}
