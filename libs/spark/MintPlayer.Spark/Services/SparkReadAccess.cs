using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Whether the current caller may open a record by its id alone, through the same gates the detail read path applies.
/// </summary>
/// <remarks>
/// For a feature that inherits its access from another record whose type it does not know at compile time: a log that
/// belongs to some persistent object (#660 G15) is readable exactly when that object is. The type is resolved from the
/// stored document's collection, never from the id's text, so an id cannot claim a type it does not have.
/// </remarks>
public interface ISparkReadAccess
{
    /// <summary>
    /// True when the record exists, its type is in the model, the caller holds the type's <c>Read</c> right, and the
    /// type's row rule admits this row for <c>Read</c>. Everything else, a missing record included, is false: the
    /// answer is no existence oracle.
    /// </summary>
    Task<bool> MayReadAsync(string id, CancellationToken cancellationToken = default);
}

[Register(typeof(ISparkReadAccess), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped)]
internal sealed partial class SparkReadAccess : ISparkReadAccess
{
    [Inject] private readonly ISparkSession session;
    [Inject] private readonly ISparkStore store;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly ISparkTypeResolver typeResolver;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IRowSecurity rowSecurity;

    public async Task<bool> MayReadAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id))
            return false;

        // Untyped, only to read the stored collection, and in a side session: the document may deserialize to a JObject
        // when its CLR type does not resolve, and that must not become the request session's tracked instance.
        string? collection;
        using (var probe = store.OpenSession())
            collection = await probe.LoadAsync<object>(id, cancellationToken) is { } document
                ? probe.GetStoredCollectionName(document)
                : null;
        if (string.IsNullOrEmpty(collection))
            return false;

        foreach (var definition in modelLoader.GetEntityTypes())
        {
            if (typeResolver.Resolve(definition.ClrType) is not { } clrType
                || !string.Equals(session.GetCollectionName(clrType), collection, StringComparison.Ordinal))
                continue;

            // AreAllowedAsync loads the row under its declared type and applies the type's row rule; the collection was
            // matched just now, which is the guard it asks its callers to have applied.
            return await permissionService.IsAllowedAsync("Read", definition.Name, cancellationToken)
                && await rowSecurity.AreAllowedAsync(session, clrType, "Read", [id]);
        }

        return false;
    }
}
