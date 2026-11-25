using MintPlayer.Spark.Abstractions;

namespace SqlDemo.Entities;

/// <summary>An author: a row of the <c>Authors</c> table, keyed by a <c>uuid</c> (#536 D23, the Guid id kind).</summary>
public partial class Author
{
    /// <summary>Unique identifier of the author, assigned automatically on creation.</summary>
    public Id<Author> Id { get; set; }

    /// <summary>The author's name as printed on the cover, e.g. <c>Ursula K. Le Guin</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The country the author is from.</summary>
    public string? Country { get; set; }
}
