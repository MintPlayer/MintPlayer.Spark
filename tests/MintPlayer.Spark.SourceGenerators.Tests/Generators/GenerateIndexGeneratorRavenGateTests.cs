using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// An entity library references Abstractions but not RavenDB.Client. Since #348 such a library takes
/// the Spark analyzer (for <c>AttributeDescriptionsGenerator</c>), which put <c>GenerateIndexGenerator</c>
/// in a compilation where its output — <c>AbstractIndexCreationTask</c> subclasses — cannot compile
/// (CS0400 on <c>global::Raven</c>). The host compiles those indexes from the referenced assembly; the
/// library must emit nothing.
/// </summary>
public class GenerateIndexGeneratorRavenGateTests
{
    private const string GeneratorName = "GenerateIndexGenerator";

    private const string Source = """
        using MintPlayer.Spark.Abstractions;

        namespace Lib.Entities;

        [GenerateIndex]
        public class Car
        {
            public string? Id { get; set; }
            public string Model { get; set; } = "";
        }
        """;

    [Fact]
    public void Without_RavenDB_Client_referenced_nothing_is_emitted()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [Source],
            referenceTypes: [typeof(GenerateIndexAttribute)],
            rootNamespace: "Lib");

        result.GeneratedSources.Should().BeEmpty();
        result.GeneratorDiagnostics.Should().BeEmpty();
    }

    [Fact]
    public void With_RavenDB_Client_referenced_the_index_is_emitted()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [Source],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "Lib");

        result.GeneratedSources.Should().Contain(s => s.Source.Contains("AbstractIndexCreationTask<global::Lib.Entities.Car>"));
    }
}
