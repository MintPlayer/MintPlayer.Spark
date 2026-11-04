using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Search;
using MintPlayer.Spark.Search.OpenSearch;

namespace MintPlayer.Spark.Tests.Search;

/// <summary>
/// A test that needs a real OpenSearch engine: skipped unless <c>SPARK_OPENSEARCH_URI</c> names one
/// (<c>docs/spark_search_PRD.md</c> D14; CI runs a <c>services:</c> container).
/// </summary>
public sealed class OpenSearchFactAttribute : FactAttribute
{
    internal const string UriVariable = "SPARK_OPENSEARCH_URI";

    public OpenSearchFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UriVariable)))
            Skip = $"{UriVariable} is not set: no OpenSearch engine to test against.";
    }
}

/// <summary>
/// The OpenSearch provider's queries against a real engine (D6/D7): every word must match, the last as a prefix while
/// typing, typos only for fuzzy types, label lookups on whole words. Each test owns its own index.
/// </summary>
public sealed class OpenSearchEngineTests : IAsyncLifetime
{
    private ServiceProvider? _services;
    private OpenSearchEngine _engine = null!;
    private OpenSearchSearchProvider _provider = null!;
    private HttpClient _http = null!;
    private string _index = null!;

    public async Task InitializeAsync()
    {
        var uri = Environment.GetEnvironmentVariable(OpenSearchFactAttribute.UriVariable);
        if (string.IsNullOrWhiteSpace(uri))
            return; // Every test is skipped.

        _index = $"spark-search-test-{Guid.NewGuid():N}";
        var options = new SparkOpenSearchOptions { Uri = new Uri(uri), IndexName = _index, RequestTimeout = TimeSpan.FromSeconds(30) };
        options.Validate();

        var services = new ServiceCollection();
        services.AddHttpClient(OpenSearchEngine.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(uri.EndsWith('/') ? uri : uri + "/");
            client.Timeout = options.RequestTimeout;
        });
        _services = services.BuildServiceProvider();
        var factory = _services.GetRequiredService<IHttpClientFactory>();
        _http = factory.CreateClient(OpenSearchEngine.HttpClientName);
        _engine = new OpenSearchEngine(factory, options);
        _provider = new OpenSearchSearchProvider(_engine);

