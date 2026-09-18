using MintPlayer.Spark.Abstractions.Model;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// Leading and trailing whitespace around a model or config file must not move the
/// model hash.
///
/// <para><b>This pins an existing property rather than fixing a defect.</b> Both hash
/// paths parse the JSON and hash a structural description of it
/// (<see cref="ModelFileShape.Describe(string)"/>,
/// <see cref="ConfigFileShape"/>), so whitespace outside the document has never
/// contributed. Measured end to end on 2026-09-18: prepending <c>"\r\n\t"</c> and
/// appending <c>"\n\n \t\n"</c> to a real model file left
/// <c>--spark-verify-model</c> reporting the same hash.</para>
///
/// <para>Why it is worth a test anyway. The property is load-bearing and invisible:
/// editors add and strip trailing newlines, <c>.gitattributes</c> rewrites line
/// endings on checkout, and the serializer emits no trailing newline at all — so a
/// file committed by a human and a file written by <c>--spark-synchronize-model</c>
/// routinely differ by exactly this. If the hash ever became byte-sensitive, the
/// symptom would be a model that verifies on one machine and fails CI on another,
/// with a diff that looks empty. The cheapest moment to notice that is here.</para>
///
/// <para>The boundary is deliberate: whitespace <i>outside</i> the JSON document is
/// ignored, whitespace <i>inside</i> a hashed string value is not — a label of
/// <c>"Name "</c> is a different label from <c>"Name"</c>, and the gate should say so.</para>
/// </summary>
public class ModelFileShapeWhitespaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "spark-ws-" + Guid.NewGuid().ToString("N"));

    public ModelFileShapeWhitespaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const string ModelJson = """
        {
          "persistentObject": {
            "name": "Probe",
            "clrType": "X.Probe",
            "attributes": [
              { "name": "Title", "dataType": "string", "isRequired": true }
            ],
            "queries": []
          },
          "queries": []
        }
        """;

    private string HashModelFile(string contents)
    {
        var modelDir = Path.Combine(_dir, Guid.NewGuid().ToString("N"), "App_Data", "Model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "Probe.json"), contents);

        var hashes = ModelFileShape.ComputeFileHashes(modelDir);
        return hashes["Probe.json"];
    }

    [Theory]
    [InlineData("\n")]                  // a trailing newline, the common editor difference
    [InlineData("\r\n")]                // CRLF, which .gitattributes can introduce on checkout
    [InlineData("   \t\r\n\n  ")]       // mixed trailing whitespace
    public void Trailing_whitespace_does_not_move_a_model_file_hash(string trailing)
    {
        HashModelFile(ModelJson + trailing).Should().Be(HashModelFile(ModelJson));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n\t")]
    [InlineData("  \n  ")]
    public void Leading_whitespace_does_not_move_a_model_file_hash(string leading)
    {
        HashModelFile(leading + ModelJson).Should().Be(HashModelFile(ModelJson));
    }

    [Fact]
    public void Leading_and_trailing_whitespace_together_do_not_move_a_model_file_hash()
    {
        HashModelFile("\r\n\t" + ModelJson + "\n\n   \t\n").Should().Be(HashModelFile(ModelJson));
    }

    /// <summary>
    /// The other half of the boundary: whitespace inside a hashed value is content, and
    /// must still move the hash. Without this, the test above could be satisfied by a
    /// hash that had stopped distinguishing anything at all.
    /// </summary>
    [Fact]
    public void Whitespace_inside_a_hashed_value_still_moves_the_hash()
    {
        var renamed = ModelJson.Replace("\"name\": \"Title\"", "\"name\": \"Title \"");

        HashModelFile(renamed).Should().NotBe(HashModelFile(ModelJson));
    }

    private const string CustomActionsJson = """
        {
          "actions": [
            { "name": "Approve", "showedOn": "PersistentObject", "selectionRule": "=1" }
          ]
        }
        """;

    private string HashConfigFile(string contents)
    {
        var appData = Path.Combine(_dir, Guid.NewGuid().ToString("N"), "App_Data");
        Directory.CreateDirectory(appData);
        File.WriteAllText(Path.Combine(appData, "customActions.json"), contents);

        var hashes = ConfigFileShape.ComputeFileHashes(appData);
        return hashes.TryGetValue("customActions.json", out var hash) ? hash : "(absent)";
    }

    /// <summary>
    /// `customActions.json` and `programUnits.json` are hand-authored far more often than
    /// the generated model files, so they meet a stray newline more often, not less.
    /// </summary>
    [Fact]
    public void Surrounding_whitespace_does_not_move_a_config_file_hash()
    {
        var padded = HashConfigFile("\r\n\t" + CustomActionsJson + "\n\n  \n");

        padded.Should().NotBe("(absent)");
        padded.Should().Be(HashConfigFile(CustomActionsJson));
    }
}
