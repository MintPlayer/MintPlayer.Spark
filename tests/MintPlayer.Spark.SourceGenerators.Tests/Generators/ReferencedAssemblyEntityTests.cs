using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// <c>[GenerateIndex]</c> sits on the entity, which routinely lives in a lean class library, while the index
/// and index entity belong to the application project. The generator therefore has to discover entities it
/// has no syntax for, from referenced assembly metadata.
/// <para>This is what keeps the reference arrows pointing app → library. Declaring the link from the entity
/// side instead — the reference implementation's <c>[QueryType(typeof(VCar))]</c> — drags the index entity
/// into the library, and its <c>[FromIndex]</c> then drags the index in too.</para>
/// </summary>
public class ReferencedAssemblyEntityTests
{
    private const string GeneratorName = "GenerateIndexGenerator";

    private const string EntityLibrarySource = """
        using MintPlayer.Spark.Abstractions;

        namespace Fleet.Library.Entities;

        [GenerateIndex]
        public class Car
        {
            public string? Id { get; set; }
            [Search] public string? Model { get; set; }
            public int Year { get; set; }
        }
        """;

    private static GeneratorRunResult RunWithLibrary(string appSource, string librarySource = EntityLibrarySource)
    {
        var library = GeneratorHarness.CompileToMetadataReference(
            "Fleet.Library",
            [librarySource],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)]);

        return GeneratorHarness.Run(
            GeneratorName,
            [appSource],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "Fleet", outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            additionalReferences: [library]);
    }

    [Fact]
    public void An_entity_in_a_referenced_assembly_produces_an_index_in_the_app()
    {
        var result = RunWithLibrary("namespace Fleet; public class Program { }");

        var generated = result.GeneratedSources.Should().ContainSingle().Which.Source;

        // The index lands in the APP's namespace, over the LIBRARY's entity type.
        generated.Should().Contain("namespace Fleet.Indexes");
        generated.Should().Contain("public partial class Cars_Overview : global::Raven.Client.Documents.Indexes.AbstractIndexCreationTask<global::Fleet.Library.Entities.Car>");
        generated.Should().Contain("public partial class VCar");
    }

    [Fact]
    public void Search_still_works_across_the_assembly_boundary()
    {
        var generated = RunWithLibrary("namespace Fleet; public class Program { }")
            .GeneratedSources[0].Source;

        generated.Should().Contain("Index(nameof(VCar.ModelSearch), global::Raven.Client.Documents.Indexes.FieldIndexing.Search);");
        generated.Should().Contain("ModelSearch = car.Model,");
    }

    /// <summary>
    /// Nothing generated may be referenced by the library — that is the whole point of discovering from the
    /// app side. The entity appears only as a generic argument and a map source.
    /// </summary>
    [Fact]
    public void The_library_gains_no_reference_to_anything_generated()
    {
        var generated = RunWithLibrary("namespace Fleet; public class Program { }")
            .GeneratedSources[0].Source;

        generated.Should().NotContain("namespace Fleet.Library");
        generated.Should().NotContain("QueryType");
    }

    [Fact]
    public void Source_and_referenced_entities_are_both_generated()
    {
        var generated = RunWithLibrary("""
            using MintPlayer.Spark.Abstractions;

            namespace Fleet.Entities;

            [GenerateIndex]
            public class Driver
            {
                public string? Name { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("Cars_Overview");
        generated.Should().Contain("Drivers_Overview");
    }

    /// <summary>
    /// An assembly that cannot even reference the attribute is skipped without walking its types. Without
    /// that filter the scan walks every type in every reference, the BCL included.
    /// </summary>
    [Fact]
    public void An_assembly_that_does_not_reference_Spark_contributes_nothing()
    {
        var unrelated = GeneratorHarness.CompileToMetadataReference(
            "Unrelated.Library",
            ["""
            namespace Unrelated.Library;

            public class Widget
            {
                public string? Name { get; set; }
            }
            """]);

        var result = GeneratorHarness.Run(
            GeneratorName,
            ["namespace Fleet; public class Program { }"],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "Fleet", outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            additionalReferences: [unrelated]);

        result.GeneratedSources.Should().BeEmpty();
    }

    /// <summary>
    /// Nullability, tuple names and friends are encoded as real metadata attributes on a property in a
    /// referenced assembly, and <c>GetAttributes()</c> returns them beside the author's own. Copying them
    /// emits <c>[NullableAttribute]</c> explicitly, which is CS8623 — a compile error in the generated file
    /// that appears only once the entity lives in another assembly, never in source. Found by the real build,
    /// not by these tests, which is why it gets one.
    /// </summary>
    [Fact]
    public void Compiler_synthesized_metadata_attributes_are_not_copied()
    {
        var generated = RunWithLibrary(
            "namespace Fleet; public class Program { }",
            librarySource: """
            using MintPlayer.Spark.Abstractions;

            namespace Fleet.Library.Entities;

            [GenerateIndex]
            public class Car
            {
                public string? Nullable { get; set; }
                public string NotNullable { get; set; } = string.Empty;
            }
            """).GeneratedSources[0].Source;

        generated.Should().NotContain("Nullable]");
        generated.Should().NotContain("NullableAttribute");
        generated.Should().NotContain("System.Runtime.CompilerServices");
    }

    /// <summary>
    /// #388: an entity library references MintPlayer.Spark.Attributes and MintPlayer.Spark.Model, never the
    /// Web-SDK Abstractions. The library here is compiled against exactly those two assemblies (no Raven
    /// either; <see cref="GeneratorHarness.CompileToMetadataReference"/> throws if it does not compile), uses
    /// the Model types the real libraries use, and the app still generates its index.
    /// </summary>
    [Fact]
    public void A_library_built_on_Attributes_and_Model_alone_produces_an_index_in_the_app()
    {
        Type[] vocabulary = [typeof(GenerateIndexAttribute), typeof(TranslatedString)];
        typeof(GenerateIndexAttribute).Assembly.GetName().Name.Should().Be("MintPlayer.Spark.Attributes");
        typeof(TranslatedString).Assembly.GetName().Name.Should().Be("MintPlayer.Spark.Model");

        var library = GeneratorHarness.CompileToMetadataReference(
            "Fleet.Library",
            ["""
            using MintPlayer.Spark.Abstractions;

            namespace Fleet.Library.Entities;

            public sealed class CarStatus : TransientLookupReference
            {
                public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
            }

            [GenerateIndex]
            public class Car
            {
                public string? Id { get; set; }
                [Search] public string? Model { get; set; }
                [LookupReference(typeof(CarStatus))] public string? Status { get; set; }
                public TranslatedString? Remarks { get; set; }
            }
            """],
            referenceTypes: vocabulary);

        var result = GeneratorHarness.Run(
            GeneratorName,
            ["namespace Fleet; public class Program { }"],
            referenceTypes: [.. vocabulary, typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "Fleet", outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication,
            additionalReferences: [library]);

        var generated = result.GeneratedSources.Should().ContainSingle().Which.Source;
        generated.Should().Contain("AbstractIndexCreationTask<global::Fleet.Library.Entities.Car>");
        generated.Should().Contain("ModelSearch = car.Model,");
    }

    /// <summary>
    /// #388: index classes live in the application only. A class library that sees <c>[GenerateIndex]</c>
    /// entities AND references RavenDB.Client used to get its own, never-deployed copy:
    /// CodeCoverage.Library (Raven through Authorization) and the forge integrations (through
    /// MintPlayer.Spark). Referencing Raven must not be enough.
    /// </summary>
    [Fact]
    public void A_class_library_that_references_Raven_gets_no_index_classes()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [EntityLibrarySource],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "Fleet.Library",
            outputKind: Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary);

        result.GeneratedSources.Where(s => s.Source.Contains("AbstractIndexCreationTask")).Should().BeEmpty();
    }

    [Fact]
    public void A_nested_entity_in_a_referenced_assembly_is_found()
    {
        var generated = RunWithLibrary(
            "namespace Fleet; public class Program { }",
            librarySource: """
            using MintPlayer.Spark.Abstractions;

            namespace Fleet.Library.Entities;

            public class Catalog
            {
                [GenerateIndex]
                public class Part
                {
                    public string? Code { get; set; }
                }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("Parts_Overview");
        generated.Should().Contain("global::Fleet.Library.Entities.Catalog.Part");
    }
}
