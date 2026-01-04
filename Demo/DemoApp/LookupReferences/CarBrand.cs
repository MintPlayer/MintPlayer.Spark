using MintPlayer.Spark.Abstractions;

namespace DemoApp.LookupReferences;

// Dynamic lookup reference - values are stored in RavenDB LookupReferences collection
// Users can add/edit/remove brands through the application UI
public sealed class CarBrand : DynamicLookupReference
{
    // No static Items collection - values come from the database

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Modal;
}
