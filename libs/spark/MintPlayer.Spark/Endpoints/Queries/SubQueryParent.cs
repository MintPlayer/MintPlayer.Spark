using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.Queries;

/// <summary>
/// Resolves the object a sub-query was rendered on, for <c>/spark/queries/execute</c> and
/// <c>/spark/queries/distinct-values</c> (#460).
/// </summary>
/// <remarks>
/// <para>
/// The parent is read under its OWN soft-deletion mode — the request's <c>parentDeleted</c>, scoped to
/// the parent's type — and never under the mode the request asked for its rows (<c>deleted</c>, scoped
/// to the query's type). The two are independent: a deleted parent opened from the recycle bin lists
/// its live children, and a <c>deleted: only</c> grid on a live parent of the same type still finds
/// that parent.
/// </para>
/// <para>
/// Core decides nothing here. The mode is only carried to the row policies, and the SoftDelete
/// package's policy honours anything but <see cref="SparkDeletedFilter.Exclude"/> only for callers
/// holding <c>ViewDeleted</c> on the parent's type — exactly as on <c>/spark/po/load</c>. For anyone
/// else a deleted parent is filtered out like any hidden row, so the caller gets the same 404 a missing
/// parent gives (#453: missing and forbidden look the same).
/// </para>
/// <para>
/// ⚠️ Row filters are memoized per (type, action) for the request, so the memo is dropped before and
/// after the parent is read. Without that, a filter computed under the parent's mode would be reused for the
/// rows — which matters whenever the parent's type, or a type one of its references points at, is the
/// query's own type.
/// </para>
/// </remarks>
internal static class SubQueryParent
{
    public static async Task<Abstractions.PersistentObject?> ResolveAsync(
        IDatabaseAccess databaseAccess,
        IRowPolicyRequestState rowPolicyRequestState,
        IRowSecurity rowSecurity,
        EntityTypeDefinition parentEntityType,
        string parentId,
        SparkDeletedFilter? parentDeleted)
    {
        var rowsDeleted = rowPolicyRequestState.Deleted;
        var rowsScope = rowPolicyRequestState.DeletedScopeClrType;

        rowPolicyRequestState.Deleted = parentDeleted ?? SparkDeletedFilter.Exclude;
        rowPolicyRequestState.DeletedScopeClrType = parentEntityType.ClrType;
        // Dropped on the way in too: a filter memoized under the rows' mode must not serve the parent.
        rowSecurity.ResetRequestFilterCache();
        try
        {
            return await databaseAccess.GetPersistentObjectAsync(parentEntityType.Id, parentId);
        }
        finally
        {
            rowPolicyRequestState.Deleted = rowsDeleted;
            rowPolicyRequestState.DeletedScopeClrType = rowsScope;
            rowSecurity.ResetRequestFilterCache();
        }
    }
}
