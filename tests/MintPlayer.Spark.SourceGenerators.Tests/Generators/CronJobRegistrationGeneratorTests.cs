using MintPlayer.Spark.Cron;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

public class CronJobRegistrationGeneratorTests
{
    private const string GeneratorName = "CronJobRegistrationGenerator";

    [Fact]
    public void ISparkCronJob_implementer_is_registered()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Cron;

            namespace TestApp.Jobs;

            public class NightlyCleanup : ISparkCronJob
            {
                public static string CronSchedule => "0 0 * * *";
                public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(ISparkCronJob)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;

        generated.Should().Contain("AddCronJobs");
        generated.Should().Contain("cron.AddJob<global::TestApp.Jobs.NightlyCleanup>()");
    }

    [Fact]
    public void No_source_without_Cron_reference()
    {
        var source = """
            namespace TestApp;
            public class Foo { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: Array.Empty<Type>(),
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
    }
}
