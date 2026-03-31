using MintPlayer.Spark.Abstractions;

namespace SparkEditor.LookupReferences;

public sealed class ProgramUnitType : TransientLookupReference<string>
{
    private ProgramUnitType() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<ProgramUnitType> Items { get; } =
    [
        new() { Key = "query", Values = _TS("Query", nl: "Query") },
        new() { Key = "persistentObject", Values = _TS("Persistent Object", nl: "Persistent Object") },
    ];
}
