using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.Abstractions.Search;
using MintPlayer.Spark.Search;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Tests.Search;

/// <summary>
/// The default provider (<c>docs/spark_search_PRD.md</c> D5) over the <c>Spark_Search</c> index the generator really
/// emits. This project is a library, so the generator does not run here: the index is the generator tests' snapshot
/// (<c>SearchIndexGeneratorTests.The_search_tests_index</c>), compiled at run time and deployed to the server, which
/// is what shows that the generated index compiles on RavenDB.
/// </summary>
public class RavenSearchProviderTests : SparkTestDriver
{
    private SparkEndpointFactory<SrchContext> _factory = null!;
    private HttpClient _client = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await SeedFromJsonAsync("Search/Data/srch-songs.json");
        await GeneratedIndex().ExecuteAsync(Store);
        WaitForIndexing(Store);

        _factory = new SparkEndpointFactory<SrchContext>(Store, [SparkSearchTests.SongModel(fuzzy: true)],
            configureSpark: spark => spark.AddSearch(),
            security: SparkTestSecurity.Permissive);
        _client = _factory.CreateClient();
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <summary>Compiles the snapshot against this process's assemblies, so it maps the real <see cref="SrchSong"/>.</summary>
    private static AbstractIndexCreationTask GeneratedIndex()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Search", "Data", "Spark_Search.generated.txt"));
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create(
            "SrchGenerated" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        using var dll = new MemoryStream();
        var emitted = compilation.Emit(dll);
        emitted.Success.Should().BeTrue(string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var type = Assembly.Load(dll.ToArray()).GetType("SrchGenerated.Indexes.Spark_Search", throwOnError: true)!;
        var index = (AbstractIndexCreationTask)Activator.CreateInstance(type)!;
        index.IndexName.Should().Be(SparkSearchIndex.Name);
        return index;
    }

    private async Task<string[]> IdsAsync(string url)
        => (await _client.GetFromJsonAsync<SparkSearchHit[]>(url))!.Select(h => h.Id).ToArray();

    [Fact]
    public async Task The_generated_index_compiles_on_the_server_and_maps_every_song()
    {
        var errors = await Store.Maintenance.SendAsync(new GetIndexErrorsOperation([SparkSearchIndex.Name]));
        errors.Single().Errors.Should().BeEmpty();

        using var session = Store.OpenAsyncSession();
        (await session.Advanced.AsyncDocumentQuery<SparkSearchEntry>(SparkSearchIndex.Name).CountAsync()).Should().Be(7);
    }

    [Fact]
    public async Task Hits_carry_the_stored_id_although_the_index_answers_lower_cased()
    {
        // The projection's ids are lower-cased (srchsongs/1); the read path matched them ordinally and dropped them all.
        var hits = (await _client.GetFromJsonAsync<SparkSearchHit[]>("/spark/search?q=bohemian"))!;

        hits.Single().Id.Should().Be("SrchSongs/3");
        hits.Single().Url.Should().Contain("SrchSongs%2F3");
    }

    [Fact]
    public async Task Every_word_must_match_and_hidden_rows_stay_hidden()
    {
        (await IdsAsync("/spark/search?q=queen")).Should().BeEquivalentTo(["SrchSongs/1", "SrchSongs/10"]);
        (await IdsAsync("/spark/search?q=bohemian%20rhapsody")).Should().Equal(["SrchSongs/3"]);
        (await IdsAsync("/spark/search?q=bohemian%20queen")).Should().BeEmpty();
    }

    [Fact]
    public async Task Suggestions_complete_the_last_word()
    {
        (await IdsAsync("/spark/search/suggest?q=bohem")).Should().Equal(["SrchSongs/3"]);
        (await IdsAsync("/spark/search?q=bohem")).Should().BeEmpty("a complete search does not complete words");
    }

    [Fact]
    public async Task A_fuzzy_type_tolerates_a_typo()
        => (await IdsAsync("/spark/search?q=bohemain")).Should().Equal(["SrchSongs/3"]);

    [Fact]
    public async Task A_label_resolves_through_the_index()
    {
        var response = await _client.GetAsync("/spark/search/resolve?q=Queen%20(Srch%20Song%202)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SparkSearchHit>())!.Id.Should().Be("SrchSongs/10");
    }
}
