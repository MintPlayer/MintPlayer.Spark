using MintPlayer.Spark.Abstractions;

namespace SparkEditor.LookupReferences;

public sealed class ShowedOnOptions : TransientLookupReference<string>
{
    private ShowedOnOptions() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Multiselect;

    public static IReadOnlyCollection<ShowedOnOptions> Items { get; } =
    [
        new() { Key = "Query", Values = _TS("Query") },
        new() { Key = "PersistentObject", Values = _TS("Persistent Object") },
    ];
}
