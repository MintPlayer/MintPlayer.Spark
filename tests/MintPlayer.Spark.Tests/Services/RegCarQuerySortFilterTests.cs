using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Sorting and filtering a <c>SparkQuery</c> on a <see cref="DateTimeOffset"/> column, over the shape
/// Fleet ships (<c>Car.RegisteredAt</c> behind <c>Cars_Overview</c> / <c>VCar</c>), seeded from a JSON
/// fixture. See <c>docs/datetimeoffset_query_sort_filter_PRD.md</c>; the T-numbers below are its §6.
/// </summary>
/// <remarks>
/// <para>
/// The fixture is built so that <b>instant order differs from wall-clock order and from the
/// ordinal order of the written strings</b>: by instant it is F G A B C=D E, by wall clock F G B D C E A.
/// A sort that compared the text, or the local clock, cannot pass by luck — and every sort test also
/// asserts that guard, because a fixture that happened to be ordered both ways would prove nothing.
/// </para>
/// <para>
/// ⚠️ <see cref="DateTimeOffset.Equals(DateTimeOffset)"/> compares instants. Offsets are therefore
/// asserted with <see cref="DateTimeOffset.EqualsExact"/>, never with an equality assertion, which
/// would pass against the very value this class exists to catch.
/// </para>
/// <para>
/// The test-local types mirror the generated Fleet ones under distinct names, because assembly-wide
/// scans match entities and actions by simple name.
/// </para>
/// </remarks>
public class RegCarQuerySortFilterTests(RegCarQuerySortFilterTests.SeededRegCars host)
    : SparkSharedTestDriver(host), IClassFixture<RegCarQuerySortFilterTests.SeededRegCars>, IDisposable
{
    internal static readonly Guid RegCarTypeId = Guid.Parse("ffff7777-ffff-ffff-ffff-ffff77777777");
    internal const string FixturePath = "Services/Data/reg-cars.json";

    public class RegCar
    {
        public string? Id { get; set; }
        public string LicensePlate { get; set; } = string.Empty;
        public string Brand { get; set; } = string.Empty;
        public string Vin { get; set; } = string.Empty;
        public string[] Tags { get; set; } = [];
        public DateTimeOffset RegisteredAt { get; set; }
        public DateTimeOffset? DeregisteredAt { get; set; }
    }

    /// <summary>The shape the generator emits for <c>[GenerateIndex]</c> on Fleet's <c>Car</c>.</summary>
    public class RegCars_Overview : AbstractIndexCreationTask<RegCar>
    {
        public RegCars_Overview()
        {
            Map = cars => from car in cars
                          select new
                          {
                              car.LicensePlate,
                              car.Brand,
                              car.Vin,
                              car.Tags,
                              car.RegisteredAt,
                              car.DeregisteredAt,
                              RegisteredAtRaw = new SparkIndexValue<DateTimeOffset> { V = car.RegisteredAt },
                              DeregisteredAtRaw = new SparkIndexValue<DateTimeOffset?> { V = car.DeregisteredAt },
                          };
            // Required on Corax, which otherwise parks the index in Error with zero entries.
            Index(nameof(VRegCar.RegisteredAtRaw), FieldIndexing.No);
            Index(nameof(VRegCar.DeregisteredAtRaw), FieldIndexing.No);
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(RegCars_Overview))]
    public class VRegCar
    {
        public string? Id { get; set; }
        public string LicensePlate { get; set; } = string.Empty;
        public string Brand { get; set; } = string.Empty;
        public string Vin { get; set; } = string.Empty;
        public string[] Tags { get; set; } = [];
        public DateTimeOffset RegisteredAt { get; set; }
        public DateTimeOffset? DeregisteredAt { get; set; }

        [IgnoreProperty]
        public SparkIndexValue<DateTimeOffset>? RegisteredAtRaw { get; set; }

        [IgnoreProperty]
        public SparkIndexValue<DateTimeOffset?>? DeregisteredAtRaw { get; set; }
    }

    /// <summary>
    /// The fixture's rows as C#: the single statement of what <c>reg-cars.json</c> holds, used both to
    /// check that the import kept every offset (T0) and as the data of the in-memory custom queries.
    /// </summary>
    internal static readonly RegCar[] FixtureRows =
    [
        Row("A", "Volvo", ["fleet"], new(2027, 3, 2, 1, 0, 0, TimeSpan.FromHours(12)), new(2027, 4, 1, 10, 0, 0, TimeSpan.FromHours(2))),
        Row("B", "Audi", ["lease"], new(2027, 3, 1, 9, 0, 0, TimeSpan.FromHours(-5)), null),
        Row("C", "Volvo", [], new(2027, 3, 1, 16, 0, 0, TimeSpan.FromHours(1)), new(2027, 4, 1, 9, 30, 0, TimeSpan.Zero)),
        Row("D", "Audi", ["fleet", "lease"], new(2027, 3, 1, 15, 0, 0, TimeSpan.Zero), new(2027, 3, 31, 23, 0, 0, TimeSpan.FromHours(-5))),
        Row("E", "Volvo", ["fleet"], new(2027, 3, 1, 22, 15, 0, new TimeSpan(5, 45, 0)), null),
        Row("F", "Audi", ["lease"], new(2026, 12, 31, 23, 59, 0, TimeSpan.FromHours(-8)), new(2027, 4, 1, 12, 0, 0, TimeSpan.FromHours(5))),
        // G has no DeregisteredAt property at all in the JSON — absent, not null.
        Row("G", "Audi", ["fleet"], new(2027, 2, 1, 0, 0, 0, TimeSpan.Zero), null),
    ];

    private static RegCar Row(string plate, string brand, string[] tags, DateTimeOffset registered, DateTimeOffset? deregistered) => new()
    {
        Id = $"regcars/{plate.ToLowerInvariant()}",
        LicensePlate = plate,
        Brand = brand,
        Vin = $"VIN-{plate}",
        Tags = tags,
        RegisteredAt = registered,
        DeregisteredAt = deregistered,
    };

    /// <summary>Custom queries over the fixture rows, for the two paths that sort in process.</summary>
    public class RegCarActions(IEntityMapper entityMapper) : DefaultPersistentObjectActions<RegCar>(entityMapper)
    {
        /// <summary>Not an <see cref="IQueryable"/>: the executor sorts the mapped rows itself.</summary>
        public IEnumerable<RegCar> ListRegCars(CustomQueryArgs _) => FixtureRows;

        /// <summary>An <c>EnumerableQuery</c>: the executor composes <c>OrderBy</c>/<c>ThenBy</c> onto it.</summary>
        public IQueryable<RegCar> InMemoryRegCars(CustomQueryArgs _) => FixtureRows.AsQueryable();
    }

    public class RegContext : SparkContext
    {
        public IRavenQueryable<RegCar> RegCars => Session.Query<RegCar>();
    }

    /// <summary>
    /// Mirrors Fleet's <c>Car.json</c> for the attributes under test. Deliberately no entity-level
    /// <c>IndexName</c>: the index is bound per query, so <see cref="Unindexed"/> really reads the
    /// collection through an auto index.
    /// </summary>
    internal static EntityTypeFile RegCarModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = RegCarTypeId,
            Name = "RegCar",
            ClrType = typeof(RegCar).FullName!,
            Breadcrumb = "{LicensePlate}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(RegCar.LicensePlate), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(RegCar.Brand), DataType = "string" },
                // On the query surface but declared unsortable: a caller-supplied sort by it is refused.
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(RegCar.Vin), DataType = "string", CanSort = false },
                // A collection: refused as a sort column even when the model itself declares it.
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(RegCar.Tags), DataType = "string", IsArray = true },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(RegCar.RegisteredAt), DataType = "datetime" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(RegCar.DeregisteredAt), DataType = "datetime" },
            ],
        },
    };

    /// <summary>The fixture imported and indexed once for the class; every case only reads.</summary>
    public sealed class SeededRegCars : SharedSparkHost<RegContext>
    {
        protected override async Task BeforeHostAsync()
        {
            await new RegCars_Overview().ExecuteAsync(Store);
            // The importer waits for non-stale indexes itself.
            await JsonFixtureImporter.ImportAsync(Store, Path.Combine(AppContext.BaseDirectory, FixturePath));
        }

        protected override SparkEndpointFactory<RegContext> CreateFactory() =>
            new(Store, [RegCarModel()],
                configureIndexCatalog: catalog =>
                {
                    catalog.RegisterIndex(typeof(RegCars_Overview));
                    catalog.RegisterProjection(typeof(VRegCar), typeof(RegCars_Overview));
                });
    }

    // --- Plumbing -------------------------------------------------------------------------------

    private IServiceScope? _scope;

    public void Dispose() => _scope?.Dispose();

    private IQueryExecutor Executor()
    {
        _scope?.Dispose();
        _scope = host.Factory.CreateScope();
        return _scope.ServiceProvider.GetRequiredService<IQueryExecutor>();
    }

    /// <summary>Fleet's <c>Registrations</c>: index-bound, declared order <c>RegisteredAt desc</c>.</summary>
    internal static SparkQuery Registrations(params SortColumn[] declared) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Registrations",
        Source = "Database.RegCars",
        IndexName = "RegCars_Overview",
        EntityType = "RegCar",
        SortColumns = declared.Length > 0 ? declared : [Desc(nameof(RegCar.RegisteredAt))],
    };

    private static SparkQuery Unindexed(params SortColumn[] sort) => new()
    {
        Id = Guid.NewGuid(),
        Name = "RegistrationsUnindexed",
        Source = "Database.RegCars",
        EntityType = "RegCar",
        SortColumns = sort,
    };

    private static SparkQuery Custom(string method, params SortColumn[] declared) => new()
    {
        Id = Guid.NewGuid(),
        Name = method,
        Source = $"Custom.{method}",
        EntityType = "RegCar",
        SortColumns = declared,
    };

    internal static SortColumn Asc(string property) => new() { Property = property, Direction = "asc" };
    internal static SortColumn Desc(string property) => new() { Property = property, Direction = "desc" };

    /// <summary>A filter value exactly as the endpoint receives it: a JSON element, not a CLR value.</summary>
    internal static JsonElement Wire(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal static string[] Plates(QueryResult result)
        => result.Items.Select(item => (string)Value(item, nameof(RegCar.LicensePlate))!).ToArray();

    internal static object? Value(QueryResultItem item, string attribute)
        => item.Values.Single(v => v.Key == attribute).Value;

    private static RegCar Fixture(string plate) => FixtureRows.Single(r => r.LicensePlate == plate);

    /// <summary>The order the same plates would have if the column were compared as wall-clock text.</summary>
    private static string[] WallClockOrder(IEnumerable<string> plates, Func<RegCar, DateTimeOffset?> select)
        => plates.Select(Fixture)
            .OrderBy(r => select(r)?.DateTime)
            .ThenBy(r => r.LicensePlate, StringComparer.Ordinal)
            .Select(r => r.LicensePlate)
            .ToArray();

    // --- T0: the fixture itself -------------------------------------------------------------------

    [Fact]
    public async Task T0_the_imported_fixture_keeps_every_offset()
    {
        using var session = Store.OpenAsyncSession();

        foreach (var expected in FixtureRows)
        {
            var loaded = await session.LoadAsync<RegCar>(expected.Id);

            loaded.Should().NotBeNull($"{expected.Id} must be imported");
            loaded!.RegisteredAt.EqualsExact(expected.RegisteredAt).Should().BeTrue(
                $"{expected.Id}: the fixture says {expected.RegisteredAt:O}, the store holds {loaded.RegisteredAt:O}. " +
                "An importer that parses dates rewrites them to the machine's clock and drops the offset.");
            (loaded.DeregisteredAt is null).Should().Be(expected.DeregisteredAt is null, $"{expected.Id} DeregisteredAt");
            if (expected.DeregisteredAt is { } deregistered)
                loaded.DeregisteredAt!.Value.EqualsExact(deregistered).Should().BeTrue(
                    $"{expected.Id}: expected {deregistered:O}, got {loaded.DeregisteredAt:O}");
        }
    }

    // --- Sorting on the index-bound path (Fleet's shape) ------------------------------------------

    [Fact]
    public async Task T1_ascending_is_by_instant()
    {
        var result = await Executor().ExecuteQueryAsync(
            Registrations(Asc(nameof(RegCar.RegisteredAt)), Asc(nameof(RegCar.LicensePlate))));

        var plates = Plates(result);
        plates.Should().Equal("F", "G", "A", "B", "C", "D", "E");
        plates.Should().NotEqual(WallClockOrder(plates, r => r.RegisteredAt),
            "the fixture must tell instant order from wall-clock order, or this test proves nothing");
    }

    [Fact]
    public async Task T2_descending_is_the_exact_reverse()
    {
        var result = await Executor().ExecuteQueryAsync(
            Registrations(Desc(nameof(RegCar.RegisteredAt)), Desc(nameof(RegCar.LicensePlate))));

        Plates(result).Should().Equal("E", "D", "C", "B", "A", "G", "F");
    }

    [Fact]
    public async Task T3_without_a_requested_sort_the_declared_order_applies()
    {
        var result = await Executor().ExecuteQueryAsync(Registrations());

        var instants = result.Items.Select(i => ((DateTimeOffset)Value(i, nameof(RegCar.RegisteredAt))!).UtcTicks).ToArray();
        instants.Should().BeInDescendingOrder("Registrations declares RegisteredAt desc");
        Plates(result).First().Should().Be("E");
        Plates(result).Last().Should().Be("F");
    }

    [Fact]
    public async Task T4_a_requested_sort_replaces_the_declared_one()
    {
        var query = Registrations().WithSortColumns([Asc(nameof(RegCar.LicensePlate))]);

        var result = await Executor().ExecuteQueryAsync(query);

        Plates(result).Should().Equal("A", "B", "C", "D", "E", "F", "G");
    }

    [Fact]
    public async Task T5_a_tie_on_the_instant_is_broken_by_the_next_column_in_both_directions()
    {
        var executor = Executor();

        var ascending = await executor.ExecuteQueryAsync(
            Registrations(Asc(nameof(RegCar.RegisteredAt)), Asc(nameof(RegCar.LicensePlate))));
        var descending = await executor.ExecuteQueryAsync(
            Registrations(Asc(nameof(RegCar.RegisteredAt)), Desc(nameof(RegCar.LicensePlate))));

        // C (16:00+01:00) and D (15:00+00:00) are the same instant.
        Plates(ascending).Skip(4).Take(2).Should().Equal("C", "D");
        Plates(descending).Skip(4).Take(2).Should().Equal("D", "C");
    }

    [Fact]
    public async Task T6_a_date_as_the_second_sort_column_orders_within_each_group_by_instant()
    {
        var executor = Executor();

        var ascending = await executor.ExecuteQueryAsync(
            Registrations(Asc(nameof(RegCar.Brand)), Asc(nameof(RegCar.RegisteredAt))));
        var descending = await executor.ExecuteQueryAsync(
            Registrations(Asc(nameof(RegCar.Brand)), Desc(nameof(RegCar.RegisteredAt))));

        Plates(ascending).Should().Equal("F", "G", "B", "D", "A", "C", "E");
        Plates(descending).Should().Equal("D", "B", "G", "F", "E", "C", "A");
    }

    [Fact]
    public async Task T7_pages_concatenate_to_the_full_sorted_list()
    {
        var executor = Executor();
        var query = Registrations(Asc(nameof(RegCar.RegisteredAt)), Asc(nameof(RegCar.LicensePlate)));

        var paged = new List<string>();
        for (var skip = 0; skip < FixtureRows.Length; skip += 2)
        {
            var page = await executor.ExecuteQueryAsync(query, skip: skip, take: 2);
            page.TotalItems.Should().Be(FixtureRows.Length);
            paged.AddRange(Plates(page));
        }

        paged.Should().Equal("F", "G", "A", "B", "C", "D", "E");
    }

    [Fact]
    public async Task T8_every_sorted_row_keeps_its_original_offset()
    {
        var result = await Executor().ExecuteQueryAsync(
            Registrations(Asc(nameof(RegCar.RegisteredAt)), Asc(nameof(RegCar.LicensePlate))));

        foreach (var item in result.Items)
        {
            var plate = (string)Value(item, nameof(RegCar.LicensePlate))!;
            var actual = (DateTimeOffset)Value(item, nameof(RegCar.RegisteredAt))!;
            var expected = Fixture(plate).RegisteredAt;

            actual.EqualsExact(expected).Should().BeTrue($"{plate}: expected {expected:O}, got {actual:O}");
        }
    }

    [Fact]
    public async Task T9_rows_without_a_value_sort_first_ascending_and_last_descending()
    {
        var executor = Executor();

        var ascending = Plates(await executor.ExecuteQueryAsync(
            Registrations(Asc(nameof(RegCar.DeregisteredAt)), Asc(nameof(RegCar.LicensePlate)))));
        var descending = Plates(await executor.ExecuteQueryAsync(
            Registrations(Desc(nameof(RegCar.DeregisteredAt)), Asc(nameof(RegCar.LicensePlate)))));

        // B and E hold an explicit null, G has no property at all. Corax and Lucene disagree on the
        // order WITHIN that group (PRD SP-A), so only the group is pinned.
        ascending.Take(3).Should().BeEquivalentTo(["B", "E", "G"]);
        ascending.Skip(3).Should().Equal("D", "F", "A", "C");
        ascending.Skip(3).Should().NotEqual(WallClockOrder(ascending.Skip(3), r => r.DeregisteredAt));

        descending.Take(4).Should().Equal("C", "A", "F", "D");
        descending.Skip(4).Should().BeEquivalentTo(["B", "E", "G"]);
    }

    [Fact]
    public async Task T10_sort_and_filter_combine()
    {
        var result = await Executor().ExecuteQueryAsync(
            Registrations(Desc(nameof(RegCar.RegisteredAt)), Asc(nameof(RegCar.LicensePlate))),
            columnFilters: [new QueryColumnFilter { Name = nameof(RegCar.Brand), Includes = [Wire("\"Audi\"")] }]);

        Plates(result).Should().Equal("D", "B", "G", "F");
    }

    // --- Filtering --------------------------------------------------------------------------------

    [Fact]
    public async Task T11_another_spelling_of_an_instant_selects_every_row_at_that_instant()
    {
        // 10:00-05:00 is 15:00Z, the instant C and D share — written in neither row's own offset.
        var result = await Executor().ExecuteQueryAsync(Registrations(Asc(nameof(RegCar.LicensePlate))),
            columnFilters: [new QueryColumnFilter
            {
                Name = nameof(RegCar.RegisteredAt),
                Includes = [Wire("\"2027-03-01T10:00:00-05:00\"")],
            }]);

        Plates(result).Should().Equal("C", "D");
    }

    [Fact]
    public async Task T12_several_includes_select_their_union()
    {
        var result = await Executor().ExecuteQueryAsync(Registrations(Asc(nameof(RegCar.LicensePlate))),
            columnFilters: [new QueryColumnFilter
            {
                Name = nameof(RegCar.RegisteredAt),
                Includes = [Wire("\"2027-03-02T01:00:00+12:00\""), Wire("\"2026-12-31T23:59:00-08:00\"")],
            }]);

        Plates(result).Should().Equal("A", "F");
    }

    [Fact]
    public async Task T13_excludes_remove_every_row_at_that_instant()
    {
        var result = await Executor().ExecuteQueryAsync(Registrations(Asc(nameof(RegCar.LicensePlate))),
            columnFilters: [new QueryColumnFilter
            {
                Name = nameof(RegCar.RegisteredAt),
                Excludes = [Wire("\"2027-03-01T15:00:00Z\"")],
            }]);

        Plates(result).Should().Equal("A", "B", "E", "F", "G");
    }

    /// <summary>
    /// ⚠️ Pins a known limitation, not a wish. G has no <c>DeregisteredAt</c> property at all, reads
    /// back as <c>null</c> like B and E, and yet a null filter cannot select it: an absent field
    /// carries no null term in RavenDB, and no LINQ shape selects "absent or null". Measured and
    /// documented in <see cref="AbsentVersusNullFieldTests"/>. Absence only arises from schema
    /// evolution, since Spark's own writes store an explicit null. When this starts returning G,
    /// the limitation has been lifted; update both tests.
    /// </summary>
    [Fact]
    public async Task T14_including_null_selects_the_explicit_nulls_but_not_an_absent_field()
    {
        var result = await Executor().ExecuteQueryAsync(Registrations(Asc(nameof(RegCar.LicensePlate))),
            columnFilters: [new QueryColumnFilter { Name = nameof(RegCar.DeregisteredAt), Includes = [Wire("null")] }]);

        Plates(result).Should().Equal("B", "E");
    }

    [Fact]
    public async Task T17_an_unparseable_value_selects_nothing_rather_than_failing()
    {
        var result = await Executor().ExecuteQueryAsync(Registrations(Asc(nameof(RegCar.LicensePlate))),
            columnFilters: [new QueryColumnFilter { Name = nameof(RegCar.RegisteredAt), Includes = [Wire("\"not a date\"")] }]);

        result.TotalItems.Should().Be(0);
    }

    [Fact]
    public async Task T18_distinct_values_are_ordered_by_instant_and_each_selects_its_rows()
    {
        var executor = Executor();
        var query = Registrations(Asc(nameof(RegCar.LicensePlate)));

        var distincts = await executor.GetDistinctValuesAsync(query, nameof(RegCar.RegisteredAt));

        // Seven rows, six instants: C and D are one value, whichever offset is shown for it.
        distincts.Matching.Should().HaveCount(6);
        distincts.Matching.Select(d => ((DateTimeOffset)d.Value!).UtcTicks).Should().BeInAscendingOrder();

        foreach (var offered in distincts.Matching)
        {
            // Round-trip the value the way the browser does: serialized, then posted back.
            var posted = Wire(JsonSerializer.Serialize(offered.Value));
            var result = await executor.ExecuteQueryAsync(query,
                columnFilters: [new QueryColumnFilter { Name = nameof(RegCar.RegisteredAt), Includes = [posted] }]);

            var instant = ((DateTimeOffset)offered.Value!).UtcTicks;
            var expected = FixtureRows.Where(r => r.RegisteredAt.UtcTicks == instant).Select(r => r.LicensePlate).ToArray();
            // Both sides are in plate order: the query sorts by plate, FixtureRows is listed A to G.
            result.TotalItems.Should().Be(expected.Length, $"the panel offered '{offered.Label}'");
            Plates(result).Should().Equal(expected);
        }
    }

    // --- Other execution paths ----------------------------------------------------------------

    [Fact]
    public async Task T20_a_collection_source_without_an_index_also_sorts_by_instant()
    {
        var executor = Executor();

        var ascending = await executor.ExecuteQueryAsync(Unindexed(Asc(nameof(RegCar.RegisteredAt)), Asc(nameof(RegCar.LicensePlate))));
        var descending = await executor.ExecuteQueryAsync(Unindexed(Desc(nameof(RegCar.RegisteredAt)), Desc(nameof(RegCar.LicensePlate))));

        Plates(ascending).Should().Equal("F", "G", "A", "B", "C", "D", "E");
        Plates(descending).Should().Equal("E", "D", "C", "B", "A", "G", "F");
    }

    [Fact]
    public async Task T21_an_enumerable_custom_query_sorts_by_instant_in_process()
    {
        var executor = Executor();

        var ascending = await executor.ExecuteQueryAsync(
            Custom("ListRegCars", Asc(nameof(RegCar.RegisteredAt)), Asc(nameof(RegCar.LicensePlate))));
        var nullable = await executor.ExecuteQueryAsync(
            Custom("ListRegCars", Asc(nameof(RegCar.DeregisteredAt)), Asc(nameof(RegCar.LicensePlate))));

        Plates(ascending).Should().Equal("F", "G", "A", "B", "C", "D", "E");
        // The in-process comparer puts rows without a value LAST ascending — the opposite of RavenDB.
        Plates(nullable).Should().Equal("D", "F", "A", "C", "B", "E", "G");
    }

    [Fact]
    public async Task T22_a_refused_first_sort_column_does_not_break_an_in_memory_queryable()
    {
        // Vin is on the query surface but canSort: false, so a caller-supplied sort by it is refused.
        // The next column must then become the primary order, not a ThenBy on an unordered sequence.
        var query = Custom("InMemoryRegCars")
            .WithSortColumns([Asc(nameof(RegCar.Vin)), Asc(nameof(RegCar.RegisteredAt)), Asc(nameof(RegCar.LicensePlate))]);

        var result = await Executor().ExecuteQueryAsync(query);

        Plates(result).Should().Equal("F", "G", "A", "B", "C", "D", "E");
    }

    [Fact]
    public async Task T22b_a_declared_sort_starting_with_a_collection_still_sorts_by_the_rest()
    {
        // A collection is refused even when the model declares it, so no caller is involved here.
        var query = Custom("InMemoryRegCars",
            Asc(nameof(RegCar.Tags)), Asc(nameof(RegCar.RegisteredAt)), Asc(nameof(RegCar.LicensePlate)));

        var result = await Executor().ExecuteQueryAsync(query);

        Plates(result).Should().Equal("F", "G", "A", "B", "C", "D", "E");
    }
}
