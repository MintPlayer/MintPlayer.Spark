using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// #251's reference check (<see cref="ReferenceTargetValidation"/>) on the database <c>SPARK_TEST_DATABASE</c> picks
/// (#536 M9b). A store with a table per type keys its rows per table, so the check can't load a target untyped, as it
/// does on RavenDB: SqlDemo's first book with an author failed with "System.Object is not a stored type". The
/// RavenDB-only shapes (stale CLR types, subtypes in their own collection) stay in <see cref="ReferenceTargetCollectionTests"/>.
/// The reference's breadcrumb on load is here too, for a typed id (<see cref="RnTypedBook"/>).
/// </summary>
/// <remarks>Fixture names start with <c>Rn</c>: <c>ActionsResolver</c> matches actions classes by simple name.</remarks>
public class NeutralReferenceTargetTests : SparkTestDriver
{
    private static readonly Guid AuthorTypeId = Guid.Parse("536b0000-0000-4000-8000-0000000000a1");
    private static readonly Guid PublisherTypeId = Guid.Parse("536b0000-0000-4000-8000-0000000000a2");
    private static readonly Guid BookTypeId = Guid.Parse("536b0000-0000-4000-8000-0000000000a3");
    private static readonly Guid TypedBookTypeId = Guid.Parse("536b0000-0000-4000-8000-0000000000a4");

    private SparkEndpointFactory? factory;

    public override async Task DisposeAsync()
    {
        if (factory is not null)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private SparkEndpointFactory Factory => factory ??= new SparkEndpointFactory(
        Database, [AuthorModel(), PublisherModel(), BookModel(), TypedBookModel()], security: SparkTestSecurity.Permissive);

    private async Task<string> SeedAsync(object entity)
    {
        using var session = Factory.GetService<ISparkStore>().OpenSession();
        await session.StoreAsync(entity);
        await session.SaveChangesAsync();
        return session.GetId(entity)!;
    }

    /// <summary>One save = one scope = one session, the way a request works.</summary>
    private async Task<PersistentObject> SaveAsync(PersistentObject po)
    {
        using var scope = Factory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IDatabaseAccess>().SavePersistentObjectAsync(po);
    }

    private static PersistentObject Book(string title, string? author) => Book("RnBook", BookTypeId, title, author);

    private static PersistentObject Book(string type, Guid typeId, string title, string? author) => new()
    {
        Name = type,
        ObjectTypeId = typeId,
        Attributes =
        [
            new PersistentObjectAttribute { Name = "Title", DataType = "string", Value = title, IsValueChanged = true },
            new PersistentObjectAttribute { Name = "Author", DataType = "Reference", Value = author, IsValueChanged = true },
        ],
    };

    [Fact]
    public async Task A_reference_to_a_stored_target_saves()
    {
        var author = await SeedAsync(new RnAuthor { Name = "Ursula" });

        var saved = await SaveAsync(Book("The Dispossessed", author));

        using var session = Factory.GetService<ISparkStore>().OpenSession();
        var book = await session.LoadAsync<RnBook>(saved.Id!);
        book!.Author.Should().NotBeNull();
        book.Author!.ToLowerInvariant().Should().Be(author.ToLowerInvariant());
    }

    [Fact]
    public async Task A_reference_to_a_row_of_another_type_is_refused()
    {
        var publisher = await SeedAsync(new RnPublisher { Name = "Harper & Row" });

        var act = () => SaveAsync(Book("Not by a publisher", publisher));

        var thrown = await act.Should().ThrowAsync<SparkSaveValidationException>();
        var error = thrown.Which.Result.Errors.Should().ContainSingle().Which;
        error.AttributeName.Should().Be("Author");
        error.RuleType.Should().Be("referenceTarget");
    }

    [Fact]
    public async Task A_reference_to_a_missing_target_is_refused()
    {
        await SeedAsync(new RnAuthor { Name = "Someone else" });

        var act = () => SaveAsync(Book("By nobody", Guid.NewGuid().ToString()));

        var thrown = await act.Should().ThrowAsync<SparkSaveValidationException>();
        thrown.Which.Result.Errors.Should().ContainSingle().Which.RuleType.Should().Be("referenceTarget");
    }

    [Fact]
    public async Task A_typed_id_reference_loads_with_the_breadcrumb_of_its_target()
    {
        // SqlDemo's Book.Author is an Id<Author>?. The breadcrumb resolver took a reference value only as a string or a
        // list, so it never loaded the boxed typed id's target, and the book's form showed the raw id.
        var author = await SeedAsync(new RnAuthor { Name = "Ursula" });
        var saved = await SaveAsync(Book("RnTypedBook", TypedBookTypeId, "The Lathe of Heaven", author));

        using var scope = Factory.CreateScope();
        var loaded = await scope.ServiceProvider.GetRequiredService<IDatabaseAccess>().GetPersistentObjectAsync(TypedBookTypeId, saved.Id!);

        loaded!.Attributes.Single(a => a.Name == "Author").Breadcrumb.Should().Be("Ursula");
    }

    private static EntityTypeFile AuthorModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = AuthorTypeId, Name = "RnAuthor", ClrType = typeof(RnAuthor).FullName!, Breadcrumb = "{Name}",
            Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
        },
    };

    private static EntityTypeFile PublisherModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = PublisherTypeId, Name = "RnPublisher", ClrType = typeof(RnPublisher).FullName!, Breadcrumb = "{Name}",
            Attributes = [new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
        },
    };

    private static EntityTypeFile BookModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = BookTypeId, Name = "RnBook", ClrType = typeof(RnBook).FullName!, Breadcrumb = "{Title}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Author", DataType = "Reference", ReferenceType = typeof(RnAuthor).FullName },
            ],
        },
    };

    private static EntityTypeFile TypedBookModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = TypedBookTypeId, Name = "RnTypedBook", ClrType = typeof(RnTypedBook).FullName!, Breadcrumb = "{Title}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Author", DataType = "Reference", ReferenceType = typeof(RnAuthor).FullName },
            ],
        },
    };
}

public class RnAuthor
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>Stored, and field-compatible with <see cref="RnAuthor"/>.</summary>
public class RnPublisher
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class RnBook
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;

    [Reference(typeof(RnAuthor))]
    public string? Author { get; set; }
}

/// <summary>SqlDemo's <c>Book</c>: the reference is a typed id (#536 D23).</summary>
public class RnTypedBook
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;

    [Reference(typeof(RnAuthor))]
    public Id<RnAuthor>? Author { get; set; }
}

public class RnTypedBookActions : DefaultPersistentObjectActions<RnTypedBook>
{
    public RnTypedBookActions(IEntityMapper entityMapper) : base(entityMapper) { }
}

public class RnBookActions : DefaultPersistentObjectActions<RnBook>
{
    public RnBookActions(IEntityMapper entityMapper) : base(entityMapper) { }
}
