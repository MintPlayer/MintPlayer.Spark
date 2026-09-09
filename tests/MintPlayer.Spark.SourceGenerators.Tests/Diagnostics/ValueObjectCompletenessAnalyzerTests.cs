using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK017 — the completeness half of the value-object design: a type the model reaches as a
/// collection element, that nobody marked.
/// </summary>
/// <remarks>
/// The marker answers "is this a value object"; nothing in an entity library can answer "did you
/// forget one", because a library does not know which of its types the model reaches. Only the
/// application does, through its <c>SparkContext</c>.
/// </remarks>
public class ValueObjectCompletenessAnalyzerTests
{
    private const string AnalyzerName = "ValueObjectCompletenessAnalyzer";

    private static IEnumerable<Type> Refs =>
    [
        typeof(SparkContext),
        typeof(ValueObjectAttribute),
        typeof(ReferenceAttribute),
        typeof(Raven.Client.Documents.Linq.IRavenQueryable<>),
    ];

    private const string ContextAndPerson = """
        using System.Collections.Generic;
        using MintPlayer.Spark;
        using MintPlayer.Spark.Abstractions;
        using Raven.Client.Documents.Linq;

        namespace TestApp;

        public class AppContext : SparkContext
        {
            public IRavenQueryable<Person> People { get; set; } = null!;
        }

        public class Person
        {
            public string? Id { get; set; }
            public List<PhoneNumber> Phones { get; set; } = new();
        }
        """;

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(params string[] sources)
        => GeneratorHarness.RunAnalyzerAsync(AnalyzerName, sources, Refs);

    [Fact]
    public async Task An_unmarked_collection_element_is_reported()
    {
        var diagnostics = await RunAsync(ContextAndPerson, """
            namespace TestApp;

            public class PhoneNumber
            {
                public string Number { get; set; } = string.Empty;
            }
            """);

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK017");
    }

    /// <summary>
    /// The message carries the fully-qualified name because, for a type in a referenced assembly,
    /// it is all the developer gets — a `dotnet build` diagnostic there has no file and no line.
    /// </summary>
    [Fact]
    public async Task The_message_names_the_type()
    {
        var diagnostics = await RunAsync(ContextAndPerson, """
            namespace TestApp;

            public class PhoneNumber
            {
                public string Number { get; set; } = string.Empty;
            }
            """);

        diagnostics[0].GetMessage().Should().Contain("TestApp.PhoneNumber");
    }

    [Fact]
    public async Task A_marked_collection_element_is_accepted()
    {
        var diagnostics = await RunAsync(ContextAndPerson, """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp;

            [ValueObject]
            public partial class PhoneNumber
            {
                public string Number { get; set; } = string.Empty;
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// ⚠️ A key matches rows of a collection across a save. A single-valued member is matched by its
    /// property name, which cannot go missing or be reordered — so it needs none, and requiring one
    /// would flag seven types across the shipped apps that are never a row of anything.
    /// </summary>
    [Fact]
    public async Task A_single_nested_object_is_not_reported()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;

            namespace TestApp;

            public class AppContext : SparkContext
            {
                public IRavenQueryable<Build> Builds { get; set; } = null!;
            }

            public class Build
            {
                public string? Id { get; set; }
                public CoverageSummary Coverage { get; set; } = new();
            }

            public class CoverageSummary
            {
                public int Covered { get; set; }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// A collection two levels down is as much part of the model as one at the top, so a single
    /// nested object is walked even though it is never reported.
    /// </summary>
    [Fact]
    public async Task A_collection_below_a_single_nested_object_is_still_reported()
    {
        var diagnostics = await RunAsync("""
            using System.Collections.Generic;
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;

            namespace TestApp;

            public class AppContext : SparkContext
            {
                public IRavenQueryable<Build> Builds { get; set; } = null!;
            }

            public class Build
            {
                public string? Id { get; set; }
                public Settings Config { get; set; } = new();
            }

            public class Settings
            {
                public List<Threshold> Thresholds { get; set; } = new();
            }

            public class Threshold
            {
                public int Value { get; set; }
            }
            """);

        diagnostics.Should().ContainSingle()
            .Which.GetMessage().Should().Contain("TestApp.Threshold");
    }

    /// <summary>
    /// A <c>[Reference]</c> collection points at documents by id. Its target is a document in its
    /// own right, not an embedded row.
    /// </summary>
    [Fact]
    public async Task A_reference_collection_is_not_embedded()
    {
        var diagnostics = await RunAsync("""
            using System.Collections.Generic;
            using MintPlayer.Spark;
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Linq;

            namespace TestApp;

            public class AppContext : SparkContext
            {
                public IRavenQueryable<Person> People { get; set; } = null!;
            }

            public class Person
            {
                public string? Id { get; set; }
                [Reference(typeof(Company))] public List<string> Employers { get; set; } = new();
            }

            public class Company
            {
                public string? Id { get; set; }
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// ⚠️ A <c>Dictionary&lt;K,V&gt;</c> satisfies <c>IEnumerable&lt;KeyValuePair&lt;K,V&gt;&gt;</c>,
    /// so naive element extraction yields a BCL struct. The runtime does not model such a property
    /// as embedded either.
    /// </summary>
    [Fact]
    public async Task A_dictionary_is_not_a_collection_of_rows()
    {
        var diagnostics = await RunAsync("""
            using System.Collections.Generic;
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;

            namespace TestApp;

            public class AppContext : SparkContext
            {
                public IRavenQueryable<Person> People { get; set; } = null!;
            }

            public class Person
            {
                public string? Id { get; set; }
                public Dictionary<string, Detail> ByKey { get; set; } = new();
            }

            public class Detail
            {
                public string Value { get; set; } = string.Empty;
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// The roots are documents, not rows of anything.
    /// </summary>
    [Fact]
    public async Task A_document_root_is_never_reported()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark;
            using Raven.Client.Documents.Linq;

            namespace TestApp;

            public class AppContext : SparkContext
            {
                public IRavenQueryable<Person> People { get; set; } = null!;
            }

            public class Person
            {
                public string? Id { get; set; }
                public string Name { get; set; } = string.Empty;
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// An entity library has no context, so it cannot answer this question and must not guess.
    /// </summary>
    [Fact]
    public async Task A_compilation_with_no_context_says_nothing()
    {
        var diagnostics = await RunAsync("""
            using System.Collections.Generic;

            namespace TestApp;

            public class Person
            {
                public List<PhoneNumber> Phones { get; set; } = new();
            }

            public class PhoneNumber
            {
                public string Number { get; set; } = string.Empty;
            }
            """);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// A cycle in the type graph terminates rather than spinning.
    /// </summary>
    [Fact]
    public async Task A_cyclic_graph_terminates()
    {
        var diagnostics = await RunAsync("""
            using System.Collections.Generic;
            using MintPlayer.Spark;
            using MintPlayer.Spark.Abstractions;
            using Raven.Client.Documents.Linq;

            namespace TestApp;

            public class AppContext : SparkContext
            {
                public IRavenQueryable<Node> Nodes { get; set; } = null!;
            }

            public class Node
            {
                public string? Id { get; set; }
                public List<Branch> Branches { get; set; } = new();
            }

            [ValueObject]
            public partial class Branch
            {
                public List<Branch> Children { get; set; } = new();
            }
            """);

        diagnostics.Should().BeEmpty();
    }
}
