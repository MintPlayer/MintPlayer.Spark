namespace MintPlayer.Spark.Abstractions;

/// <summary>How a query's grid loads and shows its rows.</summary>
public enum SparkQueryRenderMode
{
    /// <summary>One page at a time, with a pager. The default.</summary>
    Pagination,
    /// <summary>One scrolling list that fetches rows as they come into view.</summary>
    VirtualScrolling
}
