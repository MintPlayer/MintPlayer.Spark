using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using static MintPlayer.Spark.Tests.Services.RegCarQuerySortFilterTests;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// What a <b>hand-written</b> index does at runtime when its companions are, or are not, mapped
/// (docs/datetimeoffset_query_sort_filter_PRD.md §9, T37 and T38).
/// </summary>
/// <remarks>
/// <para>
/// T37 pins the silent failure SPARK006 now guards at compile time: a <c>{Name}Raw</c> or
/// <c>{Name}Search</c> companion declared on the projection, even named in an <c>Index(...)</c> call,
/// but never assigned in the map. The index deploys healthy and nothing is logged; the grid shows every
/// offset as <c>+00:00</c>, and search finds nothing. If this class ever goes red because the runtime
/// started recovering such values, the diagnostic's rationale has changed.
/// </para>
/// <para>
/// T38 pins why SPARK005 no longer asks a <c>DateTimeOffset</c> for a companion: declared
/// <c>FieldIndexing.Exact</c> by hand, it still sorts and filters by instant.
/// </para>
/// <para>
/// Its own entity, not <c>RegCar</c>: assembly-wide scans match indexes and actions by simple name,
/// and three more indexes over <c>RegCar</c> would change what its own class boots. Several indexes over
/// one collection need a <c>[DefaultIndex]</c> (measured in SP5), carried by the mapped control.
/// </para>
/// </remarks>
public class HandWrittenIndexCompanionTests(HandWrittenIndexCompanionTests.SeededHwCars host)
    : SparkSharedTestDriver(host), IClassFixture<HandWrittenIndexCompanionTests.SeededHwCars>, IDisposable
{
    private static readonly Guid HwCarTypeId = Guid.Parse("ffff6666-ffff-ffff-ffff-ffff66666666");

    public class HwCar
    {
        public string? Id { get; set; }
        public string LicensePlate { get; set; } = string.Empty;
        public string Brand { get; set; } = string.Empty;
        public DateTimeOffset RegisteredAt { get; set; }
    }

    /// <summary>Control: both companions mapped, as the generator would write them.</summary>
    [DefaultIndex]
    public class HwCars_Mapped : AbstractIndexCreationTask<HwCar>
    {
        public HwCars_Mapped()
        {
            Map = cars => from car in cars
                          select new
                          {
                              car.LicensePlate,
                              car.Brand,
                              BrandSearch = car.Brand,
                              car.RegisteredAt,
                              RegisteredAtRaw = new SparkIndexValue<DateTimeOffset> { V = car.RegisteredAt },
                          };
            Index(nameof(VHwCarMapped.BrandSearch), FieldIndexing.Search);
            Index(nameof(VHwCarMapped.RegisteredAtRaw), FieldIndexing.No);
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(HwCars_Mapped))]
    public class VHwCarMapped
    {
        public string? Id { get; set; }
        public string LicensePlate { get; set; } = string.Empty;
        public string Brand { get; set; } = string.Empty;
        public DateTimeOffset RegisteredAt { get; set; }
        [IgnoreProperty] public string? BrandSearch { get; set; }
        [IgnoreProperty] public SparkIndexValue<DateTimeOffset>? RegisteredAtRaw { get; set; }
    }

    /// <summary>The defect: both companions declared and named in Index(...), neither mapped.</summary>
    public class HwCars_Unmapped : AbstractIndexCreationTask<HwCar>
    {
        public HwCars_Unmapped()
        {
            Map = cars => from car in cars select new { car.LicensePlate, car.Brand, car.RegisteredAt };
            Index(nameof(VHwCarUnmapped.BrandSearch), FieldIndexing.Search);
            Index(nameof(VHwCarUnmapped.RegisteredAtRaw), FieldIndexing.No);
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(HwCars_Unmapped))]
    public class VHwCarUnmapped
    {
        public string? Id { get; set; }
        public string LicensePlate { get; set; } = string.Empty;
        public string Brand { get; set; } = string.Empty;
        public DateTimeOffset RegisteredAt { get; set; }
        [IgnoreProperty] public string? BrandSearch { get; set; }
        [IgnoreProperty] public SparkIndexValue<DateTimeOffset>? RegisteredAtRaw { get; set; }
    }

    /// <summary>T38: a DateTimeOffset hand-declared Exact, offsets preserved through a mapped Raw.</summary>
    public class HwCars_Exact : AbstractIndexCreationTask<HwCar>
    {
        public HwCars_Exact()
        {
            Map = cars => from car in cars
                          select new
                          {
                              car.LicensePlate,
                              car.RegisteredAt,
                              RegisteredAtRaw = new SparkIndexValue<DateTimeOffset> { V = car.RegisteredAt },
                          };
            Index(nameof(VHwCarExact.RegisteredAt), FieldIndexing.Exact);
            Index(nameof(VHwCarExact.RegisteredAtRaw), FieldIndexing.No);
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(HwCars_Exact))]
    public class VHwCarExact
    {
        public string? Id { get; set; }
        public string LicensePlate { get; set; } = string.Empty;
        public DateTimeOffset RegisteredAt { get; set; }
        [IgnoreProperty] public SparkIndexValue<DateTimeOffset>? RegisteredAtRaw { get; set; }
    }

    public class HwContext : SparkContext
    {
        public IRavenQueryable<HwCar> HwCars => Session.Query<HwCar>();
    }

    /// <summary>The §6 fixture's plates, brands and offsets, as HwCars.</summary>
    private static readonly HwCar[] Rows = [.. FixtureRows.Select(r => new HwCar
    {
        Id = $"hwcars/{r.LicensePlate.ToLowerInvariant()}",
        LicensePlate = r.LicensePlate,
        Brand = r.Brand,
        RegisteredAt = r.RegisteredAt,
    })];

    private static EntityTypeFile HwCarModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = HwCarTypeId,
            Name = "HwCar",
            ClrType = typeof(HwCar).FullName!,
            Breadcrumb = "{LicensePlate}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(HwCar.LicensePlate), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(HwCar.Brand), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(HwCar.RegisteredAt), DataType = "datetime" },
            ],
        },
    };

    public sealed class SeededHwCars : SharedSparkHost<HwContext>
    {
        protected override async Task BeforeHostAsync()
        {
            await new HwCars_Mapped().ExecuteAsync(Store);
            await new HwCars_Unmapped().ExecuteAsync(Store);
            await new HwCars_Exact().ExecuteAsync(Store);

            using (var session = Store.OpenAsyncSession())
            {
                foreach (var row in Rows) await session.StoreAsync(row);
                await session.SaveChangesAsync();
            }
            await RavenIndexHelper.WaitForNonStaleAsync(Store);
        }

        protected override SparkEndpointFactory<HwContext> CreateFactory() =>
            new(Store, [HwCarModel()],
                configureIndexCatalog: catalog =>
                {
                    catalog.RegisterIndex(typeof(HwCars_Mapped));
                    catalog.RegisterProjection(typeof(VHwCarMapped), typeof(HwCars_Mapped));
                    catalog.RegisterIndex(typeof(HwCars_Unmapped));
                    catalog.RegisterProjection(typeof(VHwCarUnmapped), typeof(HwCars_Unmapped));
                    catalog.RegisterIndex(typeof(HwCars_Exact));
                    catalog.RegisterProjection(typeof(VHwCarExact), typeof(HwCars_Exact));
                });
    }

    private IServiceScope? _scope;

    public void Dispose() => _scope?.Dispose();

    private Task<QueryResult> QueryAsync(string index, string? search = null, IReadOnlyList<QueryColumnFilter>? filters = null)
    {
        _scope?.Dispose();
        _scope = host.Factory.CreateScope();
        var executor = _scope.ServiceProvider.GetRequiredService<IQueryExecutor>();

        return executor.ExecuteQueryAsync(new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "Q_" + index,
            Source = "Database.HwCars",
            IndexName = index,
            EntityType = "HwCar",
            SortColumns = [Asc(nameof(HwCar.RegisteredAt)), Asc(nameof(HwCar.LicensePlate))],
        }, search: search, columnFilters: filters);
    }

    private static DateTimeOffset RegisteredAtOf(QueryResultItem item) => (DateTimeOffset)Value(item, nameof(HwCar.RegisteredAt))!;
    private static DateTimeOffset FixtureOf(QueryResultItem item)
        => Rows.Single(r => r.LicensePlate == (string)Value(item, nameof(HwCar.LicensePlate))!).RegisteredAt;

    // --- T37 ----------------------------------------------------------------------------------------

    [Fact]
    public async Task T37_an_unmapped_Raw_companion_returns_every_offset_as_utc_silently()
    {
        var unmapped = await QueryAsync(nameof(HwCars_Unmapped));
        var mapped = await QueryAsync(nameof(HwCars_Mapped));

        unmapped.Items.Should().HaveCount(Rows.Length);
        foreach (var item in unmapped.Items)
        {
            RegisteredAtOf(item).Offset.Should().Be(TimeSpan.Zero);
            RegisteredAtOf(item).UtcTicks.Should().Be(FixtureOf(item).UtcTicks, "the instant survives; only the offset is lost");
        }

        // The fixture can tell the two apart: five of its seven rows carry a non-zero offset.
        unmapped.Items.Count(i => !RegisteredAtOf(i).EqualsExact(FixtureOf(i))).Should().Be(5);

        // And nothing else gives it away: the order is still by instant.
        Plates(unmapped).Should().Equal("F", "G", "A", "B", "C", "D", "E");

        // The control, same data, companion mapped: every offset exact.
        mapped.Items.All(i => RegisteredAtOf(i).EqualsExact(FixtureOf(i))).Should().BeTrue();
    }

    [Fact]
    public async Task T37_an_unmapped_Search_companion_makes_search_find_nothing()
    {
        var unmapped = await QueryAsync(nameof(HwCars_Unmapped), search: "Volvo");
        var mapped = await QueryAsync(nameof(HwCars_Mapped), search: "Volvo");

        mapped.TotalItems.Should().Be(3, "A, C and E are Volvos");
        unmapped.TotalItems.Should().Be(0, "the analyzed copy search reads is empty for every document");
    }

    // --- T38 ----------------------------------------------------------------------------------------

    [Fact]
    public async Task T38_a_DateTimeOffset_declared_Exact_by_hand_still_sorts_and_filters_by_instant()
    {
        var sorted = await QueryAsync(nameof(HwCars_Exact));
        var filtered = await QueryAsync(nameof(HwCars_Exact), filters:
            [new QueryColumnFilter { Name = nameof(HwCar.RegisteredAt), Includes = [Wire("\"2027-03-01T10:00:00-05:00\"")] }]);

        Plates(sorted).Should().Equal("F", "G", "A", "B", "C", "D", "E");
        sorted.Items.All(i => RegisteredAtOf(i).EqualsExact(FixtureOf(i))).Should().BeTrue();
        Plates(filtered).Should().Equal("C", "D");
    }
}
