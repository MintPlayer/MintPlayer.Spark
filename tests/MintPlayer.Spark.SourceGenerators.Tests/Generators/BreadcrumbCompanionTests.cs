using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// #273 Layer 2: a singular complex property whose type carries a property-level
/// <c>[Breadcrumb]</c> marker gets a <c>{Name}Sort</c> companion mapped to the marked property's
/// <em>persisted</em> value — one member-access hop per level, no template rendering. The base
/// field stays stored + <c>FieldIndexing.No</c>; the companion is undeclared (sortable) and
/// <c>[IgnoreProperty]</c>, so <c>QueryExecutor.ResolveSortProperty</c> redirects to it with zero
/// runtime changes.
/// </summary>
public class BreadcrumbCompanionTests
{
    private const string GeneratorName = "GenerateIndexGenerator";

    private static GeneratorRunResult Run(string source)
        => GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(GenerateIndexAttribute)],
            rootNamespace: "TestApp");

    private const string PersonWithMarkedAddress = """
        using MintPlayer.Spark.Abstractions;

        namespace TestApp.Entities;

        public class Address
        {
            [Breadcrumb] public string City { get; set; } = string.Empty;
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
    public void Marked_scalar_on_the_complex_type_produces_a_sort_companion()
    {
        var result = Run(PersonWithMarkedAddress);
        var generated = result.GeneratedSources[0].Source;

        // Base field: still mapped, stored, inert.
        generated.Should().Contain("Address = person.Address,");
        generated.Should().Contain("Index(nameof(VPerson.Address), global::Raven.Client.Documents.Indexes.FieldIndexing.No);");

        // Companion: mapped down the persisted path, undeclared (= sortable), [IgnoreProperty].
        generated.Should().Contain("AddressSort = person.Address!.City,");
        generated.Should().NotContain("Index(nameof(VPerson.AddressSort)");

        // The column is no longer inert, so the stored-not-indexed warning must not fire.
        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK_INDEX_010");
    }

    /// <summary>
    /// The sanctioned combination: a computed get-only property marked [Breadcrumb] and hidden from
    /// the model with [IgnoreProperty]. It persists like any property (Newtonsoft serializes
    /// readable members), so the companion reads it — the lookup must be a raw member scan, not the
    /// model/index filter that [IgnoreProperty] would fail.
    /// </summary>
    [Fact]
    public void Marked_computed_property_with_IgnoreProperty_is_honored()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public class Address
            {
                public string City { get; set; } = string.Empty;
                public string Street { get; set; } = string.Empty;
                [Breadcrumb, IgnoreProperty] public string Crumb => $"{Street}, {City}";
            }

            [GenerateIndex]
            public class Person
            {
                public string? Id { get; set; }
                public string Name { get; set; } = string.Empty;
                public Address? Address { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("AddressSort = person.Address!.Crumb,");
    }

    /// <summary>
    /// Multi-level: a marker on a complex-typed property delegates to that type's own declaration,
    /// mirroring how a computed chain would compose. Every level must declare explicitly.
    /// </summary>
    [Fact]
    public void Marker_on_a_complex_property_delegates_to_that_types_own_marker()
    {
        var generated = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public class AddressDescription
            {
                [Breadcrumb] public string Title { get; set; } = string.Empty;
            }

            public class Address
            {
                public string City { get; set; } = string.Empty;
                [Breadcrumb] public AddressDescription? Description { get; set; }
            }

            [GenerateIndex]
            public class Person
            {
                public string? Id { get; set; }
                public string Name { get; set; } = string.Empty;
                public Address? Address { get; set; }
            }
            """).GeneratedSources[0].Source;

        generated.Should().Contain("AddressSort = person.Address!.Description!.Title,");
    }

    [Fact]
    public void No_marker_anywhere_means_no_companion_and_SPARK_INDEX_010()
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
                public string Name { get; set; } = string.Empty;
                public Address? Address { get; set; }
            }
            """);

        result.GeneratedSources[0].Source.Should().NotContain("AddressSort");
        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_010");
    }

    [Fact]
    public void Two_marked_properties_use_the_ordinal_min_and_report_011()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public class Address
            {
                [Breadcrumb] public string Street { get; set; } = string.Empty;
                [Breadcrumb] public string City { get; set; } = string.Empty;
            }

            [GenerateIndex]
            public class Person
            {
                public string? Id { get; set; }
                public string Name { get; set; } = string.Empty;
                public Address? Address { get; set; }
            }
            """);

        result.GeneratedSources[0].Source.Should().Contain("AddressSort = person.Address!.City,");
        result.GeneratorDiagnostics.Should().Contain(d =>
            d.Id == "SPARK_INDEX_011" && d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Marker_on_a_Reference_id_is_rejected_with_012_and_the_field_stays_inert()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public class Job
            {
                [Breadcrumb, Reference(typeof(Person))] public string? ProfessionId { get; set; }
            }

            [GenerateIndex]
            public class Person
            {
                public string? Id { get; set; }
                public string Name { get; set; } = string.Empty;
                public Job? CurrentJob { get; set; }
            }
            """);

        result.GeneratedSources[0].Source.Should().NotContain("CurrentJobSort");
        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_012");
    }

    [Fact]
    public void Marker_on_a_collection_property_is_rejected_with_012()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;
            using System.Collections.Generic;

            namespace TestApp.Entities;

            public class Address
            {
                [Breadcrumb] public List<string> Lines { get; set; } = new();
            }

            [GenerateIndex]
            public class Person
            {
                public string? Id { get; set; }
                public string Name { get; set; } = string.Empty;
                public Address? Address { get; set; }
            }
            """);

        result.GeneratedSources[0].Source.Should().NotContain("AddressSort");
        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_012");
    }

    [Fact]
    public void Existing_entity_property_named_like_the_companion_skips_it_and_reports_012()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public class Address
            {
                [Breadcrumb] public string City { get; set; } = string.Empty;
            }

            [GenerateIndex]
            public class Person
            {
                public string? Id { get; set; }
                public string Name { get; set; } = string.Empty;
                public Address? Address { get; set; }
                public string AddressSort { get; set; } = string.Empty;
            }
            """);

        // The entity's own AddressSort maps as itself; the breadcrumb companion must not be emitted.
        result.GeneratedSources[0].Source.Should().Contain("AddressSort = person.AddressSort,");
        result.GeneratedSources[0].Source.Should().NotContain("AddressSort = person.Address!.City");
        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_012");
    }

    [Fact]
    public void Collection_of_complex_never_gets_a_companion_even_when_the_element_is_marked()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;
            using System.Collections.Generic;

            namespace TestApp.Entities;

            public class Job
            {
                [Breadcrumb] public string Title { get; set; } = string.Empty;
            }

            [GenerateIndex]
            public class Person
            {
                public string? Id { get; set; }
                public string Name { get; set; } = string.Empty;
                public List<Job> Jobs { get; set; } = new();
            }
            """);

        result.GeneratedSources[0].Source.Should().NotContain("JobsSort");
        result.GeneratedSources[0].Source.Should().Contain("Index(nameof(VPerson.Jobs), global::Raven.Client.Documents.Indexes.FieldIndexing.No);");
    }

    [Fact]
    public void Companion_works_for_referenced_assembly_entities()
    {
        var library = GeneratorHarness.CompileToMetadataReference(
            "Fleet.Library",
            [PersonWithMarkedAddress],
            referenceTypes: [typeof(GenerateIndexAttribute)]);

        var result = GeneratorHarness.Run(
            GeneratorName,
            ["namespace TestApp; public class Program { }"],
            referenceTypes: [typeof(GenerateIndexAttribute)],
            rootNamespace: "TestApp",
            additionalReferences: [library]);

        result.GeneratedSources[0].Source.Should().Contain("AddressSort = person.Address!.City,");
    }

    [Fact]
    public void Self_referencing_marked_chain_terminates_with_012()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            public class Node
            {
                [Breadcrumb] public Node? Parent { get; set; }
            }

            [GenerateIndex]
            public class Person
            {
                public string? Id { get; set; }
                public string Name { get; set; } = string.Empty;
                public Node? Tree { get; set; }
            }
            """);

        result.GeneratedSources[0].Source.Should().NotContain("TreeSort");
        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_INDEX_012");
    }
}
