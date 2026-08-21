using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The runtime facts the #273 breadcrumb-companion design rests on, pinned against a real server:
/// a computed get-only <c>[Breadcrumb]</c> property persists into the document JSON (that is what
/// makes the companion a plain member access), a null-unsafe getter makes the entity unsavable
/// (loud, at serialization time), and the <c>{Name}Sort</c> + <c>[IgnoreProperty]</c> redirect
/// sorts a complex column by its breadcrumb end-to-end through <see cref="QueryExecutor"/>.
/// </summary>
public class BreadcrumbCompanionRuntimeTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("cccc3333-cccc-cccc-cccc-cccc33333333");

    public class Address
    {
        public string City { get; set; } = string.Empty;
        public string Street { get; set; } = string.Empty;

        /// <summary>The sanctioned marker shape: computed, null-safe, hidden from the model.</summary>
        [Breadcrumb, IgnoreProperty]
        public string Crumb => $"{Street}, {City}";
    }

    public class Person
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Address? Address { get; set; }

        /// <summary>Entity-level computed chain composing the embedded crumb.</summary>
        [IgnoreProperty]
        public string? Crumb => Address == null ? null : $"{Name} @ {Address.Crumb}";
    }

    public class UnsafeDescription
    {
        public string Label { get; set; } = string.Empty;
    }

    public class UnsafeEntity
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public UnsafeDescription? Description { get; set; }

        /// <summary>Deliberately NOT null-safe — the failure mode the docs warn about.</summary>
        [Breadcrumb, IgnoreProperty]
        public string Crumb => Description!.Label + " / " + Name;
    }

    /// <summary>Mirrors the generated emission for a breadcrumb-carrying complex property.</summary>
    public class People_Overview : AbstractIndexCreationTask<Person>
    {
        public People_Overview()
        {
            Map = people => from person in people
                            select new
                            {
                                person.Name,
                                person.Address,
                                AddressSort = person.Address!.Crumb,
                            };
            Index(nameof(VPerson.Address), FieldIndexing.No);
            StoreAllFields(FieldStorage.Yes);
            SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        }
    }

    [FromIndex(typeof(People_Overview))]
    public class VPerson
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Address? Address { get; set; }

        [IgnoreProperty]
        public string? AddressSort { get; set; }
    }

    public class TestContext : SparkContext
    {
        public IRavenQueryable<Person> People => Session.Query<Person>();
    }

    private static EntityTypeFile PersonModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = PersonTypeId,
            Name = "Person",
            ClrType = typeof(Person).FullName!,
            Breadcrumb = "{Name}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
                // Declared because the test sorts by it. Synchronization would emit this for a
                // complex property, and since #295 a sort column must name a modelled attribute of
                // the query surface — an undeclared one is refused rather than resolved by reflection.
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Address", DataType = "AsDetail" },
            ],
        },
    };

    private async Task SeedPeopleAsync()
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new Person { Name = "Alice", Address = new Address { City = "Ghent", Street = "Main" } }, "people/1");
        await session.StoreAsync(new Person { Name = "Bob", Address = null }, "people/2");
        await session.StoreAsync(new Person { Name = "Carol", Address = new Address { City = "Antwerp", Street = "Side" } }, "people/3");
        await session.SaveChangesAsync();

        await new People_Overview().ExecuteAsync(Store);
        await RavenIndexHelper.WaitForNonStaleAsync(Store);
    }

    /// <summary>
    /// The design's load-bearing fact: a computed get-only property serializes into the stored
    /// document, at the embedded level and as an entity-level composition. If a serializer change
    /// ever suppresses get-only members, every breadcrumb companion silently empties — this is the
    /// test that catches it.
    /// </summary>
    [Fact]
    public async Task Computed_breadcrumb_properties_persist_into_the_stored_document()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Person { Name = "Alice", Address = new Address { City = "Ghent", Street = "Main" } }, "people/1");
            await session.SaveChangesAsync();
        }

        using var reader = Store.OpenAsyncSession();
        var command = new Raven.Client.Documents.Commands.GetDocumentsCommand(
            Store.Conventions, "people/1", includes: null, metadataOnly: false);
        await reader.Advanced.RequestExecutor.ExecuteAsync(command, reader.Advanced.Context);

        var document = command.Result.Results[0]!.ToString()!;

        document.Should().Contain("\"Crumb\":\"Main, Ghent\"", "the embedded computed property persists");
        document.Should().Contain("\"Crumb\":\"Alice @ Main, Ghent\"", "the entity-level computed chain persists");
    }

    [Fact]
    public async Task A_null_unsafe_computed_breadcrumb_makes_the_entity_unsavable()
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new UnsafeEntity { Name = "Boom", Description = null });

        var act = async () => await session.SaveChangesAsync();

        // Newtonsoft wraps the getter's NullReferenceException at serialization time.
        (await act.Should().ThrowAsync<Exception>())
            .Which.GetType().Name.Should().Be("JsonSerializationException");
    }

    [Fact]
    public async Task Sorting_by_the_companion_orders_by_the_breadcrumb_nulls_first()
    {
        await SeedPeopleAsync();

        using var session = Store.OpenAsyncSession();
        var results = await session.Query<VPerson, People_Overview>()
            .OrderBy(v => v.AddressSort)
            .ProjectInto<VPerson>()
            .ToListAsync();

        results.Select(v => v.Name).Should().Equal("Bob", "Alice", "Carol");
        results.Single(v => v.Name == "Alice").Address!.City.Should().Be("Ghent",
            "the complex field itself stays stored and projectable");
    }

    [Fact]
    public async Task QueryExecutor_redirects_a_sort_on_the_complex_column_to_the_companion()
    {
        await SeedPeopleAsync();

        await using var factory = new SparkEndpointFactory<TestContext>(Store, [PersonModel()],
            configureIndexCatalog: catalog =>
            {
                catalog.RegisterIndex(typeof(People_Overview));
                catalog.RegisterProjection(typeof(VPerson), typeof(People_Overview));
            });

        var executor = factory.GetService<IQueryExecutor>();
        var result = await executor.ExecuteQueryAsync(new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "PeopleByAddress",
            Source = "Database.People",
            IndexName = "People_Overview",
            SortColumns = [new SortColumn { Property = "Address", Direction = "asc" }],
        });

        var names = result.Data
            .Select(po => po.Attributes.Single(a => a.Name == "Name").Value?.ToString())
            .ToList();

        names.Should().Equal("Bob", "Alice", "Carol");
    }
}
