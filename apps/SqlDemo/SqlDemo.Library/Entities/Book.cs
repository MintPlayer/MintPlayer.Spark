using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.History;

namespace SqlDemo.Entities;

/// <summary>
/// A book. Its list reads the <c>Books_WithAuthor</c> view (<c>SqlDemo/Views</c>), which joins the author's name in:
/// an SQL view on PostgreSQL, where the RavenDB demos use a static index (#536 D32).
/// </summary>
/// <remarks>
/// <see cref="IAuditable"/> (#271): <c>CreatedBy</c>/<c>CreatedAt</c> and <c>ModifiedBy</c>/<c>ModifiedAt</c> are
/// generated into this partial type and stamped by History. The revisions History keeps (<c>App_Data/Model/Book.json</c>)
/// are rows too, and each one names its author through <c>ModifiedBy</c>.
/// </remarks>
public partial class Book : IAuditable
{
    /// <summary>Unique identifier of the book, assigned automatically on creation.</summary>
    public Id<Book> Id { get; set; }

    /// <summary>The book's title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The author who wrote the book: a foreign key to the <c>Authors</c> table.</summary>
    /// <remarks>
    /// A typed id (#536 D23): the column gets a real FK constraint (D26), and the view's <c>Load</c> joins on it. A
    /// <c>[Reference]</c> <see cref="string"/> stores the same key, but a relational view refuses to join on it.
    /// </remarks>
    [Reference(typeof(Author))]
    public Id<Author>? Author { get; set; }

    /// <summary>The year the book was first published.</summary>
    public int Year { get; set; }

    /// <summary>The book's ISBN-13, e.g. <c>978-0-441-47812-5</c>.</summary>
    public string? Isbn { get; set; }
}
