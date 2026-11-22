using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK063 (#700 D2b): an application whose entities have a stored-file property, but which does not
/// reference <c>MintPlayer.Spark.Files</c>. The types live in <c>MintPlayer.Spark.Model</c>, so the
/// entity compiles without the plugin; <c>UseSpark()</c> would refuse to start.
/// </summary>
public class FileAttributeRuntimeAnalyzerTests
{
    private const string AnalyzerName = "FileAttributeRuntimeAnalyzer";

    private static Task<IReadOnlyList<Diagnostic>> Analyze(string source, OutputKind kind = OutputKind.ConsoleApplication, params MetadataReference[] extra)
        => GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], [typeof(SparkFile)], additionalReferences: extra, outputKind: kind);

    /// <summary>An in-memory assembly with the given name, standing in for a package or an entity library.</summary>
    private static MetadataReference Assembly(string name, string source = "namespace Stub { public class Marker { } }")
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Collections.dll")),
                MetadataReference.CreateFromFile(typeof(SparkFile).Assembly.Location),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        result.Success.Should().BeTrue(string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private const string PhotoApp = """
        using System.Collections.Generic;
        using MintPlayer.Spark.Abstractions;

        namespace Acme;

        public class Car
        {
            public string? Id { get; set; }
            public List<SparkImage> Photos { get; set; } = new();
        }

        public static class Program { public static void Main() { } }
        """;

    [Fact]
    public async Task A_file_property_without_Files_is_reported_naming_it()
    {
        var diagnostics = await Analyze(PhotoApp);

        var warning = diagnostics.Should().ContainSingle(d => d.Id == "SPARK063").Which;
        warning.Severity.Should().Be(DiagnosticSeverity.Warning);
        warning.GetMessage().Should().Contain("'Car.Photos'").And.Contain("SparkImage").And.Contain("AddFiles()");
    }

    [Fact]
    public async Task A_file_property_with_Files_referenced_is_not_reported()
    {
        var diagnostics = await Analyze(PhotoApp, OutputKind.ConsoleApplication, Assembly("MintPlayer.Spark.Files"));

        diagnostics.Should().NotContain(d => d.Id == "SPARK063");
    }

    [Fact]
    public async Task A_library_is_not_checked()
    {
        var diagnostics = await Analyze(PhotoApp, OutputKind.DynamicallyLinkedLibrary);

        diagnostics.Should().NotContain(d => d.Id == "SPARK063");
    }

    [Fact]
    public async Task A_single_SparkFile_property_is_reported()
    {
        var diagnostics = await Analyze("""
            using MintPlayer.Spark.Abstractions;

            namespace Acme;

            public class Invoice { public SparkFile? Scan { get; set; } }

            public static class Program { public static void Main() { } }
            """);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK063").Which.GetMessage().Should().Contain("'Invoice.Scan'");
    }

    /// <summary>Entities usually live in a separate library; the host is where the plugin must be referenced.</summary>
    [Fact]
    public async Task A_file_property_in_a_referenced_entity_library_is_reported_in_the_host()
    {
        var entities = Assembly("Acme.Entities", """
            using MintPlayer.Spark.Abstractions;
            namespace Acme.Entities;
            public class Invoice { public SparkFile? Scan { get; set; } }
            """);

        var diagnostics = await Analyze("public static class Program { public static void Main() { } }", OutputKind.ConsoleApplication, entities);

        diagnostics.Should().ContainSingle(d => d.Id == "SPARK063").Which.GetMessage().Should().Contain("'Invoice.Scan'");
    }

    [Fact]
    public async Task A_url_string_and_no_file_type_is_not_reported()
    {
        var diagnostics = await Analyze("""
            namespace Acme;

            public class Account { public string? AvatarUrl { get; set; } }

            public static class Program { public static void Main() { } }
            """);

        diagnostics.Should().BeEmpty();
    }
}
