using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// The generator that gives an entity with an <c>Id&lt;Self&gt;</c> its hidden <c>__DocumentId</c> member, RavenDB's
/// string identity (#536 D23 rule 6).
/// </summary>
public class DocumentIdGeneratorTests
{
    private static GeneratorRunResult Run(params string[] sources)
        => GeneratorHarness.Run(
            "DocumentIdGenerator",
            sources,
            referenceTypes: [typeof(Id<>)],
            rootNamespace: "TestApp",
            generatorAssemblyName: "MintPlayer.Spark.LibraryGenerators");

    private static IEnumerable<Diagnostic> Errors(GeneratorRunResult result)
        => result.GeneratorDiagnostics
            .Concat(result.FinalCompilationDiagnostics)
            .Where(d => d.Severity == DiagnosticSeverity.Error);

    private static string Combined(GeneratorRunResult result)
        => string.Join("\n", result.GeneratedSources.Select(s => s.Source));

    [Fact]
    public void A_partial_entity_gets_a_private_string_identity_that_mirrors_its_id()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public partial class Person
            {
                public Id<Person> Id { get; set; }
                public Id<Company>? CompanyId { get; set; }
            }

            public partial class Company
            {
                public Id<Company> Id { get; set; }
            }
            """);

        Errors(result).Should().BeEmpty();
        var combined = Combined(result);
        combined.Should().Contain("partial class Person");
        combined.Should().Contain("internal string? __DocumentId { get => Id.IsEmpty ? null : Id.Value; set => Id = global::MintPlayer.Spark.Abstractions.Id<global::TestApp.Person>.FromTrusted(value); }");
        combined.Should().Contain("partial class Company");
    }

    [Fact]
    public void A_nested_entity_reopens_its_containing_types()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public static partial class Outer
            {
                public partial class Inner
                {
                    public Id<Inner> Id { get; set; }
                }
            }
            """);

        Errors(result).Should().BeEmpty();
        Combined(result).Should().Contain("__DocumentId");
    }

    [Fact]
    public void A_string_id_or_another_entitys_id_gets_nothing()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public class Plain { public string? Id { get; set; } }
            public class Company { public string? Id { get; set; } }
            public class Link { public Id<Company> Id { get; set; } }
            """);

        Errors(result).Should().BeEmpty();
        Combined(result).Should().NotContain("__DocumentId");
    }

    [Fact]
    public void A_non_partial_entity_is_reported()
    {
        var result = Run("""
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            public class Person
            {
                public Id<Person> Id { get; set; }
            }
            """);

        result.GeneratorDiagnostics.Should().ContainSingle(d => d.Id == "SPARK064");
        Combined(result).Should().NotContain("__DocumentId");
    }
}
