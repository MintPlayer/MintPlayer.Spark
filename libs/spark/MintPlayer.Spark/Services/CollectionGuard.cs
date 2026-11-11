using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Binds a client-supplied document id to the entity type that was authorized.
/// <para>
/// The generic persistent-object endpoints resolve the CLR type from <c>objectTypeId</c> (which is
/// run through <see cref="Abstractions.Authorization.IPermissionService"/>) but take the document
/// <c>id</c> from the route or body untrusted. RavenDB's <c>LoadAsync&lt;T&gt;(id)</c> does not
/// enforce a collection — handed an id from another collection it silently deserializes that
/// document into <c>T</c>. So an authorization decision made about one type could be applied to a
/// write against a document of another (a caller with rights on <c>Customer</c> overwriting a
/// <c>SparkUser</c> by naming its id). This guard rejects that: a loaded document whose real
/// <c>@collection</c> is not the authorized type's collection is treated as not-found.
/// </para>
/// </summary>
public interface ICollectionGuard
{
    /// <summary>
    /// Whether a document just loaded and tracked by <paramref name="session"/> genuinely belongs
    /// to <paramref name="expectedType"/>'s collection. The document's metadata carries its real
    /// <c>@collection</c> regardless of the type it was deserialized into, so this catches a
    /// foreign id even when the two types are field-compatible. A tracked entity with no metadata
    /// (should not happen for a stored document) fails closed.
    /// </summary>
    bool BelongsToAuthorizedCollection(ISparkSession session, object entity, Type expectedType);
}

[Register(typeof(ICollectionGuard), ServiceLifetime.Singleton)]
internal sealed class CollectionGuard : ICollectionGuard
{
    public bool BelongsToAuthorizedCollection(
        ISparkSession session, object entity, Type expectedType)
    {
        var expected = session.GetCollectionName(expectedType);

        var actual = session.GetStoredCollectionName(entity);
        if (actual is null)
            return false;

        return string.Equals(actual, expected, StringComparison.Ordinal);
    }
}
