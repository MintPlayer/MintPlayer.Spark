using System.Linq.Expressions;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Search;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.OpenSearchDescription;
using MintPlayer.Spark.Search;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Search;

// Fixture names start with Srch: ActionsResolver matches actions classes by simple name across the whole assembly.

public class SrchSong
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool Hidden { get; set; }
}

/// <summary>A hidden song is invisible to every read, as a row rule hides a row from the list page.</summary>
public class SrchSongActions(IEntityMapper entityMapper) : DefaultPersistentObjectActions<SrchSong>(entityMapper)
{
    public override Task<Expression<Func<SrchSong, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<Expression<Func<SrchSong, bool>>?>(s => !s.Hidden);
}

public class SrchContext : SparkContext
{
    public IRavenQueryable<SrchSong> SrchSongs => Session.Query<SrchSong>();
}

/// <summary>
/// A provider that knows nothing about security, as an index does not: it proposes every song, hidden or not, in
/// <c>Data/srch-songs.json</c>'s order, for searches and for label lookups alike.
/// </summary>
internal sealed class SrchEveryoneProvider : ISparkSearchProvider
{
    public static readonly string[] Ids = ["SrchSongs/1", "SrchSongs/2", "SrchSongs/10", "SrchSongs/3", "SrchSongs/4", "SrchSongs/5", "SrchSongs/6"];

    private static IReadOnlyList<SparkSearchCandidate> All(IReadOnlyCollection<string> types)
        => types.Contains(nameof(SrchSong)) ? Ids.Select(id => new SparkSearchCandidate(nameof(SrchSong), id)).ToList() : [];

    public Task<IReadOnlyList<SparkSearchCandidate>> SearchAsync(SparkSearchRequest request, CancellationToken cancellationToken)
        => Task.FromResult(All(request.EntityTypes));

    public Task<IReadOnlyList<SparkSearchCandidate>> FindByLabelAsync(string label, IReadOnlyCollection<string> entityTypes, CancellationToken cancellationToken)
        => Task.FromResult(All(entityTypes));
}

/// <summary>
/// Spark search's security half and labels (<c>docs/spark_search_PRD.md</c> D2–D4, D9), through the real endpoints and
/// the real read path against RavenDB: the provider proposes hidden rows too, and they must disappear without a trace,
/// including from the namesake count that numbers the labels (no existence oracle).
/// </summary>
public class SparkSearchTests : SparkTestDriver
{
    private static readonly Guid SongTypeId = Guid.Parse("5ea7c400-0000-4000-8000-000000000238");

    private SparkEndpointFactory<SrchContext> _factory = null!;
    private HttpClient _client = null!;

