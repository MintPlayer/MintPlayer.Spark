using MintPlayer.Assertions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Whether a <see cref="DateTimeOffset"/> value the distinct panel offers can be filtered on.
/// </summary>
/// <remarks>
/// The suspicion (D14) was that these cannot round-trip: <c>ProjectedOffsetRestorer</c> re-attaches the
/// original offset to a row <em>after</em> materialization, so the panel lists <c>+02:00</c> values
/// while the index term is the UTC instant — and a filter compares against the index term.
/// <para>
/// ⚠️ It was carried as <b>unmeasured</b> for most of this campaign, which is the worst state for a
/// suspected defect: too plausible to ignore and too unproven to act on. This is the measurement.
/// </para>
/// </remarks>
public class DateTimeOffsetFilterTests : SparkTestDriver
{
    private static readonly Guid SlotTypeId = Guid.Parse("eeee6666-eeee-eeee-eeee-eeee66666666");

    public class Slot
    {
        public string? Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public DateTimeOffset StartsAt { get; set; }
    }

    public class Slots_Overview : AbstractIndexCreationTask<Slot>
    {
        public Slots_Overview()
        {
            Map = slots => from s in slots select new { s.Label, s.StartsAt };
        }
    }

    public class TestContext : SparkContext
    {
        public IRavenQueryable<Slot> Slots => Session.Query<Slot>();
    }

    private SparkEndpointFactory<TestContext>? _factory;

    /// <summary>Two instants, one of them written with a non-UTC offset.</summary>
    private static readonly DateTimeOffset Berlin = new(2026, 3, 9, 10, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset NewYork = new(2026, 3, 9, 4, 0, 0, TimeSpan.FromHours(-5));

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new Slots_Overview().ExecuteAsync(Store);

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new Slot { Label = "berlin", StartsAt = Berlin });
            await session.StoreAsync(new Slot { Label = "newyork", StartsAt = NewYork });
        });
        await Store.WaitForIndexingAsync();
    }

    public override async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static EntityTypeFile SlotModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = SlotTypeId,
            Name = "Slot",
            ClrType = typeof(Slot).FullName!,
            Breadcrumb = "{Label}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Slot.Label), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Slot.StartsAt), DataType = "datetime" },
            ],
        },
    };

    private IQueryExecutor Executor()
    {
        _factory = new SparkEndpointFactory<TestContext>(
            Store, [SlotModel()],
            configureIndexCatalog: catalog => catalog.RegisterIndex(typeof(Slots_Overview)));

        return _factory.GetService<IQueryExecutor>();
    }

    private static SparkQuery Query() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Slots",
        Source = "Database.Slots",
    };

    /// <summary>
    /// The value the panel offers is the value a filter selects — the round-trip D14 doubted.
    /// </summary>
    [Fact]
    public async Task A_value_the_distinct_panel_offers_selects_exactly_its_row()
    {
        var executor = Executor();

        var distincts = await executor.GetDistinctValuesAsync(Query(), nameof(Slot.StartsAt));
        distincts.Matching.Should().HaveCount(2, "two slots, two distinct instants");

        foreach (var offered in distincts.Matching)
        {
            var result = await executor.ExecuteQueryAsync(Query(),
                columnFilters: [new QueryColumnFilter { Name = nameof(Slot.StartsAt), Includes = [offered.Value] }]);

            result.TotalItems.Should().Be(1,
                $"the panel offered '{offered.Label}', so filtering by it must select exactly the row it came from");
        }
    }

    /// <summary>
    /// ⚠️ The offset is not part of the identity. Two ways of writing the same instant are the same
    /// index term, so a filter for one finds the other — which is correct for a timestamp, and worth
    /// pinning because it looks like a bug the first time someone sees it.
    /// </summary>
    [Fact]
    public async Task The_same_instant_written_with_a_different_offset_is_the_same_value()
    {
        var executor = Executor();

        var asUtc = Berlin.ToUniversalTime();

        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Slot.StartsAt), Includes = [asUtc] }]);

        result.TotalItems.Should().Be(1,
            "RavenDB reduces a DateTimeOffset to a canonical instant, so +02:00 and its UTC equivalent " +
            "are one term — the offset is display fidelity, not identity");
    }
}
