using MintPlayer.Spark.Abstractions;

namespace Fleet.LookupReferences;

/// <summary>
/// Dynamic lookup reference — values are stored in the RavenDB database
/// and can be managed by users at runtime.
/// </summary>
public sealed class CarBrand : DynamicLookupReference
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
}
