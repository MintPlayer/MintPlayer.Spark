using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

public class GenerateIndexGeneratorTests
{
    private const string GeneratorName = "GenerateIndexGenerator";

    /// <summary>
    /// The generated code references RavenDB types this test project does not reference, so these tests
    /// assert on emitted text rather than on a clean final compilation. Real-build fidelity is covered
    /// separately by a demo app that must actually compile against the generated output.
    /// </summary>
    private static GeneratorRunResult Run(string source, string rootNamespace = "TestApp")
        => GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(GenerateIndexAttribute)],
            rootNamespace: rootNamespace);

    private const string PlainCar = """
        using MintPlayer.Spark.Abstractions;

        namespace TestApp.Entities;

        [GenerateIndex]
        public class Car
        {
            public string? Id { get; set; }
            public string LicensePlate { get; set; } = string.Empty;
            public int Year { get; set; }
        }
        """;

    [Fact]
    public void Emits_an_index_and_an_index_entity()
    {
        var result = Run(PlainCar);

        result.GeneratedSources.Should().ContainSingle();
        var (hintName, generated) = result.GeneratedSources[0];

        hintName.Should().Be("SparkGeneratedIndexes.g.cs");
        generated.Should().Contain("public partial class VCar");
        generated.Should().Contain("public partial class Cars_Overview : global::Raven.Client.Documents.Indexes.AbstractIndexCreationTask<global::TestApp.Entities.Car>");
    }

    [Fact]
    public void Index_entity_is_linked_to_the_index_by_FromIndex()
    {
        var generated = Run(PlainCar).GeneratedSources[0].Source;

        generated.Should().Contain("[global::MintPlayer.Spark.Abstractions.FromIndex(typeof(Cars_Overview))]");
    }

    /// <summary>
    /// Both generated types belong to the application project, never to the assembly that declares the
    /// entity — that is what lets an entity library stay lean.
    /// </summary>
    [Fact]
    public void Generated_types_land_in_the_app_namespace_not_the_entity_namespace()
    {
        var generated = Run(PlainCar, rootNamespace: "MyApp").GeneratedSources[0].Source;

        generated.Should().Contain("namespace MyApp.Indexes");
        generated.Should().NotContain("namespace TestApp.Entities");
    }

    [Fact]
    public void Map_projects_every_indexable_property()
    {
        var generated = Run(PlainCar).GeneratedSources[0].Source;

        generated.Should().Contain("Map = cars => from car in cars");
        generated.Should().Contain("select new VCar()");
        generated.Should().Contain("LicensePlate = car.LicensePlate,");
        generated.Should().Contain("Year = car.Year,");
    }

    /// <summary>
    /// Without this a projection-only field returns null through <c>ProjectInto</c> while the index is
    /// provably correct — no error, no index fault. Measured; it is the likeliest way a generated index
    /// appears broken, so it gets its own test.
    /// </summary>
    [Fact]
    public void StoreAllFields_is_always_emitted()
    {
        var generated = Run(PlainCar).GeneratedSources[0].Source;

        generated.Should().Contain("StoreAllFields(global::Raven.Client.Documents.Indexes.FieldStorage.Yes);");
    }

    [Fact]
    public void Id_is_declared_on_the_index_entity_but_never_mapped()
    {
        var generated = Run(PlainCar).GeneratedSources[0].Source;

        generated.Should().Contain("public string? Id { get; set; }");
        generated.Should().NotContain("Id = car.Id");
    }

    [Fact]
    public void Emits_the_OnInitialize_extension_seam()
    {
        var generated = Run(PlainCar).GeneratedSources[0].Source;

        generated.Should().Contain("OnInitialize();");
        generated.Should().Contain("partial void OnInitialize();");
    }

    [Fact]
    public void No_source_without_the_Spark_reference()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            ["namespace TestApp; public class Foo { }"],
            referenceTypes: Array.Empty<Type>(),
            rootNamespace: "TestApp");

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void No_source_for_an_entity_without_the_attribute()
    {
        var result = Run("""
            namespace TestApp.Entities;

            public class Car
            {
                public string LicensePlate { get; set; } = string.Empty;
            }
            """);

        result.GeneratedSources.Should().BeEmpty();
    }

    // --- property selection -------------------------------------------------------------------

    [Fact]
    public void IgnoreProperty_and_IgnoreForIndex_are_both_excluded()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public string LicensePlate { get; set; } = string.Empty;
                [IgnoreProperty] public string? SyncEtag { get; set; }
                [IgnoreForIndex] public string? Notes { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("LicensePlate");
        generated.Should().NotContain("SyncEtag");
        generated.Should().NotContain("Notes");
    }

    /// <summary>
    /// Discovering only declared members silently drops inherited properties — a documented defect of the
    /// design this replaces, so the hierarchy walk gets an explicit test.
    /// </summary>
    [Fact]
    public void Inherited_properties_are_included()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public abstract class AuditedEntity
            {
                public string? CreatedBy { get; set; }
            }

            [GenerateIndex]
            public class Car : AuditedEntity
            {
                public string LicensePlate { get; set; } = string.Empty;
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("CreatedBy = car.CreatedBy,");
        generated.Should().Contain("LicensePlate = car.LicensePlate,");
    }

    [Fact]
    public void Indexers_and_write_only_properties_are_excluded()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public string LicensePlate { get; set; } = string.Empty;
                public string this[int i] { get => string.Empty; set { } }
                public string WriteOnly { set { } }
                private string Hidden { get; set; } = string.Empty;
                public static string Statik { get; set; } = string.Empty;
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("LicensePlate");
        generated.Should().NotContain("WriteOnly");
        generated.Should().NotContain("Hidden");
        generated.Should().NotContain("Statik");
        generated.Should().NotContain("this[");
    }

    /// <summary>
    /// A non-nullable reference type needs <c>= default!</c> or the generated file warns CS8618 in a
    /// nullable-enabled compilation.
    /// </summary>
    [Fact]
    public void Nullability_is_preserved_and_non_nullable_references_get_an_initializer()
    {
        var generated = Run(PlainCar).GeneratedSources[0].Source;

        generated.Should().Contain("public string LicensePlate { get; set; } = default!;");
        generated.Should().Contain("public int Year { get; set; }");
        generated.Should().NotContain("public int Year { get; set; } = default!;");
    }

    // --- naming overrides ---------------------------------------------------------------------

    [Fact]
    public void IndexName_and_IndexEntityName_can_be_overridden()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex(IndexName = "Vehicles_Search", IndexEntityName = "VehicleView")]
            public class Car
            {
                public string LicensePlate { get; set; } = string.Empty;
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("public partial class VehicleView");
        generated.Should().Contain("public partial class Vehicles_Search :");
        generated.Should().Contain("[global::MintPlayer.Spark.Abstractions.FromIndex(typeof(Vehicles_Search))]");
        generated.Should().NotContain("Cars_Overview");
    }

    [Fact]
    public void Description_is_emitted_on_the_index()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex(Description = "Cars for the overview grid")]
            public class Car
            {
                public string LicensePlate { get; set; } = string.Empty;
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("[global::System.ComponentModel.Description(\"Cars for the overview grid\")]");
    }

    /// <summary>
    /// The index name is the RavenDB index name, and renaming one re-indexes the database — so
    /// pluralization is pinned by tests rather than left to a general-purpose inflector.
    /// </summary>
    [Theory]
    [InlineData("Car", "Cars_Overview")]
    [InlineData("Person", "People_Overview")]
    [InlineData("Company", "Companies_Overview")]
    [InlineData("Address", "Addresses_Overview")]
    [InlineData("Child", "Children_Overview")]
    [InlineData("Day", "Days_Overview")]
    public void Index_names_are_pluralized_predictably(string entityName, string expectedIndexName)
    {
        var generated = Run($$"""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class {{entityName}}
            {
                public string Name { get; set; } = string.Empty;
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain($"public partial class {expectedIndexName} :");
    }

    // --- diagnostics --------------------------------------------------------------------------

    /// <summary>
    /// Every abort path reports a diagnostic. <c>Producer.Produce</c> discards exceptions, so a silent
    /// abort would emit nothing and leave a runtime auto-index in its place — the exact problem
    /// <c>[GenerateIndex]</c> exists to prevent.
    /// </summary>
    [Fact]
    public void Entity_with_no_indexable_properties_warns_and_emits_nothing()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public string? Id { get; set; }
                [IgnoreProperty] public string? SyncEtag { get; set; }
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_003");
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Two_entities_generating_the_same_index_name_report_a_duplicate()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex(IndexName = "Shared_Overview")]
            public class Car
            {
                public string Name { get; set; } = string.Empty;
            }

            [GenerateIndex(IndexName = "Shared_Overview")]
            public class Truck
            {
                public string Name { get; set; } = string.Empty;
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_002");
    }

    /// <summary>
    /// The duplicate is dropped rather than emitted twice, which would be a compile error in the
    /// generated file instead of a targeted diagnostic.
    /// </summary>
    [Fact]
    public void A_duplicate_index_name_is_emitted_only_once()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex(IndexName = "Shared_Overview")]
            public class Car
            {
                public string Name { get; set; } = string.Empty;
            }

            [GenerateIndex(IndexName = "Shared_Overview")]
            public class Truck
            {
                public string Name { get; set; } = string.Empty;
            }
            """).GeneratedSources[0].Source;

        CountOccurrences(generated, "public partial class Shared_Overview :").Should().Be(1);
    }

    [Fact]
    public void Multiple_entities_are_emitted_into_one_file()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public string Name { get; set; } = string.Empty;
            }

            [GenerateIndex]
            public class Truck
            {
                public string Name { get; set; } = string.Empty;
            }
            """);

        result.GeneratedSources.Should().ContainSingle();
        var generated = result.GeneratedSources[0].Source;
        generated.Should().Contain("public partial class Cars_Overview :");
        generated.Should().Contain("public partial class Trucks_Overview :");
        generated.Should().Contain("public partial class VCar");
        generated.Should().Contain("public partial class VTruck");
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
