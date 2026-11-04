using System.Text.RegularExpressions;

namespace MintPlayer.Spark.Search.OpenSearch;

/// <summary>Options of <see cref="SparkOpenSearchExtensions.UseOpenSearch"/>.</summary>
public sealed partial class SparkOpenSearchOptions
{
    /// <summary>The engine's base address, e.g. <c>http://mintplayer-search:9200</c>. Required.</summary>
    public Uri? Uri { get; set; }

    /// <summary>
    /// The engine index this application owns. Default <c>spark-search</c>. Lower-case letters, digits, <c>-</c> and
    /// <c>_</c>: it is a URL segment.
    /// </summary>
    public string IndexName { get; set; } = "spark-search";

    /// <summary>
    /// How long one engine request may take before the RavenDB index answers instead (D6). Default two seconds: a
    /// suggestion that arrives later is useless anyway.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How often changes are copied from <c>Spark_Search</c> to the engine. Default five seconds.</summary>
    public TimeSpan SyncInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Rows copied per engine request. Default 500.</summary>
    public int SyncBatchSize { get; set; } = 500;

    internal void Validate()
    {
        if (Uri is null || !Uri.IsAbsoluteUri || (Uri.Scheme != "http" && Uri.Scheme != "https"))
            throw new InvalidOperationException("UseOpenSearch needs an absolute http(s) Uri for the engine.");
        if (!IndexNamePattern().IsMatch(IndexName))
            throw new InvalidOperationException($"'{IndexName}' is not a valid OpenSearch index name for Spark: use lower-case letters, digits, '-' and '_'.");
        if (RequestTimeout <= TimeSpan.Zero || SyncInterval <= TimeSpan.Zero || SyncBatchSize <= 0)
            throw new InvalidOperationException("UseOpenSearch timeouts, interval and batch size must be positive.");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex IndexNamePattern();
}
