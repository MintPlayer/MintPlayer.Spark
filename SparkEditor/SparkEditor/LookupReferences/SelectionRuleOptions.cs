using MintPlayer.Spark.Abstractions;

namespace SparkEditor.LookupReferences;

public sealed class SelectionRuleOptions : TransientLookupReference<string>
{
    private SelectionRuleOptions() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<SelectionRuleOptions> Items { get; } =
    [
        new() { Key = "None", Values = _TS("None", nl: "Geen") },
        new() { Key = "ZeroOrMore", Values = _TS("Zero or more", nl: "Nul of meer") },
        new() { Key = "OneOrMore", Values = _TS("One or more", nl: "Eén of meer") },
        new() { Key = "ExactlyOne", Values = _TS("Exactly one", nl: "Precies één") },
    ];
}
