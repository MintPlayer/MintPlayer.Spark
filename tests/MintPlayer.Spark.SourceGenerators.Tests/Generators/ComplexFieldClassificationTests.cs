using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// #273 Layer 1: a complex-typed property (nested object, collection of non-scalars, dictionary,
/// user struct) maps verbatim but must be declared <c>FieldIndexing.No</c> — Corax faults per
/// document on a complex field with default indexing, so the index silently ends up empty. The
/// field stays stored and projectable, so AsDetail columns keep rendering.
/// </summary>
public class ComplexFieldClassificationTests
{
    private const string GeneratorName = "GenerateIndexGenerator";

    private static GeneratorRunResult Run(string source)
        => GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(System.Drawing.Color)],
            rootNamespace: "TestApp");

    private const string PersonWithAddress = """
        using MintPlayer.Spark.Abstractions;

        namespace TestApp.Entities;

        public class Address
        {
            public string City { get; set; } = string.Empty;
            public string Street { get; set; } = string.Empty;
        }

        [GenerateIndex]
        public class Person
        {
            public string? Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public Address? Address { get; set; }
        }
        """;

    [Fact]
    public void Nested_object_is_mapped_stored_and_declared_FieldIndexing_No()
    {
        var generated = Run(PersonWithAddress).GeneratedSources[0].Source;

        generated.Should().Contain("Address = person.Address,");
        generated.Should().Contain("Index(nameof(VPerson.Address), global::Raven.Client.Documents.Indexes.FieldIndexing.No);");
        generated.Should().NotContain("AddressSort");
    }

    [Fact]
    public void Complex_property_without_breadcrumb_reports_SPARK_INDEX_010()
    {
        var result = Run(PersonWithAddress);

        result.GeneratorDiagnostics.Should().Contain(d =>
            d.Id == "SPARK_INDEX_010" && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Collection_of_complex_elements_is_declared_FieldIndexing_No()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;
            using System.Collections.Generic;

            namespace TestApp.Entities;

            public class Job
            {
                public string Title { get; set; } = string.Empty;
            }

            [GenerateIndex]
            public class Person
            {
                public string? Id { get; set; }
                public string Name { get; set; } = string.Empty;
                public List<Job> Jobs { get; set; } = new();
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("Jobs = person.Jobs,");
        generated.Should().Contain("Index(nameof(VPerson.Jobs), global::Raven.Client.Documents.Indexes.FieldIndexing.No);");
    }

    [Fact]
    public void Collection_of_strings_stays_search_eligible_and_is_not_inerted()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;
            using System.Collections.Generic;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Person
            {
                public string? Id { get; set; }
                [Search] public List<string> Nicknames { get; set; } = new();
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("Index(nameof(VPerson.Nicknames), global::Raven.Client.Documents.Indexes.FieldIndexing.Search);");
        generated.Should().NotContain("FieldIndexing.No");
    }

    /// <summary>
    /// The trap the runtime classifier falls into: a user-defined struct is a value type, but it
    /// persists as a JSON object and faults Corax exactly like a class.
    /// </summary>
    [Fact]
    public void User_defined_struct_is_declared_FieldIndexing_No()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public struct GeoPoint
            {
                public double Lat { get; set; }
                public double Lng { get; set; }
            }

            [GenerateIndex]
            public class Site
            {
                public string? Id { get; set; }
                public GeoPoint Location { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("Index(nameof(VSite.Location), global::Raven.Client.Documents.Indexes.FieldIndexing.No);");
    }

    [Fact]
    public void Dictionary_is_declared_FieldIndexing_No()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;
            using System.Collections.Generic;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Site
            {
                public string? Id { get; set; }
                public Dictionary<string, string> Tags { get; set; } = new();
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("Index(nameof(VSite.Tags), global::Raven.Client.Documents.Indexes.FieldIndexing.No);");
    }

    /// <summary>
    /// <c>System.Drawing.Color</c> is a struct with R/G/B properties, but Spark persists it as an
    /// <c>"#rrggbb"</c> string (<c>ColorNewtonsoftJsonConverter</c>) — indexing it verbatim works.
    /// Classifying it complex would regress Fleet's working Color columns.
    /// </summary>
    [Fact]
    public void Color_stays_scalar_and_is_not_inerted()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public string? Id { get; set; }
                public System.Drawing.Color? Color { get; set; }
            }
            """);

        var generated = result.GeneratedSources[0].Source;
        generated.Should().Contain("Color = car.Color,");
        generated.Should().NotContain("FieldIndexing.No");
        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK_INDEX_010");
    }

    [Fact]
    public void Search_on_complex_still_reports_005_and_the_field_is_inerted()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public class Address
            {
                public string City { get; set; } = string.Empty;
            }

            [GenerateIndex]
            public class Person
            {
                public string? Id { get; set; }
                [Search] public Address? Address { get; set; }
            }
            """);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_005");
        var generated = result.GeneratedSources[0].Source;
        generated.Should().Contain("Index(nameof(VPerson.Address), global::Raven.Client.Documents.Indexes.FieldIndexing.No);");
        generated.Should().NotContain("FieldIndexing.Search");
        generated.Should().NotContain("AddressSort");
    }

    [Fact]
    public void Classification_works_for_referenced_assembly_entities()
    {
        var library = GeneratorHarness.CompileToMetadataReference(
            "Fleet.Library",
            [PersonWithAddress],
            referenceTypes: [typeof(GenerateIndexAttribute)]);

        var result = GeneratorHarness.Run(
            GeneratorName,
            ["namespace TestApp; public class Program { }"],
            referenceTypes: [typeof(GenerateIndexAttribute)],
            rootNamespace: "TestApp",
            additionalReferences: [library]);

        var generated = result.GeneratedSources[0].Source;
        generated.Should().Contain("Index(nameof(VPerson.Address), global::Raven.Client.Documents.Indexes.FieldIndexing.No);");
        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_010");
    }
}
