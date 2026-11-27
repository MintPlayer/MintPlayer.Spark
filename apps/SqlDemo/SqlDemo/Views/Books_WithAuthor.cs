using System.ComponentModel;
using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Views;
using SqlDemo.Entities;

namespace SqlDemo.Views;

/// <summary>A book as its list shows it: the book's own columns and its author's name.</summary>
public class VBook
{
    /// <summary>The book's id.</summary>
    public string? Id { get; set; }

    /// <summary>The book's title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The author who wrote the book.</summary>
    [Reference(typeof(Author))]
    public Id<Author>? Author { get; set; }

    /// <summary>The author's name, joined in by the view; empty when the book has no author.</summary>
    public string? AuthorName { get; set; }

    /// <summary>The year the book was first published.</summary>
    public int Year { get; set; }

    /// <summary>The book's ISBN-13.</summary>
    public string? Isbn { get; set; }
}

/// <summary>
/// Books with their author's name (#536 D32). Declared once: on PostgreSQL synchronize has EF translate it into the
/// <c>CREATE VIEW</c> of a migration (<c>Migrations/PostgreSql</c>), where RavenDB would deploy a static index with a
/// <c>LoadDocument</c>. A view is computed when it is read, so a new book is in the list as soon as it is saved.
/// </summary>
/// <remarks>
/// <c>[DefaultIndex]</c> makes it the Book list's source (D48): <c>Book.json</c>'s <c>queryType</c> and
/// <c>indexName</c> come from it. <c>Load</c> is the one join form; a missing author reads as <see langword="null"/>.
/// </remarks>
[DefaultIndex]
[Description("Books with their author's name")]
public sealed class Books_WithAuthor : SparkView<VBook>
{
    public override Expression<Func<ISparkViewSource, IQueryable<VBook>>> Map => s =>
        from book in s.Set<Book>()
        select new VBook
        {
            Id = s.IdOf(book),
            Title = book.Title,
            Author = book.Author,
            AuthorName = s.Load<Author>(book.Author)!.Name,
            Year = book.Year,
            Isbn = book.Isbn,
        };
}
