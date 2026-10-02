using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using Po = MintPlayer.Spark.Abstractions.PersistentObject;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

/// <summary>
/// The parent an object is created under when its New was started from a sub-query card on that
/// parent's detail page (#460, D19): the same three fields — <c>parentId</c>, <c>parentType</c>,
/// <c>queryId</c> — on <c>POST /po/new</c> and on <c>POST /po/create</c>.
/// </summary>
/// <remarks>
/// One resolution for both, so the object a hook prefilled from and the object the save is checked
/// against cannot disagree. Every field is verified rather than trusted, and every mismatch is the
/// same refusal as a missing row, so none of them answers "does this parent / query exist".
/// </remarks>
internal static class SubQueryNewParent
{
    internal sealed record Resolution(Po Parent, EntityTypeDefinition ParentType, SparkQuery Query, string? ParentReference);

    /// <summary>
    /// <see langword="null"/> with <paramref name="refused"/> false: no parent was named (all three
    /// fields absent). Refused: some but not all fields, an unknown type or query, a query that is not
    /// one of the parent type's sub-queries or does not list <paramref name="entityType"/>, or a
    /// parent the caller may not read.
    /// </summary>
    public static async Task<(Resolution? Resolution, bool Refused)> ResolveAsync(
        IModelLoader modelLoader,
        IQueryLoader queryLoader,
        IDatabaseAccess databaseAccess,
        EntityTypeDefinition entityType,
        string? parentId,
        string? parentTypeName,
        string? queryId)
    {
        if (parentId is not { Length: > 0 } && parentTypeName is not { Length: > 0 } && queryId is not { Length: > 0 })
            return (null, false);

        if (parentId is not { Length: > 0 } || parentTypeName is not { Length: > 0 } || queryId is not { Length: > 0 })
            return (null, true);

        var parentType = modelLoader.ResolveEntityType(parentTypeName);
        var query = queryLoader.ResolveQuery(queryId);
        var entry = parentType is null || query is null ? null : SparkSubQueries.FindEntry(parentType, query);

        // The query must be one the parent's type declares as a sub-query, and must list the type
        // being constructed — otherwise a caller could hand any object any "parent".
        if (parentType is null || query is null || entry is null
            || !string.Equals(query.EntityType, entityType.Name, StringComparison.OrdinalIgnoreCase))
            return (null, true);

        // Through the gated read: a parent the caller may not see refuses the request.
        var parent = await databaseAccess.GetPersistentObjectAsync(parentType.Id, parentId);
        if (parent is null)
            return (null, true);

        return (new Resolution(parent, parentType, query, entry.ParentReference ?? query.ParentReference), false);
    }
}
