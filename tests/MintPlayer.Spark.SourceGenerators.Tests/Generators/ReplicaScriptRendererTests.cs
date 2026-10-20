using MintPlayer.Spark.Replication.Abstractions.Rendering;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// The replica renderer shared by the <c>[Replicated]</c> generator and the replication owner (warehouse
/// PRD §4.2.1). The owner parses manifests a consumer posted, so the parser is the trust boundary: it must
/// refuse anything that is not exactly the canonical grammar with plain identifiers.
/// </summary>
public class ReplicaScriptRendererTests
{
    private static ReplicaManifest Cars() => new("Cars", "Cars",
    [
        new ReplicaField("LicensePlate", ["LicensePlate"]),
        new ReplicaField("OwnerName", ["Owner", "Name"]),
    ]);

    [Fact]
    public void ToJson_is_canonical()
        => ReplicaScriptRenderer.ToJson(Cars()).Should().Be(
            """{"v":1,"source":"Cars","target":"Cars","fields":[["LicensePlate","LicensePlate"],["OwnerName","Owner.Name"]]}""");

    [Fact]
    public void ToJson_and_TryParse_round_trip()
    {
        var json = ReplicaScriptRenderer.ToJson(Cars());

        ReplicaScriptRenderer.TryParse(json, out var parsed, out var error).Should().BeTrue();
        error.Should().BeNull();
        ReplicaScriptRenderer.ToJson(parsed!).Should().Be(json);
        parsed!.Fields[1].Path.Should().Equal("Owner", "Name");
    }

    [Fact]
    public void TryParse_tolerates_whitespace_between_tokens()
    {
        const string json = """
            {
                "v": 1,
                "source": "Cars",
                "target": "Cars",
                "fields": [ [ "OwnerName", "Owner.Name" ] ]
            }
            """;

        ReplicaScriptRenderer.TryParse(json, out var parsed, out _).Should().BeTrue();
        ReplicaScriptRenderer.ToJson(parsed!).Should().Be(
            """{"v":1,"source":"Cars","target":"Cars","fields":[["OwnerName","Owner.Name"]]}""");
    }

    [Theory]
    [InlineData("""{"v":1,"source":"Cars","target":"Cars","fields":[["A","Owner.Na-me"]]}""")]
    [InlineData("""{"v":1,"source":"Cars","target":"Cars","fields":[["A","loadDocument("]]}""")]
    [InlineData("""{"v":1,"source":"Cars","target":"Cars","fields":[["A","x"]],"extra":1}""")]
    [InlineData("""{"v":1,"source":"Cars","target":"Cars","script":"x","fields":[["A","x"]]}""")]
    [InlineData("""{"v":2,"source":"Cars","target":"Cars","fields":[["A","x"]]}""")]
    [InlineData("""{"v":1,"source":"Cars","target":"Cars","fields":[["A","x"]]}""")]
    [InlineData("""{"v":1,"source":"Cars');x('","target":"Cars","fields":[["A","x"]]}""")]
    [InlineData("""{"v":1,"source":"Cars","target":"Cars","fields":[]}""")]
    [InlineData("""{"v":1,"source":"Cars","target":"Cars","fields":[["Id","Id"]]}""")]
    [InlineData("""{"v":1,"source":"Cars","target":"Cars","fields":[["A","x"],["A","y"]]}""")]
    [InlineData("""{"v":1,"source":"Cars","target":"Cars","fields":[["A","x"]]} trailing""")]
    [InlineData("")]
    public void TryParse_refuses_anything_but_the_canonical_grammar(string json)
    {
        ReplicaScriptRenderer.TryParse(json, out var parsed, out var error).Should().BeFalse();
        parsed.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Fact]
    public void RenderScript_reads_nested_paths_null_safely()
        => ReplicaScriptRenderer.RenderScript(Cars()).Should().Be(
            "loadToCars({\n" +
            "    LicensePlate: this.LicensePlate,\n" +
            "    OwnerName: (this.Owner == null ? null : this.Owner.Name),\n" +
            "    '@metadata': { 'Spark-Replica-Source-Change-Vector': getMetadata(this)['@change-vector'] }\n" +
            "});");

    [Fact]
    public void RenderEtlScript_adds_the_source_module()
        => ReplicaScriptRenderer.RenderEtlScript(Cars(), "HR").Should().Contain(
            "'@metadata': { 'Spark-Replica-Source-Change-Vector': getMetadata(this)['@change-vector'], 'Spark-Replica-Source-Module': 'HR' }");

    [Theory]
    [InlineData("H'R")]
    [InlineData("HR Module")]
    [InlineData("")]
    public void RenderEtlScript_refuses_a_bad_module_name(string module)
    {
        var refused = false;
        try { ReplicaScriptRenderer.RenderEtlScript(Cars(), module); }
        catch (ArgumentException) { refused = true; }

        refused.Should().BeTrue();
    }

    [Fact]
    public void RenderScript_refuses_an_invalid_manifest()
    {
        var refused = false;
        try { ReplicaScriptRenderer.RenderScript(new ReplicaManifest("Cars", "Cars", [new ReplicaField("A", ["x)"])])); }
        catch (ArgumentException) { refused = true; }

        refused.Should().BeTrue();
    }

    [Fact]
    public void Hash_is_lowercase_sha256_of_manifest_newline_script()
    {
        var hash = ReplicaScriptRenderer.Hash("{}", "x");

        // sha256("{}\nx"), computed independently of the renderer's own hex formatting.
        var expected = "sha256:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("{}\nx")));
        hash.Should().Be(expected);
    }

    [Theory]
    [InlineData("Car", true)]
    [InlineData("_car9", true)]
    [InlineData("9car", false)]
    [InlineData("ca-r", false)]
    [InlineData("caré", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsIdentifier_is_ascii_only(string? value, bool expected)
        => ReplicaScriptRenderer.IsIdentifier(value).Should().Be(expected);
}
