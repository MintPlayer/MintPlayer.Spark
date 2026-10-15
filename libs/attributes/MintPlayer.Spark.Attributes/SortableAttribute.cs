namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Marks an <c>AsDetail</c> array, or a list of plain values (<c>List&lt;string&gt;</c>, <c>int[]</c>),
/// as drag-to-reorderable in the PO-edit UI.
/// Order is the array position; no explicit index field is required.
/// Ignored on scalar properties and on <c>Reference</c> arrays.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SortableAttribute : Attribute;
