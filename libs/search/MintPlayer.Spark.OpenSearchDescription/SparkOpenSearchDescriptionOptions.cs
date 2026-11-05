namespace MintPlayer.Spark.OpenSearchDescription;

/// <summary>Options of <see cref="SparkOpenSearchDescriptionExtensions.AddOpenSearchDescription"/>.</summary>
public sealed class SparkOpenSearchDescriptionOptions
{
    /// <summary>The name in the browser's list of search engines. At most 16 characters (OpenSearch 1.1); longer is cut.</summary>
    public string? ShortName { get; set; }

    /// <summary>A sentence describing the engine. Default <c>Search {ShortName}</c>.</summary>
    public string? Description { get; set; }

    /// <summary>A contact address advertised in the description. Default none.</summary>
    public string? Contact { get; set; }

    /// <summary>The engine's icon, a site-relative path. Default <c>/favicon.ico</c>; null advertises none.</summary>
    public string? ImageUrl { get; set; } = "/favicon.ico";

    /// <summary>The media type of <see cref="ImageUrl"/>. Default <c>image/x-icon</c>.</summary>
    public string ImageType { get; set; } = "image/x-icon";

    /// <summary>The icon's size in pixels. Default 16.</summary>
    public int ImageSize { get; set; } = 16;

    /// <summary>
    /// Where a browser search lands when the term names one object (<c>docs/spark_search_PRD.md</c> D4). Default
    /// <see cref="SingleHitBehavior.Item"/>.
    /// </summary>
    public SingleHitBehavior SingleHit { get; set; } = SingleHitBehavior.Item;

    /// <summary>The description document; link it with <c>withOpenSearchDescription()</c>. Default <c>/opensearch.xml</c>.</summary>
    public string Path { get; set; } = "/opensearch.xml";

    /// <summary>The suggestions the browser polls while the user types. Default <c>/spark/opensearch/suggest</c>.</summary>
    public string SuggestPath { get; set; } = "/spark/opensearch/suggest";

    /// <summary>The submitted search, which redirects. Default <c>/spark/opensearch/search</c>.</summary>
    public string SearchPath { get; set; } = "/spark/opensearch/search";

    /// <summary>OpenSearch 1.1 caps <c>ShortName</c> at 16 characters.</summary>
    internal const int ShortNameMaxLength = 16;
}

/// <summary>Where a browser search that names exactly one object lands.</summary>
public enum SingleHitBehavior
{
    /// <summary>On the object's page: a picked suggestion, or a term only one visible object matches.</summary>
    Item,

    /// <summary>Always on the search page.</summary>
    SearchPage,
}
