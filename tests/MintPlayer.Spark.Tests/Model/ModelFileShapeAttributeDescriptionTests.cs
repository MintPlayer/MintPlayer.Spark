using MintPlayer.Spark.Abstractions.Model;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// An attribute's <c>description</c> is help text (#348). It is presentational, like <c>label</c>:
/// adding one or translating one is a supported hand-edit, so it must never move the structural hash
/// that gates startup — otherwise every translator's commit would refuse to boot the app.
/// </summary>
public class ModelFileShapeAttributeDescriptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "spark-shape-" + Guid.NewGuid().ToString("N"));

    public ModelFileShapeAttributeDescriptionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string HashOf(string attributeExtras)
    {
        var path = Path.Combine(_dir, "Probe.json");
        File.WriteAllText(path,
            $$"""
            {
              "persistentObject": {
                "name": "Probe",
                "clrType": "X.Probe",
                "attributes": [
                  {
                    "id": "22222222-2222-2222-2222-222222222222",
                    "name": "Title",
                    "label": { "en": "Title" },
                    "dataType": "String",
                    "isRequired": true{{attributeExtras}}
                  }
                ],
                "queries": []
              },
              "queries": []
            }
            """);

        return ModelFileShape.ComputeFileHashes(_dir)["Probe.json"];
    }

    [Fact]
    public void Adding_a_description_does_not_move_the_hash()
    {
        var before = HashOf("");
        var after = HashOf(", \"description\": { \"en\": \"What the title is for.\" }");

        after.Should().Be(before);
    }

    [Fact]
    public void Translating_a_description_does_not_move_the_hash()
    {
        var before = HashOf(", \"description\": { \"en\": \"What the title is for.\" }");
        var after = HashOf(", \"description\": { \"en\": \"What the title is for.\", \"nl\": \"Waarvoor de titel dient.\" }");

        after.Should().Be(before);
    }

    [Fact]
    public void Changing_a_structural_field_beside_a_description_still_moves_the_hash()
    {
        // Guard against the fix being "ignore the whole attribute": the allowlist must keep working.
        var before = HashOf(", \"description\": { \"en\": \"x\" }");
        var after = HashOf(", \"description\": { \"en\": \"x\" }, \"isReadOnly\": true");

        after.Should().NotBe(before);
    }
}
