namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Interface for access control services. When registered, Spark will check
/// permissions before allowing CRUD operations on PersistentObjects and Queries.
/// </summary>
public interface IAccessControl
{
    /// <summary>
    /// Checks if the current user has permission to perform an action on a resource.
    /// </summary>
    /// <param name="resource">The resource identifier. Format examples:
    /// <list type="bullet">
    /// <item><description>"Read/Person" - Read access to Person PersistentObject</description></item>
    /// <item><description>"Edit/Person" - Edit access to Person PersistentObject</description></item>
    /// <item><description>"Query/Person" - Query/list access to Person PersistentObject</description></item>
    /// </list>
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if access is allowed, false otherwise</returns>
    Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's effective attribute rights for <paramref name="verb"/> on
    /// <paramref name="entityTypeName"/> (<c>{verb}/{Type}/{Attr}</c> rights, PRD §5 Q13).
    /// </summary>
    /// <remarks>
    /// The default answers the type-level question and lets every attribute inherit it — the right
    /// behaviour for an evaluator that knows no attribute-level rights (a test double granting
    /// resource strings, say). The security.json evaluator overrides it with the real composition.
    /// Call it through <see cref="IAttributeRights"/>, which memoises per request and handles system
    /// context.
    /// </remarks>
    async Task<EffectiveAttributeRights> GetAttributeRightsAsync(
        string verb, string entityTypeName, CancellationToken cancellationToken = default)
        => EffectiveAttributeRights.Inherit(
            entityTypeName, verb, await IsAllowedAsync($"{verb}/{entityTypeName}", cancellationToken));
}

/// <summary>
/// The effective attribute-level rights of the current caller (PRD §5 Q11–Q14): which attributes of
/// an entity type the caller may Query, Read, Edit or create (New).
/// </summary>
/// <remarks>
/// Scoped: evaluated once per request per (type, verb) and memoised, never per row. System context
/// (<c>SparkSystemContext</c>) is unrestricted. Enforcement — removal from payloads, query columns,
/// the write shield — consumes this; it does not decide anything itself.
/// </remarks>
public interface IAttributeRights
{
    /// <summary>The effective rights for <paramref name="verb"/> (one of <see cref="SparkAttributeRights.Verbs"/>) on <paramref name="entityType"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="verb"/> has no attribute-level form.</exception>
    Task<EffectiveAttributeRights> GetEffectiveAsync(
        EntityTypeDefinition entityType, string verb, CancellationToken cancellationToken = default);

    /// <summary>As the definition overload, by the type's name (as resources name it).</summary>
    Task<EffectiveAttributeRights> GetEffectiveAsync(
        string entityTypeName, string verb, CancellationToken cancellationToken = default);
}
