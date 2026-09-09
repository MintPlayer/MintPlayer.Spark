namespace MintPlayer.Spark.Abstractions;

[AttributeUsage(AttributeTargets.Property)]
public sealed class LookupReferenceAttribute : Attribute
{
    public Type LookupType { get; }

    public LookupReferenceAttribute(Type lookupType)
    {
        LookupType = lookupType;
    }
}
