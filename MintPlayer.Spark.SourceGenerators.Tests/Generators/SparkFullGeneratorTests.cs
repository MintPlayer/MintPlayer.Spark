using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Tests for the AllFeatures composition generator that emits the top-level
/// <c>AddSparkFull</c>/<c>UseSparkFull</c>/<c>MapSparkFull</c> extension methods.
/// Lives in a separate generator assembly; loaded via <c>generatorAssemblyName</c>.
/// </summary>
public class SparkFullGeneratorTests
{
    private const string GeneratorName = "SparkFullGenerator";
    private const string GeneratorAssembly = "MintPlayer.Spark.AllFeatures.SourceGenerators";

    [Fact]
    public void Emits_nothing_when_Spark_is_not_referenced()
    {
        var source = """
            namespace TestApp;
            public class Foo { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            rootNamespace: "TestApp",
            generatorAssemblyName: GeneratorAssembly);

        // Without a Spark reference (SparkContext is the gate), the generator doesn't
        // emit the AddSparkFull extension — nothing for it to wire up.
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void SparkContext_subclass_is_woven_into_the_generated_registration()
    {
        var source = """
            using MintPlayer.Spark;

            namespace TestApp;

            public class AppContext : SparkContext { }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(SparkContext)],
            rootNamespace: "TestApp",
            generatorAssemblyName: GeneratorAssembly);

        var combined = string.Join("\n", result.GeneratedSources.Select(s => s.Source));
        combined.Should().Contain("TestApp.AppContext");
    }

    [Fact]
    public void SparkUser_subclass_is_routed_through_AddAuthentication()
    {
        var source = """
            using MintPlayer.Spark;
            using MintPlayer.Spark.Authorization.Identity;

            namespace TestApp;

            public class AppContext : SparkContext { }
            public class AppUser : SparkUser { public string DisplayName { get; set; } = ""; }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(SparkContext), typeof(SparkUser)],
            rootNamespace: "TestApp",
            generatorAssemblyName: GeneratorAssembly);

        var combined = string.Join("\n", result.GeneratedSources.Select(s => s.Source));
        combined.Should().Contain("TestApp.AppUser");
        combined.Should().Contain("AddAuthentication");
    }
}
