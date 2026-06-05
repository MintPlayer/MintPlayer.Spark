namespace MintPlayer.Spark.Abstractions;

[AttributeUsage(AttributeTargets.Property)]
public sealed class ReferenceAttribute : Attribute
{
    public Type TargetType { get; }
    public string? Query { get; }

    public ReferenceAttribute(Type targetType, string? query = null)
    {
        TargetType = targetType;
        Query = query;
    }
}
