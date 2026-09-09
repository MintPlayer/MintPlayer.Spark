namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Marks the RavenDB index whose <c>[FromIndex]</c> projection shapes the entity's model file — the
/// attribute union, <c>inCollectionType</c>/<c>inQueryType</c> flags, and <c>showedOn</c> derivation
/// all come from the marked index's projection, and minted <c>Database.*</c> queries are stamped with
/// its name.
/// <para>
/// When an entity has exactly one projection-bearing index, that index is implicitly the default and
/// the marker is unnecessary. With two or more, exactly one must carry <c>[DefaultIndex]</c>;
/// zero or several markers fail synchronize/startup with an error naming the candidates — the
/// framework never guesses. <c>[GenerateIndex]</c> emits this marker on its generated index by
/// default (opt out with <c>IsDefault = false</c>).
/// </para>
/// <para>
/// The marker only elects the model-shaping default; it does not route queries. Each Spark query
/// declares the index it runs against via its <c>indexName</c>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class DefaultIndexAttribute : Attribute
{
}
