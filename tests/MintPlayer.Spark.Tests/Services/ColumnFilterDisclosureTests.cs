using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Issue #431 — per-column filters narrow, and a refused one narrows nothing without saying so.
/// </summary>
/// <remarks>
/// The refusal is silent for the same reason the sort gate's is: a caller who can tell "that column
/// exists but you may not filter it" from "that column does not exist" has an enumeration oracle for
/// the type's attributes. So every assertion here is on rows returned, never on an error.
/// <para>
/// The clause position is asserted on emitted RQL rather than on results. A filter that lands in the
/// wrong place can return exactly the right rows on a permissive caller and still have destroyed the
/// row-security predicate — the measured RavenDB behaviour where an explicit <c>SearchOptions</c>
/// leaks onto the adjacent clause turns a security filter into an alternative, silently, and returns
/// plausible rows while doing it.
/// </para>
/// </remarks>
public class ColumnFilterDisclosureTests : SparkTestDriver
{
    private static readonly Guid CrateTypeId = Guid.Parse("cccc3333-cccc-cccc-cccc-cccc33333333");

    public class Crate
    {
        public string? Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string Classified { get; set; } = string.Empty;

        /// <summary>
        /// A non-nullable value type, which is a different case from every other column here: a value
        /// the wire could not convert becomes null, and null is not a legal constant of this type.
        /// </summary>
        public int Weight { get; set; }

        /// <summary>
        /// A collection column. RavenDB indexes it as multi-valued terms, so a filter on it must ask
        /// whether the collection CONTAINS the value — comparing the collection itself coerces to
        /// null and silently matches the wrong rows. Nullable on purpose: a document that never wrote
        /// the field projects as null, and that is the case which throws in an in-memory custom query.
        /// </summary>
        public string[]? Tags { get; set; }
    }