    internal static EntityTypeFile SongModel(bool fuzzy = false) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = SongTypeId,
            Name = nameof(SrchSong),
            ClrType = typeof(SrchSong).FullName!,
            Breadcrumb = "{Title}",
            Search = new EntityTypeSearch { Enabled = true, Fuzzy = fuzzy },
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = nameof(SrchSong.Title), DataType = "string" },
                new() { Id = Guid.NewGuid(), Name = nameof(SrchSong.Hidden), DataType = "boolean" },
            ],
        },
        Queries = [new SparkQuery { Id = Guid.NewGuid(), Name = "SrchSongs", Source = "Database.SrchSongs", EntityType = nameof(SrchSong) }],
    };

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await SeedFromJsonAsync("Search/Data/srch-songs.json");
        _factory = new SparkEndpointFactory<SrchContext>(Store, [SongModel()],
            configureSpark: spark =>
            {
                spark.AddSearch();
                spark.AddOpenSearchDescription(o => o.ShortName = "A site name longer than sixteen");
            },
            configureServices: services => services.AddScoped<ISparkSearchProvider, SrchEveryoneProvider>(),
            security: SparkTestSecurity.Permissive);
        _client = _factory.CreateClient();
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task<SparkSearchHit[]> SearchAsync(string q)
        => (await _client.GetFromJsonAsync<SparkSearchHit[]>($"/spark/search?q={Uri.EscapeDataString(q)}"))!;

    private async Task<SparkSearchHit?> ResolveAsync(string q)
    {
        var response = await _client.GetAsync($"/spark/search/resolve?q={Uri.EscapeDataString(q)}");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SparkSearchHit>();
    }

    [Fact]
    public async Task A_row_the_caller_may_not_read_is_never_a_hit()
    {
        var hits = await SearchAsync("anything");

        hits.Select(h => h.Id).Should().Equal(["SrchSongs/1", "SrchSongs/10", "SrchSongs/3", "SrchSongs/5"],
            "the provider proposed all seven in this order; the re-read through the list query drops the hidden ones");
    }

    [Fact]
    public async Task Visible_namesakes_are_numbered_by_natural_id_and_a_hidden_one_is_not_counted()
    {
        var hits = await SearchAsync("anything");

        hits.Single(h => h.Id == "SrchSongs/1").Label.Should().Be("Queen (Srch Song 1)");
        hits.Single(h => h.Id == "SrchSongs/10").Label.Should().Be("Queen (Srch Song 2)",
            "SrchSongs/2 is hidden: counting it would make this 3 and tell the caller a third Queen exists");
        hits.Single(h => h.Id == "SrchSongs/3").Label.Should().Be("Bohemian Rhapsody",
            "its only namesake is hidden, so the label needs no suffix");
        hits.Single(h => h.Id == "SrchSongs/3").Url.Should().StartWith("/po/").And.Contain("SrchSongs%2F3");
    }

    [Fact]
    public async Task A_label_resolves_to_exactly_the_object_it_names()
    {
        (await ResolveAsync("Queen (Srch Song 2)"))!.Id.Should().Be("SrchSongs/10");
        (await ResolveAsync("queen (srch song 1)"))!.Id.Should().Be("SrchSongs/1", "labels compare ignoring case");
        (await ResolveAsync("Bohemian Rhapsody"))!.Id.Should().Be("SrchSongs/3");
        // A title ending in parentheses of its own: "(Remix)" names no type, so the plain label lookup answers.
        (await ResolveAsync("Top 40 (Remix)"))!.Id.Should().Be("SrchSongs/5");
    }

    [Fact]
    public async Task A_text_naming_none_or_several_resolves_to_nothing()
    {
        (await ResolveAsync("Queen")).Should().BeNull("two visible songs are called Queen");
        (await ResolveAsync("Queen (Srch Song 3)")).Should().BeNull("there are two visible namesakes, not three");
        (await ResolveAsync("Secret")).Should().BeNull("the only Secret is hidden: indistinguishable from none");
        (await ResolveAsync("q")).Should().BeNull("below the minimum term length");
    }

    [Fact]
    public async Task Suggestions_carry_the_same_unambiguous_labels()
    {
        var hits = (await _client.GetFromJsonAsync<SparkSearchHit[]>("/spark/search/suggest?q=que"))!;

        hits.Select(h => h.Label).Should().Contain("Queen (Srch Song 1)").And.Contain("Queen (Srch Song 2)");
        hits.Select(h => h.Id).Should().NotContain("SrchSongs/2").And.NotContain("SrchSongs/4").And.NotContain("SrchSongs/6");
    }

    [Fact]
    public void The_prerendered_results_request_is_escaped_as_the_page_escapes_it()
        // The same string the ng-spark spec expects from SparkSearchService.search (encodeURIComponent).
        => SearchPrerenderContributor.EncodeUriComponent("rock 'n' roll & co").Should().Be("rock%20'n'%20roll%20%26%20co");

    // ---- MintPlayer.Spark.OpenSearchDescription (D9) ------------------------------------------------

    [Fact]
    public async Task The_description_caps_the_short_name_and_points_at_the_spark_endpoints()
    {
        var xml = await _client.GetStringAsync("/opensearch.xml");

        xml.Should().Contain("<ShortName>A site name long</ShortName>", "OpenSearch 1.1 allows 16 characters");
        xml.Should().Contain("/spark/opensearch/search?q={searchTerms}").And.Contain("/spark/opensearch/suggest?q={searchTerms}");
    }

    [Fact]
    public async Task Browser_suggestions_are_the_visible_labels()
    {
        var body = await _client.GetStringAsync("/spark/opensearch/suggest?q=que");

        body.Should().Contain("Queen (Srch Song 1)").And.Contain("Queen (Srch Song 2)").And.NotContain("Secret");
    }

    [Theory]
    [InlineData("Bohemian Rhapsody", "/po/")]
    [InlineData("Queen", "/search?q=Queen")]
    [InlineData("Secret", "/search?q=Secret")]
    public async Task A_browser_search_opens_the_one_object_it_names_or_the_results_page(string q, string expected)
    {
        var response = await _client.GetAsync($"/spark/opensearch/search?q={Uri.EscapeDataString(q)}");

        ((int)response.StatusCode).Should().BeInRange(300, 399);
        response.Headers.Location!.OriginalString.Should().StartWith(expected);
    }
}
