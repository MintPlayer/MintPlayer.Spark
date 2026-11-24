using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Conventions;

namespace MintPlayer.Spark;

/// <summary>
/// The one place a Spark document store is configured, so the middleware and the test drivers cannot
/// drift apart.
/// </summary>
/// <remarks>
/// They had drifted: production set <c>Conventions.Serialization</c> while
/// <c>SparkTestDriver</c> and <c>SparkSharedDatabase</c> set only the id conventions, and
/// <c>SparkEndpointFactory</c> substitutes the driver's store for the one <c>AddSpark</c> registered —
/// so the middleware's configuration never applied under test. A green suite proved nothing about
/// production for anything serializer-shaped.
/// </remarks>
public static class SparkStoreConfiguration
{
    /// <summary>
    /// Applies every convention a Spark store needs. Call from the middleware and from any test
    /// driver; never configure a store by hand.
    /// </summary>
    public static Raven.Client.Documents.IDocumentStore ApplySparkConventions(
        this Raven.Client.Documents.IDocumentStore store)
    {
        store.Conventions.UseNaturalIds().UseGeneratedIds().UseTypedIds();

        var serialization = new Raven.Client.Json.Serialization.NewtonsoftJson.NewtonsoftJsonSerializationConventions
        {
            CustomizeJsonSerializer = serializer =>
            {
                serializer.Converters.Add(new Converters.ColorNewtonsoftJsonConverter());
                serializer.Converters.Add(new Converters.IdNewtonsoftJsonConverter());
            },
            CustomizeJsonDeserializer = serializer =>
            {
                serializer.Converters.Add(new Converters.ColorNewtonsoftJsonConverter());
                serializer.Converters.Add(new Converters.IdNewtonsoftJsonConverter());
            },
        };
        serialization.JsonContractResolver = new Converters.SparkRavenContractResolver(serialization);
        store.Conventions.Serialization = serialization;

        return store;
    }
}

/// <summary>
/// The document-id rules every Spark store follows.
/// <para>
/// Exposed rather than inlined into the store setup so that a test — or an app that opens its own
/// <c>DocumentStore</c> against a second database — exercises the same rules the framework applies,
/// instead of a hand-written copy that can drift from them.
/// </para>
/// <para>
/// Both must be installed before <c>DocumentStore.Initialize()</c>: RavenDB freezes conventions
/// there and throws on any later change.
/// </para>
/// </summary>
public static class SparkDocumentStoreConventions
{
    /// <summary>
    /// Stores an entity implementing <see cref="IHasNaturalId"/> under the id it derives from its
    /// own contents, rather than a generated one.
    /// </summary>
    public static DocumentConventions UseNaturalIds(this DocumentConventions conventions)
    {
        // Lowered (#536 D7): every id is lower case on every provider.
        conventions.RegisterAsyncIdConvention<IHasNaturalId>(
            (_, entity) => Task.FromResult(entity.GetId().ToLowerInvariant()));

        return conventions;
    }

    /// <summary>
    /// Gives every other entity <c>{Collection}/{Guid}</c>.
    /// <para>
    /// GUIDs rather than HiLo: HiLo reserves ranges from the server, which serializes writes behind
    /// a cluster-wide operation and makes ids guessable in sequence.
    /// </para>
    /// <para>
    /// This is a fallback, not an alternative to <see cref="UseNaturalIds"/>. RavenDB consults
    /// registered id conventions first and reaches this generator only when none matches, so
    /// installing both is what lets the entity decide — the order they are installed in does not
    /// matter.
    /// </para>
    /// </summary>
    public static DocumentConventions UseGeneratedIds(this DocumentConventions conventions)
    {
        conventions.AsyncDocumentIdGenerator = (_, entity) =>
            Task.FromResult($"{conventions.GetCollectionName(entity.GetType())}/{Guid.NewGuid()}".ToLowerInvariant());

        return conventions;
    }

    /// <summary>
    /// The id rules for entities with an <see cref="Id{T}"/> (#536 D23 rule 6, D7):
    /// <list type="bullet">
    /// <item>the generated <c>__DocumentId</c> string member is the identity property when the type has one; a type
    /// without it keeps its <c>Id</c> string, RavenDB's default</item>
    /// <item>id prefixes RavenDB builds from a collection name (HiLo, and anyone asking the convention) are lower
    /// case</item>
    /// </list>
    /// <see cref="DocumentConventions.FindCollectionName"/> is unchanged: it names the <c>@collection</c>, not the id.
    /// </summary>
    public static DocumentConventions UseTypedIds(this DocumentConventions conventions)
    {
        conventions.FindIdentityProperty = member => Converters.SparkDocumentIdentity.Of(member.DeclaringType!) is { } identity
            ? member.Name == identity.Name
            : member.Name == "Id";
        conventions.TransformTypeCollectionNameToDocumentIdPrefix = static collection => collection.ToLowerInvariant();

        // A reference stored as a string compares with an Id<T> value: the query sends the canonical string. RavenDB
        // matches a converter by the value's runtime type, so one registration over the interface covers every T.
        conventions.RegisterQueryValueConverter<ISparkId>(static (string _, ISparkId value, bool _, out string? text) =>
        {
            text = value.IsEmpty ? null : value.Value;
            return true;
        });

        return conventions;
    }
}
