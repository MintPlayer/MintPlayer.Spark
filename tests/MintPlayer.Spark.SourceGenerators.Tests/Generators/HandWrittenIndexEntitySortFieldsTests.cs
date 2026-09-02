using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// The index entity always lives in the application project, so the generator can contribute a partial half
/// to it even when the developer keeps the index and the map hand-written. This covers that path.
/// </summary>
public class HandWrittenIndexEntitySortFieldsTests
{
    private const string GeneratorName = "GenerateIndexGenerator";

    private static GeneratorRunResult Run(string source)
        => GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "TestApp");

    /// <summary>
    /// Passes each fixture piece as its own source <em>file</em>. Concatenating them would put two file-scoped
    /// namespaces in one file, which is invalid C# and yields unusable symbols.
    /// </summary>
    private static GeneratorRunResult RunFiles(params string[] sources)
        => GeneratorHarness.Run(
            GeneratorName,
            sources,
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "TestApp");

    /// <summary>
    /// A stand-in for the RavenDB base class, so the fixture compiles without referencing RavenDB. The
    /// generator matches <c>[FromIndex]</c> on the index entity, not the index's base type.
    /// </summary>
    private const string IndexStub = """
        namespace TestApp.Indexes;

        public partial class Cars_Overview
        {
            protected void Index(string field, int indexing) { }
        }
        """;

    private static string SourceFor(string indexEntity) => $$"""
        using MintPlayer.Spark.Abstractions;
        using TestApp.Indexes;

        {{indexEntity}}
        """;

    [Fact]
    public void Contributes_a_companion_to_a_partial_index_entity()
    {
        var result = Run(SourceFor("""
            namespace TestApp.Data;

            [FromIndex(typeof(Cars_Overview))]
            public partial class VCar
            {
                [Search] public string? Model { get; set; }
            }
            """) + IndexStub);

        var file = result.GeneratedSources.Should().ContainSingle().Which;
        file.HintName.Should().Be("SparkIndexEntitySortFields.g.cs");
        file.Source.Should().Contain("namespace TestApp.Data");
        file.Source.Should().Contain("public partial class VCar");
        file.Source.Should().Contain("[global::MintPlayer.Spark.Abstractions.IgnorePropertyAttribute]");
        file.Source.Should().Contain("public string? ModelSort { get; set; }");
    }

    [Fact]
    public void Leaves_non_searchable_properties_alone()
    {
        var result = Run(SourceFor("""
            namespace TestApp.Data;

            [FromIndex(typeof(Cars_Overview))]
            public partial class VCar
            {
                public string? LicensePlate { get; set; }
                public int Year { get; set; }
            }
            """) + IndexStub);

        result.GeneratedSources.Should().BeEmpty();
    }

    /// <summary>
    /// A developer who already wrote the companion by hand must not get a duplicate member.
    /// </summary>
    [Fact]
    public void Does_not_duplicate_a_hand_written_companion()
    {
        var result = Run(SourceFor("""
            namespace TestApp.Data;

            [FromIndex(typeof(Cars_Overview))]
            public partial class VCar
            {
                [Search] public string? Model { get; set; }
                [IgnoreProperty] public string? ModelSort { get; set; }
            }
            """) + IndexStub);

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void A_non_partial_index_entity_is_an_error_not_a_silent_skip()
    {
        var result = Run(SourceFor("""
            namespace TestApp.Data;

            [FromIndex(typeof(Cars_Overview))]
            public class VCar
            {
                [Search] public string? Model { get; set; }
            }
            """) + IndexStub);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_001");
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Only_the_companion_is_contributed_not_the_whole_class()
    {
        var generated = Run(SourceFor("""
            namespace TestApp.Data;

            [FromIndex(typeof(Cars_Overview))]
            public partial class VCar
            {
                [Search] public string? Model { get; set; }
                public int Year { get; set; }
            }
            """) + IndexStub).GeneratedSources[0].Source;

        generated.Should().Contain("ModelSort");
        // The developer owns these; re-declaring them would be a duplicate-member error.
        generated.Should().NotContain("public int Year");
        generated.Should().NotContain("FromIndex");
    }

    /// <summary>
    /// A generated pair already carries its companions, so the hand-written path must not also contribute
    /// them to a hand-written partial half of the same index entity.
    /// </summary>
    [Fact]
    public void A_generated_index_entity_is_not_processed_twice()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                [Search] public string? Model { get; set; }
            }
            """);

        // Only the generated pair; no separate sort-fields file contributing a second ModelSort.
        result.GeneratedSources.Should().ContainSingle();
        result.GeneratedSources[0].HintName.Should().Be("SparkGeneratedIndexes.g.cs");
    }

    [Fact]
    public void Two_index_entities_in_one_namespace_share_a_namespace_block()
    {
        var generated = Run(SourceFor("""
            namespace TestApp.Data;

            [FromIndex(typeof(Cars_Overview))]
            public partial class VCar
            {
                [Search] public string? Model { get; set; }
            }

            [FromIndex(typeof(Cars_Overview))]
            public partial class VTruck
            {
                [Search] public string? Model { get; set; }
            }
            """) + IndexStub).GeneratedSources[0].Source;

        generated.Should().Contain("public partial class VCar");
        generated.Should().Contain("public partial class VTruck");
        CountOccurrences(generated, "namespace TestApp.Data").Should().Be(1);
    }

    /// <summary>
    /// A nested index entity must be reopened inside its containing types. Emitting it as a top-level class
    /// in the namespace would not compile.
    /// </summary>
    [Fact]
    public void A_nested_index_entity_is_reopened_inside_its_parents()
    {
        var generated = Run(SourceFor("""
            namespace TestApp.Data;

            public partial class Views
            {
                [FromIndex(typeof(Cars_Overview))]
                public partial class VCar
                {
                    [Search] public string? Model { get; set; }
                }
            }
            """) + IndexStub).GeneratedSources[0].Source;

        generated.Should().Contain("namespace TestApp.Data");
        generated.Should().Contain("partial class Views");
        generated.Should().Contain("public partial class VCar");
        generated.Should().Contain("ModelSort");
    }

    // --- generated Index(...) calls -----------------------------------------------------------

    /// <summary>
    /// Declaring <c>[Search]</c> on the index entity and then repeating it as an
    /// <c>Index(nameof(VCar.Model), FieldIndexing.Search)</c> line in the constructor says the same thing twice
    /// and lets the two drift. The attribute is the single declaration; the calls are generated from it into a
    /// method the hand-written constructor calls.
    /// </summary>
    [Fact]
    public void Generates_an_IndexSearchFields_method_on_the_index()
    {
        var generated = RunFiles("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Indexes;

            [FromIndex(typeof(Cars_Overview))]
            public partial class VCar
            {
                [Search] public string? Model { get; set; }
            }
            """, IndexStub).GeneratedSources[0].Source;

        generated.Should().Contain("private void IndexSearchFields()");
        generated.Should().Contain(
            "Index(nameof(global::TestApp.Indexes.VCar.Model), global::Raven.Client.Documents.Indexes.FieldIndexing.Search);");
    }

    /// <summary>
    /// The method lands on the INDEX class, which may sit in a different namespace than the index entity — so the
    /// <c>nameof</c> has to be fully qualified. Co-location is the convention, but a consumer need not follow it,
    /// and an unqualified name there is a CS0103 in generated code.
    /// </summary>
    [Fact]
    public void The_generated_nameof_is_qualified_across_namespaces()
    {
        var generated = RunFiles("""
            using MintPlayer.Spark.Abstractions;
            using TestApp.Indexes;

            namespace TestApp.Data;

            [FromIndex(typeof(Cars_Overview))]
            public partial class VCar
            {
                [Search] public string? Model { get; set; }
            }
            """, IndexStub).GeneratedSources[0].Source;

        generated.Should().Contain("nameof(global::TestApp.Data.VCar.Model)");
        generated.Should().NotContain("nameof(VCar.Model)");
    }

    [Fact]
    public void A_DateTimeOffset_field_is_indexed_Exact_by_the_generated_method()
    {
        var generated = RunFiles("""
            using System;
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Indexes;

            [FromIndex(typeof(Cars_Overview))]
            public partial class VCar
            {
                public DateTimeOffset CreatedOn { get; set; }
            }
            """, IndexStub).GeneratedSources[0].Source;

        generated.Should().Contain(
            "Index(nameof(global::TestApp.Indexes.VCar.CreatedOn), global::Raven.Client.Documents.Indexes.FieldIndexing.Exact);");
        generated.Should().Contain("CreatedOnSort");
    }

    /// <summary>
    /// Without <c>partial</c> on the index the method cannot be contributed, and the fields would silently be
    /// indexed with default options — searchable text that is not searchable.
    /// </summary>
    [Fact]
    public void A_non_partial_index_is_reported()
    {
        var result = RunFiles("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Indexes;

            [FromIndex(typeof(Cars_Overview))]
            public partial class VCar
            {
                [Search] public string? Model { get; set; }
            }
            """, """
            namespace TestApp.Indexes;

            public class Cars_Overview
            {
                protected void Index(string field, int indexing) { }
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_009");
    }

    [Fact]
    public void No_method_is_generated_when_nothing_is_searchable()
    {
        var result = RunFiles("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Indexes;

            [FromIndex(typeof(Cars_Overview))]
            public partial class VCar
            {
                public string? Model { get; set; }
            }
            """, IndexStub);

        result.GeneratedSources.Should().BeEmpty();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
