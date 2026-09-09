namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Excludes a property from the Spark model entirely. The property stays a perfectly
/// ordinary CLR property — it is persisted by RavenDB like any other — but Spark treats
/// it as if it did not exist.
/// <para>
/// Specifically, an ignored property gets no attribute in the generated
/// <c>App_Data/Model/{Type}.json</c>, is never populated onto or read back from a
/// <c>PersistentObject</c>, is not <c>.Include()</c>d when decorated with
/// <see cref="ReferenceAttribute"/>, is not transmitted or declared writable by
/// replication, and gets no constant in the generated <c>AttributeNames</c> class.
/// Applies on embedded/value-object types as well as entity roots.
/// </para>
/// <para>
/// Use this for stored properties that need a public setter but are not part of the
/// application model — infrastructure fields, denormalized caches, persistence helpers.
/// It is also how you exclude a computed get-only property: those are part of the model
/// by default, surfaced as read-only attributes.
/// </para>
/// <para><strong>Ignoring an existing property is destructive to its model settings.</strong>
/// The next <c>--spark-synchronize-model</c> run removes the attribute block from the
/// committed model file, discarding its id, translated label, rules, renderer and group.
/// Re-adding the property later regenerates it with a new id.
/// <br/>
/// This is the one case where synchronization deletes anything. An attribute whose property
/// was merely renamed or removed is <em>kept</em> — marking a property ignored is an explicit
/// instruction, a property quietly disappearing is not.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IgnorePropertyAttribute : Attribute;
