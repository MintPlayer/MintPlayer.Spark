using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Actions;

/// <summary>
/// Where a New started from a Reference picker's <b>Add new</b> came from: the type that owns the
/// picker and its Reference attribute, both verified against the model (the attribute exists, is a
/// Reference with <c>canAddNewReference</c>, and targets the type being constructed). Built by the
/// framework only; a hook reads it through <see cref="SparkNewArgs.ParentType"/> and
/// <see cref="SparkNewArgs.ReferenceAttribute"/>.
/// </summary>
public sealed class SparkNewReferenceContext
{
    internal SparkNewReferenceContext(EntityTypeDefinition parentType, EntityAttributeDefinition attribute)
    {
        ParentType = parentType;
        Attribute = attribute;
    }

    internal EntityTypeDefinition ParentType { get; }
    internal EntityAttributeDefinition Attribute { get; }
}
