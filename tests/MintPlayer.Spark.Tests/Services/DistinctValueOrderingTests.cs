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
/// The filter panel's value list is ordered by what it <em>is</em>, not by how it prints.
/// </summary>
public class DistinctValueOrderingTests : SparkTestDriver
{
    private static readonly Guid TallyTypeId = Guid.Parse("dddd5555-dddd-dddd-dddd-dddd55555555");

    public class Tally
    {
        public string? Id { get; set; }
        public string Label { get; set; } = string.Empty;

        /// <summary>Chosen so text order and numeric order disagree: "120" sorts before "20".</summary>
        public int Headcount { get; set; }
    }

    public class Tallies_Overview : AbstractIndexCreationTask<Tally>
    {
        public Tallies_Overview()
        {
            Map = tallies => from t in tallies select new { t.Label, t.Headcount };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    public class TestContext : SparkContext
    {
        public IRavenQueryable<Tally> Tallies => Session.Query<Tally>();
    }

    private SparkEndpointFactory<TestContext>? _factory;

    private static EntityTypeFile TallyModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = TallyTypeId,
            Name = "Tally",
            ClrType = typeof(Tally).FullName!,
            Breadcrumb = "{Label}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Tally.Label), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Tally.Headcount), DataType = "int" },
            ],
        },
    };

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new Tallies_Overview().ExecuteAsync(Store);

        await SeedAsync(async session =>
        {
            foreach (var (label, headcount) in new[] { ("a", 1), ("b", 120), ("c", 20), ("d", 40) })
                await session.StoreAsync(new Tally { Label = label, Headcount = headcount });
        });
        await Store.WaitForIndexingAsync();
    }

    public override async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static SparkQuery Query() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Tallies",
        Source = "Database.Tallies",
    };

    /// <summary>
    /// ⚠️ Measured before the fix on DemoApp: an <c>EmployeeCount</c> column listed
    /// <c>1, 120, 20, 40</c>. The list was sorted by <c>Label</c>, which is the rendered string.
    /// </summary>
    [Fact]
    public async Task Numeric_values_are_listed_in_numeric_order_not_text_order()
    {
        _factory = new SparkEndpointFactory<TestContext>(
            Store, [TallyModel()],
            configureIndexCatalog: catalog => catalog.RegisterIndex(typeof(Tallies_Overview)));
        var executor = _factory.GetService<IQueryExecutor>();

        var result = await executor.GetDistinctValuesAsync(Query(), nameof(Tally.Headcount));

        string.Join(",", result.Matching.Select(v => v.Label))
            .Should().Be("1,20,40,120", "the values are numbers, so they order as numbers");
    }
}
