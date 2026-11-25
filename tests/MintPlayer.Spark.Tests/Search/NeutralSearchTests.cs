using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Search;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Search;
using MintPlayer.Spark.Search.OpenSearch;
using MintPlayer.Spark.Storage.InMemory;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Tests.Search;

public class NsrchTrack
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Artist { get; set; }
    public List<string> Tags { get; set; } = [];
    public bool IsDeleted { get; set; }
}

public class NsrchContext : SparkContext
{
    public IQueryable<NsrchTrack> NsrchTracks => Session.Query<NsrchTrack>();
}

/// <summary>
/// #536 S10 (D44): site search on the database <c>SPARK_TEST_DATABASE</c> picks, through the real endpoints. RavenDB
/// answers from its generated index; SQL Server, PostgreSQL and memory from the collection (<c>LIKE</c> on SQL), with the
/// same words: every word a whole word of the label or a search field, the last one completed while typing, soft-deleted
/// rows never.
/// </summary>
public class NeutralSearchTests : SparkTestDriver
{
    /// <summary>
    /// What the RavenDB generator emits for <see cref="NsrchTrack"/> (<c>SearchIndexGenerator</c>): the label's attribute
    /// and <c>search.fields</c>, a string collection joined. Nested, so a test host deploys it only where a test arms it
    /// (RavenDB here): a top-level index would replace <c>Spark/Search</c> in every RavenDB host of this assembly.
    /// </summary>
    public sealed class NsrchSearchIndex : SparkMultiMapIndexCreationTask<SparkSearchEntry>
    {
        public override string IndexName => SparkSearchIndex.Name;

        public NsrchSearchIndex()
        {
            AddMap<NsrchTrack>(docs => from d in docs
                let text = (d.Title ?? "") + " " + (d.Artist ?? "") + " " + (d.Tags == null ? "" : string.Join(" ", d.Tags))
                select new SparkSearchEntry
                {
                    Id = d.Id!,
                    Type = nameof(NsrchTrack),
                    Text = text,
                    Terms = new[] { text },
                    IsDeleted = d.IsDeleted == true,
                    LastModified = MetadataFor(d).Value<DateTime>("@last-modified"),
                });

            Index(SparkSearchIndex.Text, FieldIndexing.Search);
            Index(SparkSearchIndex.Terms, FieldIndexing.Search);
            Store(SparkSearchIndex.Type, FieldStorage.Yes);
            Store(SparkSearchIndex.Text, FieldStorage.Yes);
            Store(SparkSearchIndex.IsDeleted, FieldStorage.Yes);
            Store(SparkSearchIndex.LastModified, FieldStorage.Yes);
            Configuration["Indexing.Static.SearchEngineType"] = "Lucene";
        }
    }

    private static readonly Guid TrackTypeId = Guid.Parse("5ea7c400-0000-4000-8000-000000000536");

