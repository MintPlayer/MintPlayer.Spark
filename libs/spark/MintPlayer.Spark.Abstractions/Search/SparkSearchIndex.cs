namespace MintPlayer.Spark.Abstractions.Search;

/// <summary>
/// The shape of the generated <c>Spark_Search</c> index (<c>docs/spark_search_PRD.md</c> D5): one entry per document
/// of every type whose model has <c>search.enabled</c>, emitted into the application by the source generator. The
/// RavenDB provider searches it, the external-engine sync reads changes from it, and both agree on these names.
/// </summary>
public static class SparkSearchIndex
{
    /// <summary>The index name RavenDB derives from the generated class <c>Spark_Search</c>.</summary>
    public const string Name = "Spark/Search";

    /// <summary>The model's entity type name of the document.</summary>
    public const string Type = nameof(SparkSearchEntry.Type);

    /// <summary>All searched text of the document: the label's attributes and <c>search.fields</c>, analyzed for full-text search.</summary>
    public const string Text = nameof(SparkSearchEntry.Text);

    /// <summary>The same text as lower-cased words, not analyzed, so a word can be matched fuzzily.</summary>
    public const string Terms = nameof(SparkSearchEntry.Terms);

    /// <summary>Whether the document is soft-deleted. Deleted documents stay in the index so a sync sees the deletion.</summary>
    public const string IsDeleted = nameof(SparkSearchEntry.IsDeleted);

    /// <summary>The document's <c>@last-modified</c>, for the external-engine sync's checkpoint.</summary>
    public const string LastModified = nameof(SparkSearchEntry.LastModified);
}

/// <summary>One entry of <see cref="SparkSearchIndex"/>, as the generated maps emit it and stored fields return it.</summary>
public sealed class SparkSearchEntry
{
    /// <summary>The document id.</summary>
    public string Id { get; set; } = "";

    /// <inheritdoc cref="SparkSearchIndex.Type" />
    public string Type { get; set; } = "";

    /// <inheritdoc cref="SparkSearchIndex.Text" />
    public string? Text { get; set; }

    /// <inheritdoc cref="SparkSearchIndex.Terms" />
    public string[]? Terms { get; set; }

    /// <inheritdoc cref="SparkSearchIndex.IsDeleted" />
    public bool IsDeleted { get; set; }

    /// <inheritdoc cref="SparkSearchIndex.LastModified" />
    public DateTime LastModified { get; set; }
}
