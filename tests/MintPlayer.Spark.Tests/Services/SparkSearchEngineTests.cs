using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Commerce X18: Spark chooses each index's search engine from its declarations. A <c>[Fuzzy]</c> field puts the
/// index on Lucene, because Corax refuses fuzzy queries at runtime; every other index keeps the server default; and
/// an engine change on an existing index is rebuilt side by side, verified after deployment.
/// </summary>
public class SparkSearchEngineTests : SparkTestDriver
{
    public class Place
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class TypoPlace
    {
        public string? Id { get; set; }
        [Fuzzy] public string Name { get; set; } = string.Empty;
    }

    /// <summary>Unmarked: no projection and no mapped type marks a field fuzzy.</summary>
    public class Places_Plain : AbstractIndexCreationTask<Place>
    {
        public Places_Plain() { Map = places => from p in places select new { p.Name }; }
    }

    /// <summary>Marked through its projection, <see cref="VFuzzyPlace"/>.</summary>
    public class Places_Fuzzy : AbstractIndexCreationTask<Place>
    {
        public Places_Fuzzy() { Map = places => from p in places select new { p.Name }; }
    }

    [FromIndex(typeof(Places_Fuzzy))]
    public class VFuzzyPlace
    {
        public string? Id { get; set; }
        [Fuzzy] public string Name { get; set; } = string.Empty;
    }

    /// <summary>Marked through its mapped document type.</summary>
    public class TypoPlaces_ByName : AbstractIndexCreationTask<TypoPlace>
    {
        public TypoPlaces_ByName() { Map = places => from p in places select new { p.Name }; }
    }

    // ⚠️ The next three take a constructor argument on purpose. RavenIndexHelperSmokeTests deploys this whole
    // assembly, which instantiates only parameterless index classes: one of these is meant to fail and two share
    // an index name.

    /// <summary>Marked, but pinned to the engine that refuses fuzzy queries.</summary>
    public class Places_PinnedCorax : AbstractIndexCreationTask<Place>
    {
        public Places_PinnedCorax(bool explicitOnly)
        {
            Map = places => from p in places select new { p.Name };
            SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        }
    }

    [FromIndex(typeof(Places_PinnedCorax))]
    public class VPinnedPlace
    {
        [Fuzzy] public string Name { get; set; } = string.Empty;
    }

    /// <summary>The same index before X18: deployed on Corax, as a Community server's default would.</summary>
    public class Typeahead_Before : AbstractIndexCreationTask<Place>
    {
        public override string IndexName => "Places/Typeahead";

        public Typeahead_Before(bool explicitOnly)
        {
            Map = places => from p in places select new { p.Name };
            SearchEngineType = Raven.Client.Documents.Indexes.SearchEngineType.Corax;
        }
    }

    /// <summary>The same index after its projection marked the field fuzzy: same map, no pin.</summary>
    public class Typeahead_After : AbstractIndexCreationTask<Place>
    {
        public override string IndexName => "Places/Typeahead";

        public Typeahead_After(bool explicitOnly) { Map = places => from p in places select new { p.Name }; }
    }

    [FromIndex(typeof(Typeahead_After))]
    public class VTypeahead
    {
        [Fuzzy] public string Name { get; set; } = string.Empty;
    }

    private static readonly Type[] Projections = [typeof(VFuzzyPlace), typeof(VPinnedPlace), typeof(VTypeahead), typeof(Place)];

    [Fact]
    public void Only_marked_indexes_are_put_on_Lucene()
    {
        var plain = new Places_Plain();
        var byProjection = new Places_Fuzzy();
        var byDocument = new TypoPlaces_ByName();

        SparkSearchEngine.Apply([plain, byProjection, byDocument], SparkSearchEngine.FuzzyIndexTypes(Projections));

        plain.SearchEngineType.HasValue.Should().BeFalse("an unmarked index keeps the server default; pinning would rebuild it");
        byProjection.SearchEngineType.Should().Be(SearchEngineType.Lucene);
        byDocument.SearchEngineType.Should().Be(SearchEngineType.Lucene);
    }

    [Fact]
    public void A_marked_index_pinned_to_Corax_fails_instead_of_deploying()
    {
        var act = () => SparkSearchEngine.Apply([new Places_PinnedCorax(explicitOnly: true)], SparkSearchEngine.FuzzyIndexTypes(Projections));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Places/PinnedCorax*SearchEngineType.Corax*");
    }

    [Fact]
    public async Task A_marked_index_deploys_on_Lucene_and_answers_a_fuzzy_query()
    {
        using (var session = Store.OpenSession())
        {
            session.Store(new Place { Name = "Brussels" });
            session.Store(new Place { Name = "Antwerp" });
            session.SaveChanges();
        }

        var task = new Places_Fuzzy();
        SparkSearchEngine.Apply([task], SparkSearchEngine.FuzzyIndexTypes(Projections));
        await IndexCreation.CreateIndexesAsync([task], Store);
        await Store.WaitForIndexingAsync();

        var stats = await Store.Maintenance.SendAsync(new GetIndexStatisticsOperation("Places/Fuzzy"));
        stats.SearchEngineType.Should().Be(SearchEngineType.Lucene);

        using var query = Store.OpenSession();
        var hits = query.Advanced.DocumentQuery<Place, Places_Fuzzy>()
            .WhereEquals(nameof(Place.Name), "brusels")
            .Fuzzy(0.8m)
            .ToList();
        hits.Should().ContainSingle().Which.Name.Should().Be("Brussels");
    }

    /// <summary>
    /// An existing Corax index whose field becomes [Fuzzy] ends up on Lucene with every entry, without being dropped:
    /// the redeploy (and, if RavenDB kept the old engine, the reconcile's side-by-side reset) rebuilds it.
    /// </summary>
    [Fact]
    public async Task An_engine_change_on_an_existing_index_is_rebuilt_on_Lucene()
    {
        using (var session = Store.OpenSession())
        {
            for (var i = 0; i < 20; i++)
                session.Store(new Place { Name = $"Place {i}" });
            session.Store(new Place { Name = "Brussels" });
            session.SaveChanges();
        }

        await IndexCreation.CreateIndexesAsync([new Typeahead_Before(explicitOnly: true)], Store);
        await Store.WaitForIndexingAsync();
        (await Store.Maintenance.SendAsync(new GetIndexStatisticsOperation("Places/Typeahead")))
            .SearchEngineType.Should().Be(SearchEngineType.Corax);

        var after = new Typeahead_After(explicitOnly: true);
        SparkSearchEngine.Apply([after], SparkSearchEngine.FuzzyIndexTypes(Projections));
        after.SearchEngineType.Should().Be(SearchEngineType.Lucene);

        await IndexCreation.CreateIndexesAsync([after], Store);
        SparkSearchEngine.Reconcile(Store, [after]);
        await Store.WaitForIndexingAsync();

        // A failure bound, not a pacing delay: the swap of a side-by-side replacement lands after it is non-stale.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        IndexStats stats;
        while (true)
        {
            stats = await Store.Maintenance.SendAsync(new GetIndexStatisticsOperation("Places/Typeahead"));
            if (stats.SearchEngineType == SearchEngineType.Lucene || DateTime.UtcNow >= deadline) break;
            await Task.Delay(100);
        }

        stats.SearchEngineType.Should().Be(SearchEngineType.Lucene);
        stats.EntriesCount.Should().Be(21);

        // Deterministic: the same build deploys the same definition, so a second deploy changes nothing.
        SparkSearchEngine.Reconcile(Store, [after]).Should().BeEmpty();
    }
}