        await _engine.EnsureIndexAsync(CancellationToken.None);
        await _engine.BulkAsync(
        [
            Entry("Artists/1", "Artist", "Queen"),
            Entry("Artists/2", "Artist", "Queens of the Stone Age"),
            Entry("Songs/1", "Song", "Bohemian Rhapsody"),
            Entry("Songs/2", "Song", "Killer Queen"),
            Entry("Songs/3", "Song", "Café del Mar"),
            Entry("Albums/1", "Album", "A Night at the Opera"),
        ], [], CancellationToken.None);
        await RefreshAsync();
    }

    public async Task DisposeAsync()
    {
        if (_services is null)
            return;
        await _engine.DropAsync(CancellationToken.None);
        _http.Dispose();
        await _services.DisposeAsync();
    }

    private static SparkSearchEntry Entry(string id, string type, string text) => new() { Id = id, Type = type, Text = text };

    /// <summary>The engine makes writes searchable on its refresh interval; the sync does not wait, a test must.</summary>
    private async Task RefreshAsync()
        => (await _http.PostAsync($"{_index}/_refresh", null)).EnsureSuccessStatusCode();

    private async Task<string[]> SearchAsync(string term, string[] types, bool prefix = false, string[]? fuzzy = null)
        => (await _provider.SearchAsync(new SparkSearchRequest(term, types, 20, prefix, fuzzy ?? []), CancellationToken.None))
            .Select(c => c.Id).ToArray();

    [OpenSearchFact]
    public async Task Every_word_must_match_and_only_the_requested_types_are_searched()
    {
        (await SearchAsync("queen", ["Artist", "Song"])).Should().BeEquivalentTo(["artists/1", "songs/2"],
            because: "'Queens' is another word without the prefix flag");
        (await SearchAsync("killer queen", ["Artist", "Song"])).Should().Equal(["songs/2"]);
        (await SearchAsync("queen", ["Album"])).Should().BeEmpty();
    }

    [OpenSearchFact]
    public async Task While_typing_the_last_word_is_a_prefix()
    {
        (await SearchAsync("quee", ["Artist", "Song"], prefix: true)).Should().BeEquivalentTo(["artists/1", "artists/2", "songs/2"]);
        (await SearchAsync("night at the op", ["Album"], prefix: true)).Should().Equal(["albums/1"]);
        (await SearchAsync("quee", ["Artist", "Song"])).Should().BeEmpty("a complete search does not complete words");
    }

    [OpenSearchFact]
    public async Task Typos_are_tolerated_only_for_fuzzy_types()
    {
        (await SearchAsync("bohemain", ["Song", "Artist"], fuzzy: ["Song"])).Should().Equal(["songs/1"]);
        (await SearchAsync("bohemain", ["Song", "Artist"])).Should().BeEmpty();
        (await SearchAsync("qeen", ["Artist", "Song"], fuzzy: ["Song"])).Should().Equal(["songs/2"], "the artist Queen is not a fuzzy type");
    }

    [OpenSearchFact]
    public async Task Accents_and_case_are_folded()
        => (await SearchAsync("CAFE", ["Song"])).Should().Equal(["songs/3"]);

    [OpenSearchFact]
    public async Task A_label_lookup_matches_whole_words_only()
    {
        var hits = await _provider.FindByLabelAsync("Queen", ["Artist", "Song"], CancellationToken.None);
        hits.Select(h => h.Id).Should().BeEquivalentTo(["artists/1", "songs/2"]);
        hits.Single(h => h.Id == "artists/1").EntityType.Should().Be("Artist");
    }

    [OpenSearchFact]
    public async Task Deletes_remove_rows_and_a_missing_one_is_not_an_error()
    {
        await _engine.BulkAsync([], ["Songs/2", "Songs/404"], CancellationToken.None);
        await _engine.DeleteAsync("Artists/1", CancellationToken.None);
        await _engine.DeleteAsync("Artists/404", CancellationToken.None);
        await RefreshAsync();

        (await SearchAsync("queen", ["Artist", "Song"])).Should().BeEmpty();
    }

    [OpenSearchFact]
    public async Task A_hard_delete_in_the_stored_casing_removes_the_row_the_sync_copied_lower_cased()
    {
        // The sync reads ids from Spark_Search projections (lower-cased); IAfterDeleteCommitted has the stored casing.
        await _engine.BulkAsync([Entry("bands/7", "Artist", "Muse")], [], CancellationToken.None);
        await RefreshAsync();
        (await SearchAsync("muse", ["Artist"])).Should().Equal(["bands/7"]);

        await _engine.DeleteAsync("Bands/7", CancellationToken.None);
        await RefreshAsync();

        (await SearchAsync("muse", ["Artist"])).Should().BeEmpty();
    }

    [OpenSearchFact]
    public async Task The_sync_checkpoint_round_trips_and_is_never_a_hit()
    {
        (await _engine.ReadCheckpointAsync(CancellationToken.None)).Should().BeNull();

        var since = new DateTime(2026, 11, 4, 10, 30, 15, 123, DateTimeKind.Utc);
        await _engine.WriteCheckpointAsync(new SyncCheckpoint(since, 7), CancellationToken.None);
        await RefreshAsync();

        (await _engine.ReadCheckpointAsync(CancellationToken.None)).Should().Be(new SyncCheckpoint(since, 7));
        var all = await _engine.SearchAsync(new System.Text.Json.Nodes.JsonObject { ["match_all"] = new System.Text.Json.Nodes.JsonObject() }, 100, CancellationToken.None);
        all.Should().HaveCount(6, "the checkpoint document is stored in the same index but filtered from every result");
    }

    [OpenSearchFact]
    public async Task Ensuring_an_index_of_the_current_mapping_keeps_its_rows()
    {
        await _engine.EnsureIndexAsync(CancellationToken.None);

        (await SearchAsync("rhapsody", ["Song"])).Should().Equal(["songs/1"]);
    }
}
