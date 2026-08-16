using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #239 M6 — RavenDB includes. Before this, <see cref="ReferenceResolver.ApplyIncludes"/> reflected
/// for a non-existent instance <c>Include(string)</c> and silently no-oped, so Spark applied NO
/// includes anywhere and every referenced-document access was a round-trip. These tests pin that
/// includes now actually emit into the query (a cache hit for the referenced doc) and that a
/// consumer's <c>GetDefaultIncludes()</c> paths are merged with the <c>[Reference]</c> ones.
/// </summary>
public class ReferenceIncludeTests : SparkTestDriver
{
    public class Author
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class Book
    {
        public string? Id { get; set; }
        public string Title { get; set; } = string.Empty;
        [Reference(typeof(Author), "GetAuthors")]
        public string? AuthorId { get; set; }
    }

    /// <summary>Declares an extra default include beyond the [Reference] property.</summary>
    public class BookActions : DefaultPersistentObjectActions<Book>
    {
        public BookActions(IEntityMapper entityMapper) : base(entityMapper) { }
        public override IReadOnlyCollection<string>? GetDefaultIncludes() => ["ExtraRef"];
    }

    private static ReferenceResolver CreateResolver(IActionsResolver? actionsResolver = null)
        => new(actionsResolver ?? Substitute.For<IActionsResolver>(), null);

    [Fact]
    public async Task ApplyIncludes_emits_include_in_the_query_and_the_referenced_doc_is_a_cache_hit()
    {
        var book = new Book { Title = "Notes" };
        await SeedAsync(async seed =>
        {
            var seedAuthor = new Author { Name = "Ada" };
            await seed.StoreAsync(seedAuthor);
            book.AuthorId = seedAuthor.Id;
            await seed.StoreAsync(book);
        });
        var bookId = book.Id!;

        var resolver = CreateResolver();
        using var session = Store.OpenAsyncSession();

        var queryable = (IRavenQueryable<Book>)resolver.ApplyIncludes(session.Query<Book>(), typeof(Book), ["AuthorId"]);

        queryable.ToString().Should().Contain("include",
            "the include must land in the RQL — before the fix ApplyIncludes silently no-oped");

        var books = await queryable.ToListAsync();
        var authorId = books.Single(b => b.Id == bookId).AuthorId!;

        var requestsBefore = session.Advanced.NumberOfRequests;
        var author = await session.LoadAsync<Author>(authorId);
        author.Name.Should().Be("Ada");
        session.Advanced.NumberOfRequests.Should().Be(requestsBefore,
            "the author was primed by .Include() in the same round-trip — accessing it is a cache hit");
    }

    [Fact]
    public void ResolveIncludePaths_merges_reference_properties_with_GetDefaultIncludes()
    {
        var actionsResolver = Substitute.For<IActionsResolver>();
        actionsResolver.ResolveForType(typeof(Book)).Returns(new BookActions(new EntityMapper(Substitute.For<IModelLoader>())));
        var resolver = CreateResolver(actionsResolver);

        var paths = resolver.ResolveIncludePaths(typeof(Book), typeof(Book));

        paths.Should().Contain("AuthorId", "the [Reference] property is auto-included");
        paths.Should().Contain("ExtraRef", "GetDefaultIncludes() paths are merged in");
    }

    [Fact]
    public void GetDefaultIncludes_returns_null_for_a_type_that_does_not_override_it()
    {
        var actionsResolver = Substitute.For<IActionsResolver>();
        actionsResolver.ResolveForType(typeof(Book))
            .Returns(new DefaultPersistentObjectActions<Book>(new EntityMapper(Substitute.For<IModelLoader>())));
        var resolver = CreateResolver(actionsResolver);

        resolver.GetDefaultIncludes(typeof(Book)).Should().BeNull();
    }
}
