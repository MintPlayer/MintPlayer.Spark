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
            rootNamespace: "Lib", outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication);

        result.GeneratedSources.Should().BeEmpty();
        result.GeneratorDiagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// The gate split (#536 M4d): an application on a store without native indexes (SQL, InMemory) gets the row type
    /// and the view, which claims the default the index would have, and nothing that needs RavenDB.
    /// </summary>
    [Fact]
    public void Without_RavenDB_Client_an_application_gets_the_view_as_its_default()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [Source],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(MintPlayer.Spark.Abstractions.Views.SparkView<>)],
            rootNamespace: "Lib", outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication);

        var source = result.GeneratedSources.Should().ContainSingle().Which.Source;
        source.Should().Contain("[global::MintPlayer.Spark.Abstractions.DefaultIndexAttribute]");
        source.Should().Contain("Cars_Overview_View : global::MintPlayer.Spark.Abstractions.Views.SparkView<VCar>");
        source.Should().NotContain("Raven").And.NotContain("FromIndexAttribute");
        // CS5001: the test source has no Main; everything the generator wrote compiles.
        result.FinalCompilationDiagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error && d.Id != "CS5001").Should().BeEmpty();
    }

    [Fact]
    public void A_class_library_without_RavenDB_Client_gets_no_view()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [Source],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(MintPlayer.Spark.Abstractions.Views.SparkView<>)],
            rootNamespace: "Lib");

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void With_RavenDB_Client_referenced_the_index_is_emitted()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [Source],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "Lib", outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication);

        result.GeneratedSources.Should().Contain(s => s.Source.Contains("AbstractIndexCreationTask<global::Lib.Entities.Car>"));
    }
}
