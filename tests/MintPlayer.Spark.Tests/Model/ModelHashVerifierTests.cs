using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Model;

/// <summary>
/// The startup gate. These tests pin the behaviour an operator meets at 3am, so they assert on the
/// message as well as the outcome.
/// </summary>
public class ModelHashVerifierTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(
        Path.GetTempPath(), "spark-hashverify-" + Guid.NewGuid().ToString("N"));

    private readonly IHostEnvironment _hostEnv = Substitute.For<IHostEnvironment>();
    private readonly IIndexRegistry _indexRegistry = Substitute.For<IIndexRegistry>();
    private readonly List<string> _log = [];

    public ModelHashVerifierTests()
    {
        Directory.CreateDirectory(_contentRoot);
        _hostEnv.ContentRootPath.Returns(_contentRoot);
        Environment.SetEnvironmentVariable(ModelHashVerifier.OverrideVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ModelHashVerifier.OverrideVariable, null);
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Synchronize() => new ModelSynchronizer(_hostEnv, _indexRegistry)
        .SynchronizeModels(new HashProbeContext());

    private void Verify(bool isDevelopment = false) => ModelHashVerifier.Verify(
        typeof(HashProbeContext), _indexRegistry, _contentRoot, isDevelopment, _log.Add);

    private void PlantExtraModelFile() => File.WriteAllText(
        Path.Combine(ModelHashFile.ModelDirectoryFor(_contentRoot), "Injected.json"),
        "{ \"persistentObject\": { \"name\": \"Injected\", \"clrType\": \"X.Injected\" }, \"queries\": [] }");

    [Fact]
    public void A_model_in_sync_starts_silently()
    {
        Synchronize();

        Verify();

        _log.Should().BeEmpty();
    }

    [Fact]
    public void A_planted_model_file_stops_the_application()
    {
        Synchronize();
        PlantExtraModelFile();

        Action act = () => Verify();

        act.Should().Throw<SparkModelOutOfSyncException>()
            .WithMessage("*Injected.json*added since the model was generated*");
    }

    [Fact]
    public void A_missing_hash_file_stops_the_application()
    {
        // Fail closed. If a missing file meant "nothing to check", the whole control could be
        // bypassed by deleting one file.
        Synchronize();
        File.Delete(ModelHashFile.PathFor(_contentRoot));

        Action act = () => Verify();

        act.Should().Throw<SparkModelOutOfSyncException>()
            .WithMessage("*no readable model-hashes.json*");
    }

    [Fact]
    public void A_corrupt_hash_file_stops_the_application()
    {
        Synchronize();
        File.WriteAllText(ModelHashFile.PathFor(_contentRoot), "{ not json");

        Action act = () => Verify();

        act.Should().Throw<SparkModelOutOfSyncException>();
    }

    [Fact]
    public void Development_warns_instead_of_stopping()
    {
        // Drift in Development is the normal state — a developer adds a property and hits F5.
        // Warning rather than staying silent is what tells them before CI does.
        Synchronize();
        PlantExtraModelFile();

        Action act = () => Verify(isDevelopment: true);

        act.Should().NotThrow();
        _log.Should().ContainSingle().Which.Should().StartWith("WARNING:");
    }

    [Fact]
    public void The_message_names_the_command_that_fixes_it()
    {
        Synchronize();
        PlantExtraModelFile();

        var message = Record.Exception(() => Verify())!.Message;

        message.Should().Contain("dotnet run --spark-synchronize-model");
        message.Should().Contain("published");  // the deployment-skew hint
        message.Should().Contain(ModelHashVerifier.OverrideVariable);
    }

    [Fact]
    public void The_override_starts_the_application_when_it_carries_the_actual_hash()
    {
        Synchronize();
        PlantExtraModelFile();
        var actual = ModelSynchronizer.BuildModelHashes(typeof(HashProbeContext), _indexRegistry, _contentRoot);

        Environment.SetEnvironmentVariable(ModelHashVerifier.OverrideVariable, actual.ModelHash);

        Action act = () => Verify();

        act.Should().NotThrow();
        _log.Should().ContainSingle().Which.Should().Contain("OVERRIDDEN");
    }

    [Fact]
    public void The_override_warns_on_every_startup_rather_than_once()
    {
        Synchronize();
        PlantExtraModelFile();
        var actual = ModelSynchronizer.BuildModelHashes(typeof(HashProbeContext), _indexRegistry, _contentRoot);
        Environment.SetEnvironmentVariable(ModelHashVerifier.OverrideVariable, actual.ModelHash);

        Verify();
        Verify();
        Verify();

        // An override that stops being visible stops being temporary.
        _log.Should().HaveCount(3);
    }

    [Fact]
    public void A_stale_override_value_still_stops_the_application()
    {
        // The property that stops the override becoming permanent: it names one build's model, so
        // the next model change invalidates it. A boolean would have survived indefinitely.
        Synchronize();
        var beforeDrift = ModelHashFile.Read(_contentRoot)!.ModelHash;
        Environment.SetEnvironmentVariable(ModelHashVerifier.OverrideVariable, beforeDrift);

        PlantExtraModelFile();

        Action act = () => Verify();

        act.Should().Throw<SparkModelOutOfSyncException>();
    }

    [Fact]
    public void An_arbitrary_override_value_does_not_disable_the_check()
    {
        Synchronize();
        PlantExtraModelFile();
        Environment.SetEnvironmentVariable(ModelHashVerifier.OverrideVariable, "true");

        Action act = () => Verify();

        act.Should().Throw<SparkModelOutOfSyncException>(
            "the override takes a specific hash, so there is no 'set it to anything truthy' path");
    }

    [Fact]
    public void Editing_a_label_does_not_stop_the_application()
    {
        Synchronize();

        var probe = Path.Combine(ModelHashFile.ModelDirectoryFor(_contentRoot), $"{nameof(HashProbe)}.json");
        File.WriteAllText(probe, File.ReadAllText(probe).Replace(
            "\"name\": \"Name\",",
            "\"name\": \"Name\",\n      \"label\": { \"nl\": \"Volledige naam\" },"));

        Action act = () => Verify();

        act.Should().NotThrow("hand-editing presentation is a supported workflow");
    }
}
