using MintPlayer.Spark.Abstractions.Model;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// Every field that gates a write is part of a model file's structural hash.
/// </summary>
/// <remarks>
/// That is the rule, and it is stated as one so the field list can be checked against it rather
/// than remembered. <c>EntityMapper.IsWritableBySchema</c> has exactly two gates, on adjacent
/// lines — <c>if (def.IsReadOnly) return false;</c> and <c>if (!def.IsVisible) return false;</c> —
/// so flipping either one in a deployed model file opens a write path.
/// <para>
/// ⚠️ <c>isReadOnly</c> was hashed and <c>isVisible</c> was not, which left a mass-assignment hole
/// exactly where the other was guarded. It was not hypothetical: four attributes across two apps
/// were protected by <c>isVisible</c> alone, <c>Repository.IsPrivate</c> among them — flipping it
/// writable lets a caller mark a private repository public.
/// </para>
/// <para>
/// The presentational cases are here too, because the value of the split is that it holds in both
/// directions: a translated label must never stop an application from starting.
/// </para>
/// </remarks>
public class ModelFileShapeWriteGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "spark-gate-" + Guid.NewGuid().ToString("N"));

    public ModelFileShapeWriteGateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string HashOf(string attributeJson)
    {
        var path = Path.Combine(_dir, "Probe.json");
        File.WriteAllText(path,
            $$"""
            {
              "persistentObject": {
                "name": "Probe",
                "clrType": "X.Probe",
                "attributes": [{{attributeJson}}],
                "queries": []
              },
              "queries": []
            }
            """);

        return ModelFileShape.ComputeFileHashes(_dir)["Probe.json"];
    }

    private const string Visible =
        """{ "name": "Secret", "dataType": "string", "isVisible": false, "isReadOnly": false }""";

    [Fact]
    public void Making_an_invisible_attribute_visible_moves_the_hash()
    {
        var hidden = HashOf(Visible);
        var shown = HashOf(
            """{ "name": "Secret", "dataType": "string", "isVisible": true, "isReadOnly": false }""");

        shown.Should().NotBe(hidden,
            "an invisible attribute is not writable, so making it visible opens a write path");
    }

    [Fact]
    public void Making_a_read_only_attribute_writable_moves_the_hash()
    {
        var locked = HashOf(
            """{ "name": "Secret", "dataType": "string", "isVisible": true, "isReadOnly": true }""");
        var open = HashOf(
            """{ "name": "Secret", "dataType": "string", "isVisible": true, "isReadOnly": false }""");

        open.Should().NotBe(locked);
    }

    [Fact]
    public void A_translated_label_does_not_move_the_hash()
    {
        var plain = HashOf(Visible);
        var labelled = HashOf(
            """
            { "name": "Secret", "dataType": "string", "isVisible": false, "isReadOnly": false,
              "label": { "en": "Secret", "nl": "Geheim" } }
            """);

        labelled.Should().Be(plain, "a translation must never stop an application from starting");
    }

    [Fact]
    public void Presentation_does_not_move_the_hash()
    {
        var plain = HashOf(Visible);
        var styled = HashOf(
            """
            { "name": "Secret", "dataType": "string", "isVisible": false, "isReadOnly": false,
              "order": 7, "group": "b2e84c07-9d13-4f56-8a2b-6c9e17f0a582",
              "renderer": "Badge", "description": { "en": "hello" } }
            """);

        styled.Should().Be(plain);
    }
}
