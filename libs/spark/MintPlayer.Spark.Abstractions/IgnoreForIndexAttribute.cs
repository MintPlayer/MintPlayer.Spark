namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Excludes a property from a generated index and its projection class, while leaving it a full member
/// of the Spark model.
/// <para>
/// The property keeps its model attribute, its label and its <c>AttributeNames</c> constant, and is
/// still populated onto and read back from a <c>PersistentObject</c>. It simply is not mapped into the
/// index and gets no projection property, so it cannot be filtered or sorted on index-side and does not
/// contribute to index size or re-indexing cost.
/// </para>
/// <para><strong>This is the opposite trade-off to <see cref="IgnorePropertyAttribute"/>.</strong>
/// <c>[IgnoreProperty]</c> removes a property from the model everywhere and is destructive to its
/// committed model settings; <c>[IgnoreForIndex]</c> touches only what the index generator emits and has
/// no effect on the model file. Use this one for properties that are part of the application model but
/// pointless or expensive to index — long free text, large embedded objects, values nobody queries by.
/// </para>
/// <para>
/// Has no effect on hand-written indexes, which map whatever their <c>Map</c> expression says.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IgnoreForIndexAttribute : Attribute;
