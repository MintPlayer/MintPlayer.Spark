using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// The argument kinds <c>AttributeRenderer</c> copies onto a generated index-entity property. Reached
/// through <c>GenerateIndexGenerator</c>, because the renderer is internal to an assembly the tests
/// load rather than reference.
/// </summary>
/// <remarks>
/// Asserting on text alone is not enough here: <c>(E)-1</c> <em>looks</em> right and is a CS0119 in the
/// generated file. So each test also compiles the output and requires the generated file to be free of
/// errors.
/// </remarks>
public class AttributeRendererTests
{
    private const string Fixture = """
        using System;
        using MintPlayer.Spark.Abstractions;

        namespace TestApp.Entities;

        public enum Level { Below = -1, None = 0, High = 2 }

        [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
        public sealed class ProbeAttribute : Attribute
        {
            public ProbeAttribute() { }
            public ProbeAttribute(Level level) { }
            public ProbeAttribute(double value) { }
            public ProbeAttribute(float value) { }
            public ProbeAttribute(bool flag, char symbol, long count, string text) { }
            public ProbeAttribute(int[] values) { }
            public ProbeAttribute(object? anything) { }
            public Level Named { get; set; }
        }

        [GenerateIndex]
        public class Car
        {
            public string? Id { get; set; }
            {{PROPERTY}}
        }
        """;

    private static (string Generated, IReadOnlyList<Diagnostic> Errors) Render(string attribute)
    {
        var result = GeneratorHarness.Run(
            "GenerateIndexGenerator",
            [Fixture.Replace("{{PROPERTY}}", $"{attribute} public string? Model {{ get; set; }}")],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "TestApp", outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication);

        var (hintName, generated) = result.GeneratedSources.Single();
        var errors = result.FinalCompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error
                        && d.Location.SourceTree?.FilePath.EndsWith(hintName, StringComparison.Ordinal) == true)
            .ToList();
        return (generated, errors);
    }

    [Fact]
    public void A_negative_enum_value_is_parenthesized_so_the_cast_compiles()
    {
        var (generated, errors) = Render("[Probe(Level.Below)]");

        generated.Should().Contain("[global::TestApp.Entities.ProbeAttribute((global::TestApp.Entities.Level)(-1))]");
        errors.Should().BeEmpty("`(E)-1` parses as a subtraction from a type, which is CS0119");
    }

    [Fact]
    public void A_negative_enum_named_argument_is_parenthesized_too()
    {
        var (generated, errors) = Render("[Probe(Named = Level.Below)]");

        generated.Should().Contain("Named = (global::TestApp.Entities.Level)(-1)");
        errors.Should().BeEmpty();
    }

    [Fact]
    public void A_non_negative_enum_value_keeps_the_bare_cast()
    {
        var (generated, errors) = Render("[Probe(Level.High)]");

        generated.Should().Contain("[global::TestApp.Entities.ProbeAttribute((global::TestApp.Entities.Level)2)]");
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("double.NaN", "double.NaN")]
    [InlineData("double.PositiveInfinity", "double.PositiveInfinity")]
    [InlineData("double.NegativeInfinity", "double.NegativeInfinity")]
    [InlineData("float.NaN", "float.NaN")]
    [InlineData("float.PositiveInfinity", "float.PositiveInfinity")]
    [InlineData("float.NegativeInfinity", "float.NegativeInfinity")]
    [InlineData("1.5", "1.5")]
    [InlineData("2.5f", "2.5f")]
    public void Floating_point_values_render_as_compilable_source(string argument, string expected)
    {
        var (generated, errors) = Render($"[Probe({argument})]");

        generated.Should().Contain($"[global::TestApp.Entities.ProbeAttribute({expected})]");
        errors.Should().BeEmpty("`NaN` and `Infinity` are not C# literals");
    }

    [Fact]
    public void Primitive_arguments_render_with_their_escapes()
    {
        var (generated, errors) = Render("""[Probe(true, '\'', 9000000000L, "a\"b\\c\r\n\t")]""");

        generated.Should().Contain("""[global::TestApp.Entities.ProbeAttribute(true, '\'', 9000000000, "a\"b\\c\r\n\t")]""");
        errors.Should().BeEmpty();
    }

    [Fact]
    public void A_backslash_char_is_escaped()
    {
        var (generated, errors) = Render("""[Probe(false, '\\', 0L, "")]""");

        generated.Should().Contain("""(false, '\\', 0, "")""");
        errors.Should().BeEmpty();
    }

    [Fact]
    public void An_array_argument_renders_as_an_array_creation()
    {
        var (generated, errors) = Render("[Probe(new[] { 1, 2, 3 })]");

        generated.Should().Contain("[global::TestApp.Entities.ProbeAttribute(new int[] { 1, 2, 3 })]");
        errors.Should().BeEmpty();
    }

    [Fact]
    public void A_null_argument_renders_as_null()
    {
        var (generated, errors) = Render("[Probe((object?)null)]");

        generated.Should().Contain("[global::TestApp.Entities.ProbeAttribute(null)]");
        errors.Should().BeEmpty();
    }

    [Fact]
    public void A_parameterless_attribute_renders_without_parentheses()
    {
        var (generated, errors) = Render("[Probe]");

        generated.Should().Contain("[global::TestApp.Entities.ProbeAttribute]");
        errors.Should().BeEmpty();
    }
}
