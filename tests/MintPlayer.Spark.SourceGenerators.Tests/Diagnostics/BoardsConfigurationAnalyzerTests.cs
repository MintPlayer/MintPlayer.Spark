using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>SPARK057-059: <c>App_Data/boards.json</c> checked at build time (docs/spark_ssr_PRD.md D14, D15).</summary>
public class BoardsConfigurationAnalyzerTests
{
    private const string AnalyzerName = "BoardsConfigurationAnalyzer";

    private const string ModelJson = """
        {
          "persistentObject": {
            "id": "11111111-1111-1111-1111-111111111111",
            "name": "Song",
            "alias": "song"
          },
          "queries": [
            { "id": "22222222-2222-2222-2222-222222222222", "name": "GetSongs" },
            { "id": "33333333-3333-3333-3333-333333333333", "name": "Artists", "alias": "all-artists" }
          ]
        }
        """;

    private const string ProgramUnitsJson = """
        { "programUnitGroups": [ { "id": "b0000000-0000-0000-0000-000000000001", "programUnits": [ { "id": "b0000000-0000-0000-0000-000000000101" } ] } ] }
        """;

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string boardsJson, string source = "class Placeholder { }")
        => GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            [source],
            additionalTexts:
            [
                ("C:\\app\\App_Data\\boards.json", boardsJson),
                ("C:\\app\\App_Data\\Model\\Song.json", ModelJson),
                ("C:\\app\\App_Data\\programUnits.json", ProgramUnitsJson),
            ]);

    private static string Board(string tiles, string paths = "\"/home\"", string id = "home")
        => $$"""{ "boards": [ { "id": "{{id}}", "paths": [{{paths}}], "tiles": [{{tiles}}] } ] }""";

    [Fact]
    public async Task Targets_that_resolve_are_silent()
    {
        var diagnostics = await RunAsync(Board("""
            { "id": "a", "kind": "query", "target": { "query": "songs" } },
            { "id": "b", "kind": "count", "target": { "query": "ALL-ARTISTS" } },
            { "id": "c", "kind": "query", "target": { "query": "22222222-2222-2222-2222-222222222222" } },
            { "id": "d", "kind": "persistentObject", "target": { "entityType": "Song", "id": "songs/1" } },
            { "id": "e", "kind": "persistentObject", "target": { "entityType": "song", "id": "songs/1" } },
            { "id": "f", "kind": "programUnit", "target": { "programUnit": "b0000000-0000-0000-0000-000000000101" } }
            """));

        diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{ "id": "t", "kind": "query", "target": { "query": "artists" } }""", "query 'artists'")]
    [InlineData("""{ "id": "t", "kind": "count", "target": { "query": "sngs" } }""", "query 'sngs'")]
    [InlineData("""{ "id": "t", "kind": "persistentObject", "target": { "entityType": "Car", "id": "cars/1" } }""", "entity type 'Car'")]
    [InlineData("""{ "id": "t", "kind": "programUnit", "target": { "programUnit": "b0000000-0000-0000-0000-000000000999" } }""", "program unit")]
    [InlineData("""{ "id": "t", "kind": "chart", "target": {} }""", "kind 'chart'")]
    public async Task A_target_that_names_nothing_is_reported(string tile, string expected)
    {
        var diagnostics = await RunAsync(Board(tile));

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK059");
        diagnostic.GetMessage().Should().Contain(expected);
    }

    [Fact]
    public async Task An_application_kind_is_not_judged_when_the_project_declares_one()
    {
        const string kind = """
            namespace MintPlayer.Spark.Boards { public interface ITileKind { } }
            class ChartKind : MintPlayer.Spark.Boards.ITileKind { }
            """;

        var diagnostics = await RunAsync(Board("""{ "id": "t", "kind": "chart", "target": {} }"""), kind);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_board_over_the_default_cap_is_reported()
    {
        var tiles = string.Join(",", Enumerable.Range(1, 21).Select(i => $$"""{ "id": "t{{i}}", "kind": "query", "target": { "query": "songs" } }"""));

        var diagnostics = await RunAsync(Board(tiles));

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK057");
    }

    [Fact]
    public async Task A_bad_or_shared_path_is_an_error()
    {
        var json = """
            { "boards": [
              { "id": "a", "paths": ["/home", "home"], "tiles": [] },
              { "id": "b", "paths": ["/HOME"], "tiles": [] }
            ] }
            """;

        var diagnostics = await RunAsync(json);

        diagnostics.Should().HaveCount(2);
        diagnostics[0].GetMessage().Should().Contain("'home' must be a page path");
        diagnostics[1].GetMessage().Should().Contain("is already the path of board 'a'");
        diagnostics.Should().OnlyContain(d => d.Id == "SPARK058" && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Malformed_json_reports_nothing()
    {
        var diagnostics = await RunAsync("{ \"boards\": [ ");

        diagnostics.Should().BeEmpty();
    }
}
