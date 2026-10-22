using MintPlayer.Spark.History;
using MintPlayer.Spark.SoftDelete;

namespace MintPlayer.Entities;

/// <summary>
/// A community blog post (legacy <c>BlogPosts</c>, 10 rows). Migrated with the rest of the data; the
/// persistent-object wiring and the blog page come in stage 1, step 9.
/// </summary>
public partial class BlogPost : ISoftDeletable, IAuditable
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Headline { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>The author's <c>SparkUser</c> id.</summary>
    public string? AuthorId { get; set; }

    public DateTimeOffset? Published { get; set; }
}