    private static EntityTypeFile TrackModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = TrackTypeId,
            Name = nameof(NsrchTrack),
            ClrType = typeof(NsrchTrack).FullName!,
            Breadcrumb = "{Title}",
            Search = new EntityTypeSearch { Enabled = true, Label = "{Title}", Fields = [nameof(NsrchTrack.Artist), nameof(NsrchTrack.Tags)], Fuzzy = false },
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = nameof(NsrchTrack.Title), DataType = "string" },
                new() { Id = Guid.NewGuid(), Name = nameof(NsrchTrack.Artist), DataType = "string" },
            ],
        },
        Queries = [new SparkQuery { Id = Guid.NewGuid(), Name = "NsrchTracks", Source = "Database.NsrchTracks", EntityType = nameof(NsrchTrack) }],
    };

    private async Task<SparkEndpointFactory<NsrchContext>> StartAsync()
    {
        var host = new SparkEndpointFactory<NsrchContext>(Database, [TrackModel()],
            configureSpark: spark => spark.AddSearch(),
            configureServices: services => services.AddSparkStoredType<NsrchTrack>(),
            configureIndexCatalog: catalog =>
            {
                if (Database.ProviderName == SparkStoreProviders.RavenDb)
                    catalog.RegisterIndex(typeof(NsrchSearchIndex));
            },
            security: SparkTestSecurity.Permissive);
        await host.SeedAsync(async session =>
        {
            await session.StoreAsync(new NsrchTrack { Id = "nsrchtracks/1", Title = "Bohemian Rhapsody", Artist = "Queen", Tags = ["rock", "opera"] });
            await session.StoreAsync(new NsrchTrack { Id = "nsrchtracks/2", Title = "Radio Ga Ga", Artist = "Queen" });
            await session.StoreAsync(new NsrchTrack { Id = "nsrchtracks/3", Title = "Rhapsody in Blue", Artist = "Gershwin", Tags = ["jazz"] });
            await session.StoreAsync(new NsrchTrack { Id = "nsrchtracks/4", Title = "Bohemian Like You", Artist = "The Dandy Warhols", IsDeleted = true });
        });
        await host.WaitForConsistencyAsync();
        return host;
    }

    private static async Task<string[]> IdsAsync(HttpClient client, string url)
        => (await client.GetFromJsonAsync<SparkSearchHit[]>(url))!.Select(h => h.Id.ToLowerInvariant()).ToArray();

    [Fact]
    public async Task Every_word_must_be_a_whole_word_of_the_label_or_a_search_field()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();

        // The artist is a search field.
        (await IdsAsync(client, "/spark/search?q=queen")).Should().BeEquivalentTo(["nsrchtracks/1", "nsrchtracks/2"]);
        (await IdsAsync(client, "/spark/search?q=rhapsody%20queen")).Should().Equal(["nsrchtracks/1"], "the words may come from different fields");
        (await IdsAsync(client, "/spark/search?q=jazz")).Should().Equal(["nsrchtracks/3"], "a string collection is searched too");
        (await IdsAsync(client, "/spark/search?q=bohemian")).Should().Equal(["nsrchtracks/1"], "a soft-deleted track is never found");
        // Case is ignored.
        (await IdsAsync(client, "/spark/search?q=RHAPSODY")).Should().BeEquivalentTo(["nsrchtracks/1", "nsrchtracks/3"]);
        (await IdsAsync(client, "/spark/search?q=rhapsody%20mozart")).Should().BeEmpty("every word must match");
    }

    [Fact]
    public async Task Suggestions_complete_the_last_word_and_a_search_does_not()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();

        (await IdsAsync(client, "/spark/search/suggest?q=bohem")).Should().Equal(["nsrchtracks/1"]);
        (await IdsAsync(client, "/spark/search?q=bohem")).Should().BeEmpty("a complete search matches whole words");
        (await IdsAsync(client, "/spark/search?q=hapsody")).Should().BeEmpty("a substring inside a word is not a word");
    }

    [Fact]
    public async Task A_label_resolves_to_its_one_object()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();

        var response = await client.GetAsync("/spark/search/resolve?q=Radio%20Ga%20Ga");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SparkSearchHit>())!.Id.ToLowerInvariant().Should().Be("nsrchtracks/2");
    }
}

/// <summary>
/// #536 D44: <c>AddSearch</c> asks for no store provider; the OpenSearch engine does, because it syncs from RavenDB's
/// <c>Spark_Search</c> index. Another engine (<c>UseProvider</c>) is not bound to RavenDB.
/// </summary>
public class SearchStoreRequirementTests
{
    private static readonly ISparkStore InMemory = new InMemorySparkStore(new InMemoryDatabase());

    private sealed class TestBuilder : ISparkBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration? Configuration => null;
        public SparkModuleRegistry Registry { get; } = new();
    }

    private sealed class OtherEngine : ISparkSearchProvider
    {
        public Task<IReadOnlyList<SparkSearchCandidate>> SearchAsync(SparkSearchRequest request, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SparkSearchCandidate>>([]);
        public Task<IReadOnlyList<SparkSearchCandidate>> FindByLabelAsync(string label, IReadOnlyCollection<string> entityTypes, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SparkSearchCandidate>>([]);
    }

    private static IReadOnlyList<string> Unmet(Action<ISparkBuilder> configure)
    {
        var builder = new TestBuilder();
        configure(builder);
        return SparkStoreRequirementExtensions.Unmet(
            builder.Services.Select(d => d.ImplementationInstance).OfType<SparkStoreRequirement>(), InMemory);
    }

    [Fact]
    public void Search_and_another_engine_run_on_any_store_and_OpenSearch_on_RavenDB_only()
    {
        Unmet(spark => spark.AddSearch()).Should().BeEmpty();
        Unmet(spark => spark.AddSearch(s => s.UseProvider<OtherEngine>())).Should().BeEmpty();
        Unmet(spark => spark.AddSearch(s => s.UseOpenSearch(o => o.Uri = new Uri("http://localhost:9200"))))
            .Should().Equal("Spark.Search.OpenSearch runs on RavenDB only; this application uses the InMemory provider.");
    }
}
