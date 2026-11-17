using MintPlayer.Spark.Abstractions;

namespace SparkNs.LookupReferences;

/// <summary>The SparkItem values. The enum is what the entity stores.</summary>
public enum ESparkItem
{
    First,
    Second,
}

/// <summary>
/// The SparkItem lookup: a fixed list of values with a translated label each.
/// </summary>
/// <remarks>
/// Put it on an entity property, then run <c>dotnet run -- --spark-synchronize-model</c> in the app project:
/// <code>
/// using MintPlayer.Spark.Abstractions;
/// using SparkNs.LookupReferences;
/// …
/// [LookupReference(typeof(SparkItem))]
/// public ESparkItem? SparkItem { get; set; }
/// </code>
/// <c>_TS</c> takes the English label, then optionally French and Dutch.
/// </remarks>
public sealed class SparkItem : TransientLookupReference<ESparkItem>
{
    private SparkItem() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<SparkItem> Items { get; } =
    [
        new SparkItem { Key = ESparkItem.First, Values = _TS("First") },
        new SparkItem { Key = ESparkItem.Second, Values = _TS("Second") },
    ];
}
