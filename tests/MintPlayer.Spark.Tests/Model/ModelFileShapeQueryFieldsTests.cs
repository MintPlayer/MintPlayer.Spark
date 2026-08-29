using MintPlayer.Spark.Abstractions.Model;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// What a query contributes to a model file's structural hash (#327 M3).
/// <para>
/// The hash is what stops a deployed model file being edited without anyone noticing, so the
/// question these answer is narrow: does changing a security-relevant query field move it?
/// Before this change the answer was no — a query contributed nothing at all unless it carried
/// an <c>indexName</c>, and <c>source</c> and <c>entityType</c> were never hashed even then.
/// </para>
/// </summary>
public class ModelFileShapeQueryFieldsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "spark-shape-" + Guid.NewGuid().ToString("N"));

    public ModelFileShapeQueryFieldsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Writes one model file and returns its structural hash.</summary>
    private string HashOf(string queriesJson)
    {
        var path = Path.Combine(_dir, "Probe.json");
        File.WriteAllText(path,
            $$"""
            {
              "persistentObject": {
                "name": "Probe",
                "clrType": "X.Probe",
                "attributes": [],
                "queries": []
              },
              "queries": {{queriesJson}}
            }
            """);

        return ModelFileShape.ComputeFileHashes(_dir)["Probe.json"];
    }

    private const string NoIndexName =
        """[ { "id": "11111111-1111-1111-1111-111111111111", "name": "All", "source": "Custom.All", "entityType": "Probe" } ]""";

    [Fact]
    public void Adding_a_query_with_no_indexName_moves_the_hash()
    {
        // The hole this closes: "queries": [] and a file carrying a WHOLE composed query used to
        // hash identically, because a query with no indexName contributed no line at all. That is
        // exactly the shape a composed query has.
        var empty = HashOf("[]");
        var withQuery = HashOf(NoIndexName);

        withQuery.Should().NotBe(empty);
    }

    [Fact]
    public void Changing_a_querys_source_moves_the_hash()
    {
        // `source` names the method that produces the rows, and a Custom.* method runs with no row
        // security. Repointing it on a deployed model must not pass the gate.
        var before = HashOf(NoIndexName);
        var after = HashOf(NoIndexName.Replace("Custom.All", "Custom.Everything"));

        after.Should().NotBe(before);
    }

    [Fact]
    public void Changing_a_querys_entityType_moves_the_hash()
    {
        // `entityType` chooses the right that gates the request and selects which actions class is
        // invoked — the single most security-relevant field on a query.
        var before = HashOf(NoIndexName);
        var after = HashOf(NoIndexName.Replace("\"entityType\": \"Probe\"", "\"entityType\": \"Other\""));

        after.Should().NotBe(before);
    }

    [Fact]
    public void Turning_a_query_into_a_streaming_query_moves_the_hash()
    {
        var before = HashOf(NoIndexName);
        var after = HashOf(NoIndexName.Replace(
            "\"entityType\": \"Probe\"", "\"entityType\": \"Probe\", \"isStreamingQuery\": true"));

        after.Should().NotBe(before);
    }

    [Fact]
    public void Changing_a_querys_presentation_does_not_move_the_hash()
    {
        // The other half of the contract: hand-editing presentation is a supported workflow, and a
        // hash that moves for a description or a sort order trains people to re-stamp without
        // reading the drift.
        var before = HashOf(NoIndexName);
        var after = HashOf(NoIndexName.Replace(
            "\"entityType\": \"Probe\"",
            "\"entityType\": \"Probe\", \"description\": \"now with prose\", \"renderMode\": \"VirtualScrolling\""));

        after.Should().Be(before);
    }

    [Fact]
    public void Reordering_queries_does_not_move_the_hash()
    {
        // Queries are hashed name-sorted, for the same reason attributes are: order in the file is
        // presentation, and reordering must not read as tampering.
        const string a = """{ "id": "aaaaaaaa-1111-1111-1111-111111111111", "name": "Alpha", "source": "Custom.A", "entityType": "Probe" }""";
        const string b = """{ "id": "bbbbbbbb-1111-1111-1111-111111111111", "name": "Beta", "source": "Custom.B", "entityType": "Probe" }""";

        HashOf($"[ {a}, {b} ]").Should().Be(HashOf($"[ {b}, {a} ]"));
    }
}
