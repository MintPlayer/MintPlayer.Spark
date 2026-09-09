namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Marks an existing property as the row key of a <see cref="ValueObjectAttribute"/> type, so no key
/// is generated for it. The property is registered as the key; nothing is emitted.
/// </summary>
/// <remarks>
/// This exists because "declares its own <c>Id</c>" and "has no key" are different statements, and an
/// earlier implementation conflated them: it skipped any type declaring an <c>Id</c>, which dropped
/// two correctly keyed types out of the registry entirely — one keyed by a GitHub option id, one by a
/// value derived during save. Being absent from the registry is not neutral; it makes the type's
/// collections unjudgeable at save time.
/// <para>
/// The key need only be stable and unique within its collection. It does not have to be a
/// <see cref="System.Guid"/>, and it does not have to be called <c>Id</c>.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ValueKeyAttribute : Attribute
{
}
