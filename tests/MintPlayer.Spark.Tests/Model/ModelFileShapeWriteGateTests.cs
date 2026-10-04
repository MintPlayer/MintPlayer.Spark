using MintPlayer.Spark.Abstractions.Model;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// Every field that gates a write is part of a model file's structural hash.
/// </summary>
/// <remarks>
/// That is the rule, and it is stated as one so the field list can be checked against it rather
/// than remembered. <c>EntityMapper.IsWritableBySchema</c> has exactly one model gate,
/// <c>if (def.IsReadOnly) return false;</c>, so flipping it in a deployed model file opens a write path.
/// <para>
/// ⚠️ Until #264 it had a second one, <c>if (!def.IsVisible) return false;</c>. Visibility is layout, not
/// a write gate (G-Q7): it silently dropped the police report number a refresh hook had revealed on a
/// stolen car. A field is protected by <c>isReadOnly</c> or an <c>Edit</c>/<c>New</c> deny, and
/// <c>HiddenAttributesStayProtectedTests</c> holds every attribute that was hidden to one of those.
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

    private const string Writable =
        """{ "name": "Secret", "dataType": "string", "isReadOnly": false }""";

    [Fact]
    public void Making_a_read_only_attribute_writable_moves_the_hash()
    {
        var locked = HashOf(
            """{ "name": "Secret", "dataType": "string", "isReadOnly": true }""");
        var open = HashOf(Writable);

        open.Should().NotBe(locked);
    }

    [Fact]
    public void A_translated_label_does_not_move_the_hash()
    {
        var plain = HashOf(Writable);
        var labelled = HashOf(
            """
            { "name": "Secret", "dataType": "string", "isReadOnly": false,
              "label": { "en": "Secret", "nl": "Geheim" } }
            """);

        labelled.Should().Be(plain, "a translation must never stop an application from starting");
    }

    [Fact]
    public void Presentation_does_not_move_the_hash()
    {
        var plain = HashOf(Writable);
        var styled = HashOf(
            """
            { "name": "Secret", "dataType": "string", "isReadOnly": false,
              "order": 7, "group": "b2e84c07-9d13-4f56-8a2b-6c9e17f0a582",
              "renderer": "Badge", "description": { "en": "hello" }, "showedOn": "Query" }
            """);

        styled.Should().Be(plain);
    }
}
