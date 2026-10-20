using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Commerce X18, "fuzzy by default" (owner, 2026-10-19): Spark's search is typo-tolerant on an index's searchable
/// fields, on an index running Lucene, with <c>[Fuzzy(false)]</c> as the per-field opt-out.
/// </summary>
/// <remarks>
/// Three indexes over one collection: <see cref="FzCars_Lucene"/> deployed the way Spark deploys it
/// (<see cref="SparkSearchEngine.Apply"/> puts it on Lucene), <see cref="FzCars_OptOut"/> the same with the field
/// opted out, and <see cref="FzCars_Corax"/> deployed on Corax as an existing index is until its side-by-side
/// rebuild swaps in. The last one is the deploy-safety case: a fuzzy clause on Corax fails the whole query.
/// </remarks>
public class FuzzySearchTests(FuzzySearchTests.SeededFzCars host)
    : SparkSharedTestDriver(host), IClassFixture<FuzzySearchTests.SeededFzCars>, IDisposable
{
    private static readonly Guid FzCarTypeId = Guid.Parse("ffff7777-ffff-ffff-ffff-ffff77777777");

    public class FzCar
    {
        public string? Id { get; set; }
        public string Model { get; set; } = string.Empty;
    }

    [DefaultIndex]
    public class FzCars_Lucene : AbstractIndexCreationTask<FzCar>
    {
        public FzCars_Lucene()
        {
            Map = cars => from car in cars select new { car.Model, ModelSearch = car.Model };
            Index(nameof(VFzCar.ModelSearch), FieldIndexing.Search);
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(FzCars_Lucene))]
    public class VFzCar
    {
        public string? Id { get; set; }
        public string Model { get; set; } = string.Empty;
        [IgnoreProperty] public string? ModelSearch { get; set; }
    }

    public class FzCars_OptOut : AbstractIndexCreationTask<FzCar>
    {
        public FzCars_OptOut()
        {
            Map = cars => from car in cars select new { car.Model, ModelSearch = car.Model };
            Index(nameof(VFzCarOptOut.ModelSearch), FieldIndexing.Search);
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(FzCars_OptOut))]
    public class VFzCarOptOut
    {
        public string? Id { get; set; }
        /// <summary>The opt-out sits on the base property; it covers the <c>ModelSearch</c> companion.</summary>
        [Fuzzy(false)] public string Model { get; set; } = string.Empty;
        [IgnoreProperty] public string? ModelSearch { get; set; }
    }

    /// <summary>Searchable, but still on Corax (deployed without Spark's engine choice).</summary>
    public class FzCars_Corax : AbstractIndexCreationTask<FzCar>
    {
        public FzCars_Corax()
        {
            Map = cars => from car in cars select new { car.Model, ModelSearch = car.Model };
            Index(nameof(VFzCarCorax.ModelSearch), FieldIndexing.Search);
            StoreAllFields(FieldStorage.Yes);
        }
    }

    [FromIndex(typeof(FzCars_Corax))]
    public class VFzCarCorax
    {
        public string? Id { get; set; }
        public string Model { get; set; } = string.Empty;
        [IgnoreProperty] public string? ModelSearch { get; set; }
    }

    public class FzContext : SparkContext
    {
        public IRavenQueryable<FzCar> FzCars => Session.Query<FzCar>();
    }

    private static readonly FzCar[] Rows =
    [
        new() { Id = "fzcars/1", Model = "Volkswagen Golf" },
        new() { Id = "fzcars/2", Model = "Volvo Amazon" },
        new() { Id = "fzcars/3", Model = "Renault Clio" },
    ];

    private static EntityTypeFile FzCarModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = FzCarTypeId,
            Name = "FzCar",
            ClrType = typeof(FzCar).FullName!,
            Breadcrumb = "{Model}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(FzCar.Model), DataType = "string" },
            ],
        },
    };

    public sealed class SeededFzCars : SharedSparkHost<FzContext>
    {
        protected override async Task BeforeHostAsync()
        {
            var tasks = new List<IAbstractIndexCreationTask> { new FzCars_Lucene(), new FzCars_OptOut() };
            SparkSearchEngine.Apply(tasks, SparkSearchEngine.FuzzyIndexTypes([typeof(VFzCar), typeof(VFzCarOptOut)]));
            await IndexCreation.CreateIndexesAsync(tasks, Store);

            // Deployed as-is: no engine set, so it takes the server default (Corax).
            await new FzCars_Corax().ExecuteAsync(Store);

            using (var session = Store.OpenAsyncSession())
            {
                foreach (var row in Rows) await session.StoreAsync(row);
                await session.SaveChangesAsync();
            }
            await RavenIndexHelper.WaitForNonStaleAsync(Store);
        }

        protected override SparkEndpointFactory<FzContext> CreateFactory() =>
            new(Store, [FzCarModel()],
                configureIndexCatalog: catalog =>
                {
                    catalog.RegisterIndex(typeof(FzCars_Lucene));
                    catalog.RegisterProjection(typeof(VFzCar), typeof(FzCars_Lucene));
                    catalog.RegisterIndex(typeof(FzCars_OptOut));
                    catalog.RegisterProjection(typeof(VFzCarOptOut), typeof(FzCars_OptOut));
                    catalog.RegisterIndex(typeof(FzCars_Corax));
                    catalog.RegisterProjection(typeof(VFzCarCorax), typeof(FzCars_Corax));
                });
    }

    private IServiceScope? _scope;

    public void Dispose() => _scope?.Dispose();

    private async Task<string?[]> SearchAsync(string index, string search)
    {
        _scope?.Dispose();
        _scope = host.Factory.CreateScope();
        var executor = _scope.ServiceProvider.GetRequiredService<IQueryExecutor>();

        var result = await executor.ExecuteQueryAsync(new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "Q_" + index,
            Source = "Database.FzCars",
            IndexName = index,
            EntityType = "FzCar",
        }, search: search);

        return [.. result.Items.Select(i => i.Breadcrumb).Order()];
    }

    // --- Through the query executor -----------------------------------------------------------------

    [Fact]
    public async Task A_typo_finds_the_row_on_a_searchable_Lucene_index()
    {
        (await SearchAsync(nameof(FzCars_Lucene), "volkswagon")).Should().Equal("Volkswagen Golf");
    }

    [Fact]
    public async Task Substring_search_still_matches_beside_the_fuzzy_arm()
    {
        (await SearchAsync(nameof(FzCars_Lucene), "olkswag")).Should().Equal("Volkswagen Golf");
        (await SearchAsync(nameof(FzCars_Lucene), "vol")).Should().Equal("Volkswagen Golf", "Volvo Amazon");
    }

    [Fact]
    public async Task Every_word_must_match_by_substring_or_typo()
    {
        (await SearchAsync(nameof(FzCars_Lucene), "volkswagon golf")).Should().Equal("Volkswagen Golf");
        (await SearchAsync(nameof(FzCars_Lucene), "volkswagon clio")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_short_word_is_not_matched_fuzzily()
    {
        // "gilf" is one edit from "golf" but four letters is the minimum, so it IS fuzzy; "glf" (three) is not.
        (await SearchAsync(nameof(FzCars_Lucene), "gilf")).Should().Equal("Volkswagen Golf");
        (await SearchAsync(nameof(FzCars_Lucene), "glf")).Should().BeEmpty();
    }

    [Fact]
    public async Task An_opted_out_field_matches_by_substring_only()
    {
        (await SearchAsync(nameof(FzCars_OptOut), "volkswagon")).Should().BeEmpty();
        (await SearchAsync(nameof(FzCars_OptOut), "olkswag")).Should().Equal("Volkswagen Golf");
    }

    [Fact]
    public async Task An_index_still_on_Corax_gets_substring_search_and_no_error()
    {
        (await SearchAsync(nameof(FzCars_Corax), "volkswagon")).Should().BeEmpty();
        (await SearchAsync(nameof(FzCars_Corax), "olkswag")).Should().Equal("Volkswagen Golf");
    }

    // --- The pieces ---------------------------------------------------------------------------------

    [Fact]
    public void The_fuzzy_fields_are_the_searchable_ones_minus_the_opt_outs()
    {
        SparkSearchEngine.FuzzySearchFields(typeof(FzCars_Lucene), typeof(VFzCar)).Should().BeEquivalentTo(["ModelSearch"]);
        SparkSearchEngine.FuzzySearchFields(typeof(FzCars_OptOut), typeof(VFzCarOptOut)).Should().BeEmpty();
    }

    [Fact]
    public async Task Only_an_index_running_Lucene_is_confirmed()
    {
        (await SparkSearchEngine.RunsLuceneAsync(Store, null, "FzCars/Lucene")).Should().BeTrue();
        (await SparkSearchEngine.RunsLuceneAsync(Store, null, "FzCars/Corax")).Should().BeFalse();
    }

    [Theory]
    [InlineData(3, "none")]
    [InlineData(4, "0.65")]
    [InlineData(5, "0.74")]
    [InlineData(7, "0.82")]
    [InlineData(8, "0.65")]
    [InlineData(10, "0.74")]
    public void The_similarity_allows_one_edit_up_to_seven_letters_and_two_from_eight(int length, string expected)
    {
        var similarity = SparkFuzzySearch.Similarity(length) is { } s
            ? s.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "none";

        similarity.Should().Be(expected);
    }

    /// <summary>
    /// Against RavenDB's own rendering of a LINQ <c>Search</c>: the rewrite must recognise it, and must carry the
    /// user's words as parameters only.
    /// </summary>
    [Fact]
    public void The_rewrite_adds_a_parameterised_fuzzy_arm_to_the_searched_field()
    {
        const string userInput = "volkswagon' or true or '";
        var term = QueryExecutor.BuildSearchTerm(userInput)!;

        using var session = Store.OpenSession();
        IQueryable<VFzCar> query = session.Query<VFzCar, FzCars_Lucene>()
            .Search(x => x.ModelSearch, term, 1m, SearchOptions.Guess, SearchOperator.And);
        var indexQuery = ((IRavenQueryInspector)query).GetIndexQuery(false);

        SparkFuzzySearch.Rewrite(indexQuery, term, new HashSet<string> { "ModelSearch" });

        // "volkswagon'" is eleven characters: two edits, 1 - 2/9 - 0.01 rounded down.
        indexQuery.Query.Should().Contain("fuzzy(ModelSearch = $sparkFuzzy0, 0.76)");
        indexQuery.Query.Should().Contain("boost(search(ModelSearch, $sparkWildcard0), 2)");
        indexQuery.Query.Should().NotContain("volkswagon").And.NotContain("true");
        indexQuery.QueryParameters["sparkFuzzy0"].Should().Be("volkswagon'");
        indexQuery.QueryParameters["sparkWildcard0"].Should().Be("*volkswagon'*");
    }

    [Fact]
    public void The_rewrite_leaves_other_fields_and_other_terms_alone()
    {
        var term = QueryExecutor.BuildSearchTerm("volkswagon")!;

        using var session = Store.OpenSession();
        IQueryable<VFzCar> query = session.Query<VFzCar, FzCars_Lucene>()
            .Search(x => x.ModelSearch, term, 1m, SearchOptions.Guess, SearchOperator.And);
        var indexQuery = ((IRavenQueryInspector)query).GetIndexQuery(false);
        var before = indexQuery.Query;

        SparkFuzzySearch.Rewrite(indexQuery, term, new HashSet<string> { "Model" });
        indexQuery.Query.Should().Be(before);

        SparkFuzzySearch.Rewrite(indexQuery, "*something else*", new HashSet<string> { "ModelSearch" });
        indexQuery.Query.Should().Be(before);
    }
}
