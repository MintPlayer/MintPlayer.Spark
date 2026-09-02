using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>One documented property: what <c>SparkAttributeDescriptions.g.cs</c> emits a line for (#348).</summary>
[AutoValueComparer]
public partial class AttributeDescriptionInfo
{
    /// <summary>The <c>typeof(...)</c> operand, fully qualified; unbound (<c>Box&lt;&gt;</c>) for generic types.</summary>
    public string TypeOfExpression { get; set; } = string.Empty;

    /// <summary>Fully qualified display name, used only to sort the output stably.</summary>
    public string TypeSortKey { get; set; } = string.Empty;

    public string PropertyName { get; set; } = string.Empty;

    /// <summary>Plain-text summary; paragraphs separated by <c>\n</c>.</summary>
    public string Summary { get; set; } = string.Empty;
}
