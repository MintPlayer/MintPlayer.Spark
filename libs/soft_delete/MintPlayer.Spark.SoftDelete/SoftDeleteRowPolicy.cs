using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.SoftDelete;

/// <summary>
/// Hides soft-deleted rows from every path that asks row security — list, detail, custom queries,
/// sub-queries, distinct values, streams, breadcrumbs, reference pickers, the edit/delete gates,
/// custom-action selections and the after-save WITH CHECK.
/// </summary>
/// <remarks>
/// <para>
/// Per action:
/// <list type="bullet">
/// <item><c>Restore</c>, <c>Purge</c> — only deleted rows (<c>IsDeleted == true</c>): you cannot
/// restore or purge a live row.</item>
/// <item><c>Query</c>, <c>Read</c> — live rows, unless the request asked for
/// <c>deleted: include</c> (everything) or <c>only</c> (deleted rows) <b>and</b> the caller holds
/// <c>ViewDeleted</c> on this type; otherwise the flag is ignored, silently, like any filter the
/// caller may not widen.</item>
/// <item>everything else (<c>Edit</c>, <c>Delete</c>, <c>New</c>, custom actions) — live rows.</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ Always <c>IsDeleted != true</c> for "live", never <c>!IsDeleted</c>: documents stored before
/// the type became soft-deletable have no field, and an absent field matches neither <c>== false</c>
/// nor <c>!x</c> (measured, #460 spike S3) — they would vanish from every list.
/// </para>
/// <para>
/// Not a visibility decision (<see cref="IRowPolicy.IsVisibilityDecision"/> is false): it hides rows
/// from everyone, so it neither satisfies the anonymous-readable validator nor trips the
/// <c>SparkQueryPage&lt;T&gt;</c> refusal (spike S5).
/// </para>
/// </remarks>
internal sealed partial class SoftDeleteRowPolicy : RowFilterPolicy<ISoftDeletable>
{
    internal static readonly Expression<Func<ISoftDeletable, bool>> Live = x => x.IsDeleted != true;
    internal static readonly Expression<Func<ISoftDeletable, bool>> Deleted = x => x.IsDeleted == true;

    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IOptions<SparkSoftDeleteOptions> options;

    /// <inheritdoc />
    public override bool BypassInSystemContext => !options.Value.ApplyInSystemContext;

    /// <inheritdoc />
    public override async ValueTask<Expression<Func<ISoftDeletable, bool>>?> GetFilterAsync(RowPolicyContext context)
    {
        switch (context.Action)
        {
            case SoftDeleteRights.Restore:
            case SoftDeleteRights.Purge:
                return Deleted;

            case "Query":
            case "Read":
                if (context.Deleted != SparkDeletedFilter.Exclude && await MayViewDeletedAsync(context))
                    return context.Deleted == SparkDeletedFilter.Include ? null : Deleted;
                return Live;

            default:
                return Live;
        }
    }

    private async Task<bool> MayViewDeletedAsync(RowPolicyContext context)
        => context.IsSystemContext
           || await permissionService.IsAllowedAsync(SoftDeleteRights.ViewDeleted, SoftDeleteTypeNames.Of(modelLoader, context.EntityType));
}

/// <summary>The <c>security.json</c> target name of an entity type.</summary>
internal static class SoftDeleteTypeNames
{
    public static string Of(IModelLoader modelLoader, Type entityType)
        => modelLoader.GetEntityTypeByClrType(entityType.FullName ?? entityType.Name)?.Name ?? entityType.Name;
}
