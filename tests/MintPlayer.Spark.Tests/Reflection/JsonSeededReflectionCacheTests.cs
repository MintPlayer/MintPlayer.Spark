using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Reflection;

/// <summary>
/// Integration test that seeds RavenDB from a JSON fixture and drives the full list
/// pipeline through the reflective hot paths the cache covers:
/// <list type="bullet">
///   <item><see cref="ReflectedTypeExtensions.GetCachedProperty"/> for Id / FirstName / LastName,</item>
///   <item><see cref="AccessorCache.GetGetter"/> for those properties,</item>
///   <item><see cref="EntityMapper.PopulateAttributeValues"/> per row,</item>
///   <item><c>GetEntityDisplayName</c> via <c>DisplayAttribute</c>.</item>
/// </list>
/// Running the same query twice on the same fixture proves the cached delegates produce
/// identical, deterministic output across calls — a regression in the cache (e.g. wrong key,
/// stale entry, swallowed exception) would surface as a divergent second result.
/// </summary>
public class JsonSeededReflectionCacheTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private SparkEndpointFactory<TestSparkContext> _factory = null!;
    private IQueryExecutor _executor = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var personModel = new EntityTypeFile
        {
            PersistentObject = new EntityTypeDefinition
            {
                Id = PersonTypeId,
                Name = "Person",
                ClrType = typeof(Person).FullName!,
                DisplayAttribute = "LastName",
                Attributes =
                [
                    new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
                    new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string" },
                ],
            },
        };

        _factory = new SparkEndpointFactory<TestSparkContext>(Store, [personModel]);
        _executor = _factory.GetService<IQueryExecutor>();

        // Seed from the JSON fixture copied to the test output directory by the .csproj
        // <Content Include="Reflection\Fixtures\**\*.json" /> rule.
        await SeedFromJsonAsync("Reflection/Fixtures/people.json");
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Listing_People_resolves_attributes_and_display_name_through_cached_reflection()
    {
        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "People",
            Source = "Database.People",
            SortColumns = [new SortColumn { Property = "LastName", Direction = "asc" }],
        };

        var result = await _executor.ExecuteQueryAsync(query);

        result.TotalRecords.Should().Be(3);

        var rows = result.Data
            .Select(po => (
                Id: po.Id,
                Name: po.Name, // GetEntityDisplayName(DisplayAttribute = "LastName") → AccessorCache.GetGetter
                FirstName: po.Attributes.Single(a => a.Name == "FirstName").Value?.ToString(),
                LastName: po.Attributes.Single(a => a.Name == "LastName").Value?.ToString()))
            .ToList();

        rows.Should().Equal(
            ("people/2-A", "Hopper", "Grace", "Hopper"),
            ("people/1-A", "Lovelace", "Ada", "Lovelace"),
            ("people/3-A", "Torvalds", "Linus", "Torvalds"));
    }

    [Fact]
    public async Task Listing_People_twice_returns_identical_results_on_cache_hit()
    {
        // First call populates the per-Type / per-PropertyInfo cache entries; second call
        // reads through them. If a cache key collided or a Lazy<T> captured wrong state,
        // the two results would diverge.
        var query = new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "People",
            Source = "Database.People",
            SortColumns = [new SortColumn { Property = "LastName", Direction = "asc" }],
        };

        var first = await _executor.ExecuteQueryAsync(query);
        var second = await _executor.ExecuteQueryAsync(query);

        var firstShape = first.Data
            .Select(po => (po.Id, po.Name, po.Attributes.Count))
            .ToList();
        var secondShape = second.Data
            .Select(po => (po.Id, po.Name, po.Attributes.Count))
            .ToList();

        secondShape.Should().Equal(firstShape,
            "cached reflective dispatch must produce identical PO shapes across repeated calls");
    }
}
