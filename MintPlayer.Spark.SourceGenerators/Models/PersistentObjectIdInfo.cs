using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>
/// A single entry harvested from a Spark Model JSON file (typically
/// <c>App_Data/Model/*.json</c>). Feeds the <c>PersistentObjectIds</c> generator
/// output — nested-by-schema <c>const string</c> Guids that user code can pass to
/// <c>IManager.NewPersistentObject(Guid)</c>.
/// </summary>
[AutoValueComparer]
public partial class PersistentObjectIdInfo
{
    /// <summary>
    /// The entity's schema (database/module namespace). Defaults to <c>"Default"</c>
    /// when the JSON file does not declare a schema.
    /// </summary>
    public string Schema { get; set; } = "Default";

    /// <summary>
    /// The entity name. Used as the generated C# member identifier under
    /// <c>PersistentObjectIds.{Schema}.{Name}</c>.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The Guid value (as its canonical string representation) to be emitted
    /// as a <c>const string</c>. Already validated as parseable when written.
    /// </summary>
    public string Id { get; set; } = string.Empty;
}