    public class Crates_Overview : AbstractIndexCreationTask<Crate>
    {
        public Crates_Overview()
        {
            Map = crates => from c in crates select new { c.Label, c.Region, c.Classified, c.Weight, c.Tags };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    public class TestContext : SparkContext
    {
        public IRavenQueryable<Crate> Crates => Session.Query<Crate>();
    }

    private static EntityTypeFile CrateModel(bool? regionCanFilter = null) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CrateTypeId,
            Name = "Crate",
            ClrType = typeof(Crate).FullName!,
            Breadcrumb = "{Label}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Crate.Label), DataType = "string" },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = nameof(Crate.Region), DataType = "string",
                    CanFilter = regionCanFilter,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = nameof(Crate.Classified), DataType = "string",
                    ShowedOn = EShowedOn.PersistentObject,
                },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Crate.Weight), DataType = "int" },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = nameof(Crate.Tags), DataType = "string", IsArray = true,
                },
            ],
        },
    };

    private SparkEndpointFactory<TestContext>? _factory;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new Crates_Overview().ExecuteAsync(Store);

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new Crate { Label = "one", Region = "eu", Classified = "red", Weight = 10, Tags = ["fragile", "urgent"] });
            await session.StoreAsync(new Crate { Label = "two", Region = "us", Classified = "red", Weight = 20, Tags = ["urgent"] });

            // No tags at all: the field is absent from the document, so it projects as null. Every
            // collection filter has to survive this row without throwing and without matching it.
            await session.StoreAsync(new Crate { Label = "three", Region = "ap", Classified = "blue", Weight = 30, Tags = null });
        });
        await Store.WaitForIndexingAsync();
    }

    public override async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private IQueryExecutor Executor(EntityTypeFile? model = null)
    {
        _factory = new SparkEndpointFactory<TestContext>(
            Store, [model ?? CrateModel()],
            configureIndexCatalog: catalog => catalog.RegisterIndex(typeof(Crates_Overview)));

        return _factory.GetService<IQueryExecutor>();
    }

    private static SparkQuery Query() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Crates",
        Source = "Database.Crates",
    };

    [Fact]
    public async Task Includes_keep_only_the_named_values()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Crate.Region), Includes = ["eu", "us"] }]);

        result.TotalItems.Should().Be(2, "two of three rows carry one of the included values");
    }

    [Fact]
    public async Task A_filter_on_a_collection_column_asks_whether_it_contains_the_value()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Crate.Tags), Includes = ["fragile"] }]);

        // Exactly one crate carries "fragile". Before the fix this compared the collection itself:
        // ConvertFilterValue could not coerce "fragile" onto string[], answered null, and the filter
        // became `x.Tags == null` — which selects the untagged crate, the one row the caller did not
        // ask for. A count of 1 is therefore not enough on its own; the identity below is the point.
        result.TotalItems.Should().Be(1, "one crate is tagged fragile");
        result.Items.Single().Values.Single(v => v.Key == nameof(Crate.Label)).Value.Should().Be("one",
            "the matching row must be the tagged crate, not the untagged one a null comparison selects");
    }

    [Fact]
    public async Task A_collection_filter_ORs_its_values_and_skips_rows_with_no_collection()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Crate.Tags), Includes = ["fragile", "urgent"] }]);

        // Both tagged crates match, and the crate whose Tags field is absent is not swept in. That
        // row also proves the expression survives a null collection rather than throwing.
        result.TotalItems.Should().Be(2, "two crates carry one of the two tags; the untagged one carries neither");
    }

    [Fact]
    public async Task Excluding_a_tag_does_not_hide_the_rows_that_have_no_tags()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Crate.Tags), Excludes = ["fragile"] }]);

        // "Not tagged fragile" has to include the untagged crate: it is not fragile. An exclusion is
        // the negation of the containment test, so this is the case that catches a null guard placed
        // inside the negation instead of around the containment.
        result.TotalItems.Should().Be(2, "the urgent-only crate and the untagged one are both not fragile");
    }

    [Fact]
    public async Task Excludes_are_the_inverse_and_not_a_separate_flag()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Crate.Region), Excludes = ["eu", "us"] }]);

        result.TotalItems.Should().Be(1, "excluding the two included above leaves exactly the third");
    }

    [Fact]
    public async Task Filters_on_different_columns_intersect()
    {
        var executor = Executor();

        // Deliberately satisfiable individually but not together.
        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters:
            [
                new QueryColumnFilter { Name = nameof(Crate.Region), Includes = ["eu"] },
                new QueryColumnFilter { Name = nameof(Crate.Label), Includes = ["two"] },
            ]);

        result.TotalItems.Should().Be(0, "columns AND together; values within one column OR");
    }

    [Fact]
    public async Task A_filter_on_a_column_off_the_query_surface_is_refused_silently()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Crate.Classified), Includes = ["red"] }]);

        result.TotalItems.Should().Be(3,
            "the filter is refused, so nothing is narrowed — and it is refused without an error, "
            + "because a distinguishable refusal answers whether the attribute exists");
    }

    [Fact]
    public async Task A_filter_on_a_column_that_may_not_be_filtered_is_refused_silently()
    {
        var executor = Executor(CrateModel(regionCanFilter: false));

        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Crate.Region), Includes = ["eu"] }]);

        result.TotalItems.Should().Be(3, "canFilter:false refuses the filter and says nothing");
    }

    [Fact]
    public async Task An_unconvertible_value_narrows_to_nothing_rather_than_throwing()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Crate.Region), Includes = [new { bogus = true }] }]);

        result.TotalItems.Should().Be(0,
            "a filter is caller input: a malformed value matches nothing rather than 500ing, and the "
            + "outcome is indistinguishable from a value that simply matches no row");
    }

    [Fact]
    public async Task An_unconvertible_value_on_a_value_type_column_narrows_to_nothing_rather_than_throwing()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Crate.Weight), Includes = ["not-a-number"] }]);

        // The sibling test above uses a string column, where a failed conversion yields null and
        // Expression.Constant(null, typeof(string)) is perfectly legal — so it never exercised this.
        // On a non-nullable value type that same null is not a constructible constant and the request
        // used to 500, which also made the refusal a type oracle: a 500 and a 200-with-no-rows are
        // trivially distinguishable, which is exactly what the silence is supposed to prevent.
        result.TotalItems.Should().Be(0);
    }

    [Fact]
    public async Task A_filter_mixing_a_valid_and_an_unconvertible_value_keeps_the_valid_one()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Crate.Weight), Includes = [20, "not-a-number"] }]);

        // Skipping the unholdable value must not take the rest of the chain with it, and must not
        // widen it either — the answer is the rows matching what could be understood.
        result.TotalItems.Should().Be(1);
    }

    [Fact]
    public async Task The_filter_clause_does_not_displace_the_row_security_predicate()
    {
        // Attached BEFORE the executor is resolved: Raven copies the handler list when a session is
        // constructed, so a recorder attached afterwards silently records nothing — which reads as a
        // passing assertion about an empty collection rather than as a broken test.
        using var rql = RqlRecorder.Attach(Store);
        var executor = Executor();

        await executor.ExecuteQueryAsync(Query(), search: "one",
            columnFilters: [new QueryColumnFilter { Name = nameof(Crate.Region), Includes = ["eu"] }]);

        // More than one statement may be emitted: paging pushdown (#431 M14) issues a count before
        // the page. The filtering statement is the one carrying the where clause.
        var emitted = rql.Should().Contain(q => q.Contains("where")).Which;

        // The shape that matters: the filter is its OWN conjunct, ANDed with the search group —
        //   where (Region = $p0) and (search(...) or search(...) or search(...))
        // The `or`s inside the parentheses are RavenDB grouping consecutive Search clauses, which is
        // the documented and intended shape. What must never appear is the filter itself becoming an
        // alternative to its neighbours, which is what an explicit SearchOptions would cause by
        // leaking onto the adjacent clause.
        emitted.Should().Contain("(Region = ",
            "the column filter composes as plain equality, not through Search");
        emitted.Should().Contain(") and (",
            "the filter and the search group are separate conjuncts");
        emitted.Should().NotContain("or (Region",
            "a filter that becomes an alternative widens the result instead of narrowing it, and "
            + "the same mistake against the row-security predicate is a silent bypass");
    }
}
