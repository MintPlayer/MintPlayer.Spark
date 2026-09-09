namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Marks an <c>AsDetail</c> array property as drag-to-reorderable in the PO-edit UI.
/// Order is the array position; no explicit index field is required.
/// Ignored on non-AsDetail or non-array properties.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SortableAttribute : Attribute;
