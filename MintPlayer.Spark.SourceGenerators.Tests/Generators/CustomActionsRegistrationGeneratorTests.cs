using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

public class CustomActionsRegistrationGeneratorTests
{
    private const string GeneratorName = "CustomActionsRegistrationGenerator";

    [Fact]
    public void ICustomAction_implementation_is_registered_as_scoped_service()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Abstractions.Actions;

            namespace TestApp.Actions;

            public class ExportAction : ICustomAction
            {
                public Task ExecuteAsync(CustomActionArgs args, CancellationToken ct = default)
                    => Task.CompletedTask;
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(ICustomAction)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;

        generated.Should().Contain("internal static class SparkCustomActionsBuilderExtensions");
        generated.Should().Contain("AddCustomActions");
        generated.Should().Contain("AddScoped<global::TestApp.Actions.ExportAction>");
        generated.Should().Contain("return builder;");
    }

    [Fact]
    public void Abstract_ICustomAction_is_skipped()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Abstractions.Actions;

            namespace TestApp.Actions;

            public abstract class BaseAction : ICustomAction
            {
                public abstract Task ExecuteAsync(CustomActionArgs args, CancellationToken ct = default);
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(ICustomAction)],
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void No_source_when_project_does_not_reference_Spark_abstractions()
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
