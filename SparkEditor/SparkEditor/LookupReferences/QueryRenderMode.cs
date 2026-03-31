using MintPlayer.Spark.Abstractions;

namespace SparkEditor.LookupReferences;

public sealed class QueryRenderMode : TransientLookupReference<string>
{
    private QueryRenderMode() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<QueryRenderMode> Items { get; } =
    [
        new() { Key = "Pagination", Values = _TS("Pagination", nl: "Paginering") },
        new() { Key = "VirtualScrolling", Values = _TS("Virtual Scrolling", nl: "Virtueel scrollen") },
    ];
}
