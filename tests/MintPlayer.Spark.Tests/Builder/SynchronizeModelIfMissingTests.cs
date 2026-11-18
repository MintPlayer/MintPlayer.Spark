using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Configuration;

namespace MintPlayer.Spark.Tests.Builder;

/// <summary>
/// <c>spark.SynchronizeModelIfMissing()</c> (templates PRD D13, §4.4): a generated app ships no model, so its
/// first Development run writes one. It must never touch a model that exists, and never run outside Development.
/// </summary>
[Collection(ProcessExitCodeCollection.Name)]
public class SynchronizeModelIfMissingTests
{
    private sealed class FirstRunContext : SparkContext
    {
        public IQueryable<FirstRunProbe> Probes => Session.Query<FirstRunProbe>();
    }

    private sealed class FirstRunProbe
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private static bool Start(ScratchContentRoot scratch, string environment, bool optIn)
    {
        var builder = scratch.CreateBuilder(environment);
        builder.Services.AddScoped<SparkContext, FirstRunContext>();
        builder.Services.AddSingleton(new SparkOptions { SynchronizeModelIfMissing = optIn });
        return builder.SynchronizeSparkModelsIfRequested([]);
    }

    private static string ModelFile(ScratchContentRoot scratch) => Path.Combine(scratch.ModelPath, "FirstRunProbe.json");
    private static string HashFile(ScratchContentRoot scratch) => Path.Combine(scratch.Path, "App_Data", "modelHashes.json");

    [Fact]
    public void The_first_Development_run_writes_the_model_and_starts()
    {
        using var scratch = new ScratchContentRoot();

        var handled = Start(scratch, Environments.Development, optIn: true);

        handled.Should().BeFalse("the app goes on to start; this is not a build command");
        Environment.ExitCode.Should().Be(0);
        File.Exists(ModelFile(scratch)).Should().BeTrue();
        File.Exists(HashFile(scratch)).Should().BeTrue("startup verifies the model against this file");
    }

    [Fact]
    public void An_existing_model_is_never_overwritten()
    {
        using var scratch = new ScratchContentRoot();
        Directory.CreateDirectory(scratch.ModelPath);
        var reviewed = Path.Combine(scratch.ModelPath, "Reviewed.json");
        File.WriteAllText(reviewed, "{ \"reviewed\": true }");

        Start(scratch, Environments.Development, optIn: true);

        File.ReadAllText(reviewed).Should().Be("{ \"reviewed\": true }");
        File.Exists(ModelFile(scratch)).Should().BeFalse("a model directory with files in it is the app's, not a first run");
        File.Exists(HashFile(scratch)).Should().BeFalse();
    }

    [Fact]
    public void Production_never_synchronizes()
    {
        using var scratch = new ScratchContentRoot();

        var handled = Start(scratch, Environments.Production, optIn: true);

        handled.Should().BeFalse();
        Directory.Exists(scratch.ModelPath).Should().BeFalse("a deployed app must ship its model, not write one");
    }

    [Fact]
    public void Without_the_option_nothing_is_written()
    {
        using var scratch = new ScratchContentRoot();

        Start(scratch, Environments.Development, optIn: false);

        Directory.Exists(scratch.ModelPath).Should().BeFalse();
    }
}
