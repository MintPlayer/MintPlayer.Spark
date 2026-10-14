using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Tests.Queries;

/// <summary>
/// A multi-map default index returns only the queried type's rows (<c>docs/default_index_PRD.md</c>, D4;
/// SP1): <c>SparkMultiMapIndexCreationTask</c> injects the collection discriminator into each map of a marked
/// index, and <c>QueryExecutor</c> narrows the query on it before row security, paging, totals and the view
/// projection compose on top. Unnarrowed, a <c>Bike</c> comes back as a <c>Car</c> — and outside the row
/// policy of the type it really is.
/// </summary>
/// <remarks>
/// ⚠️ Every test host scans this whole assembly for indexes, and the catalog refuses two claims on one type.
/// So each marked index here claims its <b>own</b> pair of entity types — sharing <c>MmCar</c> between the
/// viewed and the viewless index would fail every Spark host in the assembly at startup.
/// </remarks>
public class MultiMapDefaultIndexTests : SparkTestDriver
{
    public abstract class MmVehicle
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public string Owner { get; set; } = "";
        public MmPart[] Parts { get; set; } = [];
        public bool Active { get; set; } = true;
    }

    public class MmPart
    {
        public string Code { get; set; } = "";
    }

    // One pair per marked index (see the remarks).
    public class MmCar : MmVehicle { }
    public class MmBike : MmVehicle { }
    public class MmVan : MmVehicle { }
    public class MmScooter : MmVehicle { }
    public class MmTram : MmVehicle { }
    public class MmBoat : MmVehicle { }
    public class MmTruck : MmVehicle { }
    public class MmKart : MmVehicle { }

    /// <summary>The multi-maps' reduce-result shape.</summary>
    public class MmVehicleEntry
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public string Owner { get; set; } = "";
        public object? Parts { get; set; }
    }

    [DefaultIndex(typeof(MmCar), typeof(MmBike))]
    public class MmVehicles : SparkMultiMapIndexCreationTask<MmVehicleEntry>
    {
        public MmVehicles()
        {
            AddMap<MmCar>(cars => from c in cars select new { c.Name, c.Owner });
            AddMap<MmBike>(bikes => from b in bikes
                                    let upper = b.Name.ToUpper()
                                    select new { Name = upper, b.Owner });
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(MmVehicles))]
    public class VMmVehicle
    {
        public string? Id { get; set; }
        public string Name { get; set; } = "";
        public string Owner { get; set; } = "";
    }

    /// <summary>No view: the executor returns entity documents.</summary>
    [DefaultIndex(typeof(MmVan), typeof(MmScooter))]
    public class MmFleetNoView : SparkMultiMapIndexCreationTask<MmVehicleEntry>
    {
        public MmFleetNoView()
        {
            AddMap<MmVan>(vans => from v in vans select new { v.Name, v.Owner });
            AddMap<MmScooter>(scooters => from s in scooters select new { s.Name, s.Owner });
        }
    }

    /// <summary>The SP1 shapes that matter to the rewrite: nested objects, a tricky literal, fan-out.</summary>
    [DefaultIndex(typeof(MmTram), typeof(MmBoat))]
    public class MmNestedAndFanOut : SparkMultiMapIndexCreationTask<MmVehicleEntry>
    {
        public MmNestedAndFanOut()
        {
            AddMap<MmTram>(trams => from t in trams
                                    where t.Active
                                    select new { t.Name, Parts = t.Parts.Select(p => new { p.Code, Label = "})\"(" + p.Code }) });
            AddMap<MmBoat>(boats => from b in boats
                                    from ch in b.Name
                                    select new { b.Name, Parts = ch });
            Index(x => x.Parts, FieldIndexing.No);
            Store(x => x.Parts, FieldStorage.Yes);
        }
    }

    [DefaultIndex(typeof(MmTruck), typeof(MmKart))]
    public class MmMethodSyntaxAndLet : SparkMultiMapIndexCreationTask<MmVehicleEntry>
    {
        public MmMethodSyntaxAndLet()
        {
            AddMap<MmTruck>(trucks => trucks.Select(t => new MmVehicleEntry { Name = t.Name, Owner = t.Owner }));
            AddMap<MmKart>(karts => from k in karts
                                    let o = k.Owner
                                    where o != null
                                    select new { k.Name, Owner = o });
        }
    }

    public class MmContext : SparkContext
    {
        public IRavenQueryable<MmCar> MmCars => Session.Query<MmCar>();
        public IRavenQueryable<MmVan> MmVans => Session.Query<MmVan>();
    }

    /// <summary>When enabled, the row policy of both queried types: only owner "a".</summary>
    public static class MmRowPolicy
    {
        public static bool Enabled;
    }

    public class MmCarActions(IEntityMapper entityMapper) : MintPlayer.Spark.Actions.DefaultPersistentObjectActions<MmCar>(entityMapper)
    {
        public override Task<System.Linq.Expressions.Expression<Func<MmCar, bool>>?> GetRowFilterAsync(string action)
            => Task.FromResult<System.Linq.Expressions.Expression<Func<MmCar, bool>>?>(MmRowPolicy.Enabled ? c => c.Owner == "a" : null);
    }

    public class MmVanActions(IEntityMapper entityMapper) : MintPlayer.Spark.Actions.DefaultPersistentObjectActions<MmVan>(entityMapper)
    {
        public override Task<System.Linq.Expressions.Expression<Func<MmVan, bool>>?> GetRowFilterAsync(string action)
            => Task.FromResult<System.Linq.Expressions.Expression<Func<MmVan, bool>>?>(MmRowPolicy.Enabled ? v => v.Owner == "a" : null);
    }

    private static EntityTypeFile Model(Type entityType, string indexName, int seed) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = Guid.Parse($"5f1c0000-0000-0000-0000-0000000{seed:D2}a00"),
            Name = entityType.Name,
            ClrType = entityType.FullName!,
            IndexName = indexName,
            Breadcrumb = "{Name}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.Parse($"5f1c0000-0000-0000-0000-0000000{seed:D2}a01"), Name = nameof(MmVehicle.Name), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.Parse($"5f1c0000-0000-0000-0000-0000000{seed:D2}a02"), Name = nameof(MmVehicle.Owner), DataType = "string" },
            ],
        },
    };

    private static SparkQuery Query(Type entityType, string indexName, int seed) => new()
    {
        Id = Guid.Parse($"5f1c0000-0000-0000-0000-0000000{seed:D2}a03"),
        Name = $"Get{entityType.Name}s",
        Source = $"Database.{entityType.Name}s",
        EntityType = entityType.Name,
        IndexName = indexName,
        SortColumns = [new SortColumn { Property = nameof(MmVehicle.Name), Direction = "asc" }],
    };

    private readonly List<IAsyncDisposable> _factories = [];

    public override async Task DisposeAsync()
    {
        MmRowPolicy.Enabled = false;
        foreach (var factory in _factories) await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>Three of <typeparamref name="TQueried"/> (two owned by "a") and two of <typeparamref name="TOther"/>.</summary>
    private async Task StorePairAsync<TQueried, TOther>()
        where TQueried : MmVehicle, new()
        where TOther : MmVehicle, new()
    {
        var queried = Store.Conventions.GetCollectionName(typeof(TQueried)).ToLowerInvariant();
        var other = Store.Conventions.GetCollectionName(typeof(TOther)).ToLowerInvariant();
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new TQueried { Name = "c1", Owner = "a", Parts = [new() { Code = "p" }] }, $"{queried}/1");
        await session.StoreAsync(new TQueried { Name = "c2", Owner = "a" }, $"{queried}/2");
        await session.StoreAsync(new TQueried { Name = "c3", Owner = "b" }, $"{queried}/3");
        await session.StoreAsync(new TOther { Name = "b1", Owner = "a" }, $"{other}/1");
        await session.StoreAsync(new TOther { Name = "b2", Owner = "b" }, $"{other}/2");
        await session.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_multi_map_default_returns_only_the_queried_types_rows(bool withView, bool rowPolicy)
    {
        MmRowPolicy.Enabled = rowPolicy;
        var (entityType, index, seed) = withView ? (typeof(MmCar), typeof(MmVehicles), 1) : (typeof(MmVan), typeof(MmFleetNoView), 2);

        // Both context roots get their model, so the host describes the whole context either way.
        var factory = new SparkEndpointFactory<MmContext>(
            Store, [Model(typeof(MmCar), nameof(MmVehicles), 1), Model(typeof(MmVan), nameof(MmFleetNoView), 2)],
            configureServices: services => services.AddScoped<MmCarActions>().AddScoped<MmVanActions>(),
            configureIndexCatalog: catalog =>
            {
                catalog.RegisterIndex(typeof(MmVehicles));
                catalog.RegisterProjection(typeof(VMmVehicle), typeof(MmVehicles));
                catalog.RegisterIndex(typeof(MmFleetNoView));
            });
        _factories.Add(factory);
        if (withView) await StorePairAsync<MmCar, MmBike>(); else await StorePairAsync<MmVan, MmScooter>();
        WaitForIndexing(Store);
        var executor = factory.GetService<IQueryExecutor>();

        var all = await executor.ExecuteQueryAsync(Query(entityType, index.Name, seed), skip: 0, take: 50);
        var secondPage = await executor.ExecuteQueryAsync(Query(entityType, index.Name, seed), skip: 1, take: 1);

        var prefix = withView ? "mmcars" : "mmvans";
        var expected = (rowPolicy ? new[] { 1, 2 } : [1, 2, 3]).Select(n => $"{prefix}/{n}").ToArray();
        all.Items.Select(i => i.Id!).Should().BeEquivalentTo(expected, because: "no other mapped collection may come back as the queried type");
        all.TotalItems.Should().Be(expected.Length, "the total counts only the narrowed rows");
        secondPage.Items.Select(i => i.Id!).Should().BeEquivalentTo([expected[1]], because: "paging runs over the narrowed, sorted rows");
        secondPage.TotalItems.Should().Be(expected.Length);
    }

    [Fact]
    public async Task Without_the_narrowing_a_multi_map_returns_every_collection_as_the_queried_type()
    {
        // The hazard the discriminator closes, pinned so the executor test above cannot pass vacuously.
        await new MmFleetNoView().ExecuteAsync(Store);
        await StorePairAsync<MmVan, MmScooter>();
        WaitForIndexing(Store);

        using var session = Store.OpenAsyncSession();
        var unfiltered = await session.Query<MmVan, MmFleetNoView>().ToListAsync();
        var narrowed = await ((IRavenQueryable<MmVan>)session.Query<SparkCollectionRow, MmFleetNoView>()
            .Where(r => r.SparkCollection == "MmVans")
            .OfType<MmVan>()).ToListAsync();

        unfiltered.Should().HaveCount(5);
        narrowed.Select(v => v.Id!).Should().BeEquivalentTo(["mmvans/1", "mmvans/2", "mmvans/3"]);
    }

    [Fact]
    public async Task The_server_compiles_the_rewritten_maps_of_every_shape()
    {
        await new MmNestedAndFanOut().ExecuteAsync(Store);
        await new MmMethodSyntaxAndLet().ExecuteAsync(Store);
        await StorePairAsync<MmTram, MmBoat>();
        await StorePairAsync<MmTruck, MmKart>();
        WaitForIndexing(Store);

        var errors = await Store.Maintenance.SendAsync(new GetIndexErrorsOperation([nameof(MmNestedAndFanOut), nameof(MmMethodSyntaxAndLet)]));
        errors.SelectMany(e => e.Errors).Select(e => e.Error).Should().BeEmpty();

        // Each shape's entries carry the right discriminator, the fan-out's included.
        using var session = Store.OpenAsyncSession();
        var boats = await ((IRavenQueryable<MmBoat>)session.Query<SparkCollectionRow, MmNestedAndFanOut>()
            .Where(r => r.SparkCollection == "MmBoats")
            .OfType<MmBoat>()).ToListAsync();
        boats.Select(b => b.Id!).Distinct().Should().BeEquivalentTo(["mmboats/1", "mmboats/2"]);

        var trucks = await ((IRavenQueryable<MmTruck>)session.Query<SparkCollectionRow, MmMethodSyntaxAndLet>()
            .Where(r => r.SparkCollection == "MmTrucks")
            .OfType<MmTruck>()).ToListAsync();
        trucks.Select(t => t.Id!).Should().BeEquivalentTo(["mmtrucks/1", "mmtrucks/2", "mmtrucks/3"]);
    }
}
