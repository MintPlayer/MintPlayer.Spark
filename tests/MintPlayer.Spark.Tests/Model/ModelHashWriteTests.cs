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
    public void Altering_an_existing_model_file_invalidates_the_hash()
    {
        Synchronize();
        var original = ModelHashFile.Read(_contentRoot)!.ModelHash;

        var probeFile = Path.Combine(ModelHashFile.ModelDirectoryFor(_contentRoot), $"{nameof(HashProbe)}.json");
        File.WriteAllText(probeFile, File.ReadAllText(probeFile).Replace("\"isVisible\": true", "\"isVisible\": false"));

        Recompute().ModelHash.Should().NotBe(original);
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
    public void Line_ending_differences_do_not_invalidate_the_hash()
    {
        // The file is written on Windows and verified in a Linux container, with git rewriting line
        // endings in between. If this were sensitive, every containerised deployment would refuse to
        // start.
        Synchronize();
        var original = ModelHashFile.Read(_contentRoot)!.ModelHash;

        foreach (var file in Directory.GetFiles(ModelHashFile.ModelDirectoryFor(_contentRoot), "*.json"))
            File.WriteAllText(file, File.ReadAllText(file).Replace("\r\n", "\n").Replace("\n", "\r\n"));

        Recompute().ModelHash.Should().Be(original);
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
