using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Services;
using NSubstitute;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// Covers what synchronization writes alongside the model files: the hash that a deployed
/// application checks before it agrees to start.
/// </summary>
public class ModelHashWriteTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(
        Path.GetTempPath(), "spark-hashwrite-" + Guid.NewGuid().ToString("N"));

    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly IIndexRegistry _indexRegistry = Substitute.For<IIndexRegistry>();

    public ModelHashWriteTests()
    {
        Directory.CreateDirectory(_contentRoot);
        _hostEnv.ContentRootPath.Returns(_contentRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Synchronize() => new ModelSynchronizer(_hostEnv, _indexRegistry)
        .SynchronizeModels(new HashProbeContext());

    private string HashFilePath => ModelHashFile.PathFor(_contentRoot);

    [Fact]
    public void Synchronize_writes_the_hash_file_beside_the_model_directory()
    {
        Synchronize();

        File.Exists(HashFilePath).Should().BeTrue();

        // Deliberately NOT inside App_Data/Model: both the model loader and the generator enumerate
        // Model/*.json and deserialize every hit as an entity file, so a hash file there would log a
        // load error on every startup — next to a check that halts on model problems.
        File.Exists(Path.Combine(_contentRoot, "App_Data", "Model", ModelHashFile.FileName))
            .Should().BeFalse();
    }

    [Fact]
    public void The_hash_file_records_a_hash_per_entity_plus_a_roll_up()
    {
        Synchronize();

        var hashes = ModelHashFile.Read(_contentRoot)!;

        hashes.Version.Should().Be(1);
        hashes.ModelHash.Should().MatchRegex("^[0-9a-f]{64}$");
        hashes.ContextRoots.Should().MatchRegex("^[0-9a-f]{64}$");
        hashes.Entities.Should().ContainKey(nameof(HashProbe));
        hashes.Entities.Should().ContainKey(nameof(HashProbeDetail),
            "embedded complex types are part of the model and so part of its fingerprint");
    }

    [Fact]
    public void Re_synchronizing_an_unchanged_model_is_byte_stable()
    {
        // Everything downstream depends on this: a merge queue that regenerates and then diffs would
        // report drift on every run if synchronization were not idempotent, and the hash would churn
        // for no reason. Ids are minted only on first creation and preserved by reference after, so
        // this should hold — "should" being exactly the word worth testing rather than trusting.
        Synchronize();
        var first = ReadAllOutputs();

        Synchronize();
        var second = ReadAllOutputs();

        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public void The_hash_survives_a_round_trip_through_the_file()
    {
        Synchronize();

        var written = ModelHashFile.Read(_contentRoot)!;
        var recomputed = Recompute();

        recomputed.ModelHash.Should().Be(written.ModelHash);
        recomputed.Entities.Should().BeEquivalentTo(written.Entities);
    }

    // --- tamper detection ------------------------------------------------

    [Fact]
    public void Planting_an_extra_model_file_invalidates_the_hash()
    {
        // The entity hashes only describe what the CLR classes say the model should contain, so on
        // their own they would not notice a planted file — and the loader reads whatever is in the
        // directory, so that file would become a live entity definition.
        Synchronize();
        var original = ModelHashFile.Read(_contentRoot)!.ModelHash;

        File.WriteAllText(
            Path.Combine(ModelHashFile.ModelDirectoryFor(_contentRoot), "Injected.json"),
            """{ "persistentObject": { "name": "Injected" }, "queries": [] }""");

        Recompute().ModelHash.Should().NotBe(original);
    }

    [Fact]
    public void Altering_a_structural_field_invalidates_the_hash()
    {
        Synchronize();
        var original = ModelHashFile.Read(_contentRoot)!.ModelHash;

        Rewrite($"{nameof(HashProbe)}.json", json => json.Replace("\"dataType\": \"string\"", "\"dataType\": \"number\""));

        Recompute().ModelHash.Should().NotBe(original);
    }

    [Fact]
    public void Removing_a_validation_rule_invalidates_the_hash()
    {
        // Validation is structural, not styling: silently dropping a rule from a deployed model
        // weakens what the server accepts, which is exactly the edit worth noticing.
        const string noRules = "\"rules\": []";
        const string withRequiredRule = "\"rules\": [ { \"type\": \"required\" } ]";

        Synchronize();
        Rewrite($"{nameof(HashProbe)}.json", json => json.Replace(noRules, withRequiredRule));
        var withRule = Recompute().ModelHash;

        Rewrite($"{nameof(HashProbe)}.json", json => json.Replace(withRequiredRule, noRules));

        Recompute().ModelHash.Should().NotBe(withRule);
    }

    [Fact]
    public void Deleting_a_model_file_invalidates_the_hash()
    {
        Synchronize();
        var original = ModelHashFile.Read(_contentRoot)!.ModelHash;

        File.Delete(Path.Combine(ModelHashFile.ModelDirectoryFor(_contentRoot), $"{nameof(HashProbeDetail)}.json"));

        Recompute().ModelHash.Should().NotBe(original);
    }

    [Fact]
    public void Editing_a_label_does_not_invalidate_the_hash()
    {
        // Model JSON is hand-editable by design and synchronization preserves those edits. If a
        // translated label moved the hash, translating a caption would stop the application from
        // starting.
        Synchronize();
        var original = ModelHashFile.Read(_contentRoot)!.ModelHash;

        // Add a translated label and a renderer to the Name attribute — the sort of edit the model
        // is designed to carry. The attribute's own "name" is untouched: that IS structural.
        Rewrite($"{nameof(HashProbe)}.json", json => json.Replace(
            "\"name\": \"Name\",",
            "\"name\": \"Name\",\n      \"label\": { \"en\": \"Full name\", \"nl\": \"Volledige naam\" },\n      \"renderer\": \"bold\","));

        Recompute().ModelHash.Should().Be(original);
    }

    [Fact]
    public void Reordering_attributes_and_line_endings_do_not_invalidate_the_hash()
    {
        // Attribute order in the file is presentation. Line endings matter because the file is
        // written on Windows and verified in a Linux container, with git rewriting them in between.
        Synchronize();
        var original = ModelHashFile.Read(_contentRoot)!.ModelHash;

        foreach (var file in Directory.GetFiles(ModelHashFile.ModelDirectoryFor(_contentRoot), "*.json"))
            File.WriteAllText(file, File.ReadAllText(file).Replace("\r\n", "\n").Replace("\n", "\r\n"));

        Recompute().ModelHash.Should().Be(original);
    }

    [Fact]
    public void Reformatting_a_file_with_validation_rules_does_not_invalidate_the_hash()
    {
        // Regression: validation rules were hashed via GetRawText(), which returns the original
        // bytes including indentation and line endings. Git rewriting CRLF to LF between the Windows
        // machine that writes the file and the Linux container that verifies it would then have
        // stopped every containerised deployment from starting. Found end-to-end on a demo app.
        const string noRules = "\"rules\": []";
        const string withRules = "\"rules\": [ { \"type\": \"minLength\", \"value\": 2 } ]";

        Synchronize();
        Rewrite($"{nameof(HashProbe)}.json", json => json.Replace(noRules, withRules));
        var original = Recompute().ModelHash;

        // Convert to LF, then re-indent — neither changes what the rule means.
        Rewrite($"{nameof(HashProbe)}.json", json => json.Replace("\r\n", "\n"));
        Recompute().ModelHash.Should().Be(original, "line endings must not affect the hash");

        Rewrite($"{nameof(HashProbe)}.json", json => json.Replace(
            withRules.Replace("\r\n", "\n"),
            "\"rules\": [\n        {\n          \"value\": 2,\n          \"type\": \"minLength\"\n        }\n      ]"));

        Recompute().ModelHash.Should().Be(original,
            "reindenting and reordering keys within a rule must not affect the hash either");
    }

    [Fact]
    public void A_newline_inside_a_string_value_is_normalised_before_hashing()
    {
        // Parsing the JSON already removes the file's own line endings from the hash, so this covers
        // the remaining case: a newline carried inside a string value. Verified cross-platform by
        // publishing a self-contained spike and running it on Windows and on Linux under WSL — both
        // produced the same hashes, including for LF-converted copies of CRLF files.
        const string noRules = "\"rules\": []";

        Synchronize();
        Rewrite($"{nameof(HashProbe)}.json", json => json.Replace(
            noRules, "\"rules\": [ { \"type\": \"pattern\", \"message\": \"line1\\r\\nline2\" } ]"));
        var withCrLf = Recompute().ModelHash;

        Rewrite($"{nameof(HashProbe)}.json", json => json.Replace(
            "\"message\": \"line1\\r\\nline2\"", "\"message\": \"line1\\nline2\""));

        Recompute().ModelHash.Should().Be(withCrLf,
            "an escaped CRLF inside a value must hash the same as an escaped LF");
    }

    private void Rewrite(string fileName, Func<string, string> edit)
    {
        var path = Path.Combine(ModelHashFile.ModelDirectoryFor(_contentRoot), fileName);
        File.WriteAllText(path, edit(File.ReadAllText(path)));
    }

    private ModelHashFile Recompute()
        => ModelSynchronizer.BuildModelHashes(typeof(HashProbeContext), _indexRegistry, _contentRoot);

    [Fact]
    public void Read_returns_null_when_the_file_is_absent_or_malformed()
    {
        ModelHashFile.Read(_contentRoot).Should().BeNull();

        Directory.CreateDirectory(Path.GetDirectoryName(HashFilePath)!);
        File.WriteAllText(HashFilePath, "{ this is not json");

        ModelHashFile.Read(_contentRoot).Should().BeNull(
            "a corrupt file must not throw during startup — the caller decides what absence means");
    }

    private Dictionary<string, string> ReadAllOutputs()
    {
        var modelDir = Path.Combine(_contentRoot, "App_Data", "Model");
        var outputs = Directory.GetFiles(modelDir, "*.json")
            .ToDictionary(Path.GetFileName, File.ReadAllText, StringComparer.Ordinal)!;
        outputs[ModelHashFile.FileName] = File.ReadAllText(HashFilePath);
        return outputs!;
    }
}

public sealed class HashProbeDetail
{
    public string? Street { get; set; }
    public string? City { get; set; }
}

public sealed class HashProbe
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public HashProbeDetail? Detail { get; set; }
}

public sealed class HashProbeContext : SparkContext
{
    public IRavenQueryable<HashProbe> HashProbes => Session.Query<HashProbe>();
}
