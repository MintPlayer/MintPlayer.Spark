using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// #348 — <c>///</c> summaries on entity properties become <c>[assembly: SparkAttributeDescription]</c>
/// lines the synchronizer reads back. Every rendering fact is pinned twice: once with structured
/// documentation (<c>GenerateDocumentationFile</c> on, the harness default) and once with
/// <see cref="DocumentationMode.None"/>, which is what every project in this repository compiles
/// with today and where <c>///</c> is plain comment trivia.
/// </summary>
public class AttributeDescriptionsGeneratorTests
{
    private const string GeneratorName = "AttributeDescriptionsGenerator";

    private static readonly CSharpParseOptions NoDocs =
        CSharpParseOptions.Default.WithDocumentationMode(DocumentationMode.None);

    private static readonly CSharpParseOptions WithDocs =
        CSharpParseOptions.Default.WithDocumentationMode(DocumentationMode.Diagnose);

    public static TheoryData<string, CSharpParseOptions> Modes => new()
    {
        { "none", NoDocs },
        { "diagnose", WithDocs },
    };

    private const string Fixture = """
        using MintPlayer.Spark.Abstractions;

        namespace Fx;

        public class Company
        {
            /// <summary>The company name.</summary>
            public string Name { get; set; } = "";
        }

        public class Person
        {
            /// <summary>Plain summary.</summary>
            public string Plain { get; set; } = "";

            /// <summary>Works for <see cref="Company"/>.</summary>
            public string? CrefType { get; set; }

            /// <summary>Same as <see cref="Company.Name"/>.</summary>
            public string? CrefMember { get; set; }

            /// <summary>May be <see langword="null"/>.</summary>
            public string? Langword { get; set; }

            /// <summary>
            /// <para>First paragraph.</para>
            /// <para>Second paragraph.</para>
            /// </summary>
            public string Paras { get; set; } = "";

            /// <summary>
            /// Wrapped over
            /// two source lines.
            /// </summary>
            public string Wrapped { get; set; } = "";

            /// <summary>Use <c>code</c> here, "quoted" and back\slashed.</summary>
            public string Code { get; set; } = "";

            /// <summary>Summary here.</summary>
            /// <remarks>Remarks here.</remarks>
            public string WithRemarks { get; set; } = "";

            /// <remarks>Remarks only.</remarks>
            public string RemarksOnly { get; set; } = "";

            /// <inheritdoc/>
            public string Inherit { get; set; } = "";

            // normal comment
            public string NormalComment { get; set; } = "";

            public string Undocumented { get; set; } = "";

            /// <summary>Hidden from the model.</summary>
            [IgnoreProperty]
            public string Ignored { get; set; } = "";

            /// <summary>Read-only, not an attribute.</summary>
            public string GetterOnly => "";

            /// <summary>Static, not an attribute.</summary>
            public static string Static { get; set; } = "";

            /// <summary>Not public.</summary>
            internal string Internal { get; set; } = "";

            public class Address
            {
                /// <summary>Street of the nested type.</summary>
                public string Street { get; set; } = "";
            }
        }

        public class Box<T>
        {
            /// <summary>The boxed value.</summary>
            public T? Value { get; set; }
        }

        /// <summary>Split, first declaration.</summary>
        public partial class Split
        {
            public string A { get; set; } = "";
        }

        public partial class Split
        {
            /// <summary>Split property.</summary>
            public string B { get; set; } = "";
        }
        """;

    private static string Generate(CSharpParseOptions parseOptions, string source = Fixture)
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(SparkAttributeDescriptionAttribute)],
            rootNamespace: "Fx",
            parseOptions: parseOptions);

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.GeneratedSources.Should().ContainSingle(s => s.HintName == "SparkAttributeDescriptions.g.cs");
        return result.GeneratedSources.Single().Source;
    }

    private static string Line(string typeOf, string property, string summaryLiteral)
        => $"[assembly: global::MintPlayer.Spark.Abstractions.SparkAttributeDescription(typeof({typeOf}), \"{property}\", {summaryLiteral})]";

    [Theory, MemberData(nameof(Modes))]
    public void Plain_summary_becomes_one_attribute_line(string _, CSharpParseOptions mode)
    {
        Generate(mode).Should().Contain(Line("global::Fx.Person", "Plain", "\"Plain summary.\""));
    }

    [Theory, MemberData(nameof(Modes))]
    public void Cref_renders_as_the_simple_member_name(string _, CSharpParseOptions mode)
    {
        var generated = Generate(mode);

        generated.Should().Contain(Line("global::Fx.Person", "CrefType", "\"Works for Company.\""));
        generated.Should().Contain(Line("global::Fx.Person", "CrefMember", "\"Same as Name.\""));
    }

    [Theory, MemberData(nameof(Modes))]
    public void Langword_renders_as_the_keyword(string _, CSharpParseOptions mode)
    {
        Generate(mode).Should().Contain(Line("global::Fx.Person", "Langword", "\"May be null.\""));
    }

    [Theory, MemberData(nameof(Modes))]
    public void Para_becomes_a_newline_and_wrapped_lines_become_one(string _, CSharpParseOptions mode)
    {
        var generated = Generate(mode);

        generated.Should().Contain(Line("global::Fx.Person", "Paras", "\"First paragraph.\\nSecond paragraph.\""));
        generated.Should().Contain(Line("global::Fx.Person", "Wrapped", "\"Wrapped over two source lines.\""));
    }

    [Theory, MemberData(nameof(Modes))]
    public void Code_keeps_its_text_and_the_literal_is_escaped(string _, CSharpParseOptions mode)
    {
        Generate(mode).Should().Contain(
            Line("global::Fx.Person", "Code", "\"Use code here, \\\"quoted\\\" and back\\\\slashed.\""));
    }

    [Theory, MemberData(nameof(Modes))]
    public void Remarks_are_dropped_and_a_summary_less_comment_emits_nothing(string _, CSharpParseOptions mode)
    {
        var generated = Generate(mode);

        generated.Should().Contain(Line("global::Fx.Person", "WithRemarks", "\"Summary here.\""));
        generated.Should().NotContain("\"RemarksOnly\"");
        generated.Should().NotContain("\"Inherit\"");
    }

    [Theory, MemberData(nameof(Modes))]
    public void Undocumented_ignored_readonly_static_and_non_public_properties_emit_nothing(string _, CSharpParseOptions mode)
    {
        var generated = Generate(mode);

        generated.Should().NotContain("\"NormalComment\"");
        generated.Should().NotContain("\"Undocumented\"");
        generated.Should().NotContain("\"Ignored\"");
        generated.Should().NotContain("\"GetterOnly\"");
        generated.Should().NotContain("\"Static\"");
        generated.Should().NotContain("\"Internal\"");
    }

    [Theory, MemberData(nameof(Modes))]
    public void Nested_generic_and_partial_types_are_spelled_so_typeof_compiles(string _, CSharpParseOptions mode)
    {
        var generated = Generate(mode);

        generated.Should().Contain(Line("global::Fx.Person.Address", "Street", "\"Street of the nested type.\""));
        generated.Should().Contain(Line("global::Fx.Box<>", "Value", "\"The boxed value.\""));
        generated.Should().Contain(Line("global::Fx.Split", "B", "\"Split property.\""));
        generated.Should().NotContain("\"A\"");
    }

    [Theory, MemberData(nameof(Modes))]
    public void Output_is_sorted_by_type_then_property(string _, CSharpParseOptions mode)
    {
        var generated = Generate(mode);

        var lines = generated.Split('\n')
            .Where(l => l.StartsWith("[assembly:", StringComparison.Ordinal))
            .Select(l => l.Trim())
            .ToList();

        lines.Should().BeInAscendingOrder(StringComparer.Ordinal);
        lines.First().Should().Contain("typeof(global::Fx.Box<>)");
    }

    [Fact]
    public void Both_documentation_modes_produce_identical_output()
    {
        Generate(NoDocs).Should().Be(Generate(WithDocs));
    }

    [Fact]
    public void Generated_source_compiles_and_the_attributes_are_visible_by_reflection_in_a_debug_build()
    {
        // The generated file plus the fixture must compile as one assembly, and the attribute rows
        // must be readable the way the synchronizer reads them. DEBUG is defined here, so the
        // [Conditional("DEBUG")] applications survive.
        var generated = Generate(NoDocs);
        var attrs = GeneratorHarness.EmitAndLoad(
            "Fx.Debug",
            [Fixture, generated],
            [typeof(SparkAttributeDescriptionAttribute)],
            NoDocs.WithPreprocessorSymbols("DEBUG"))
            .GetCustomAttributes(typeof(SparkAttributeDescriptionAttribute), inherit: false)
            .Cast<SparkAttributeDescriptionAttribute>()
            .ToList();

        attrs.Should().Contain(a => a.Type.FullName == "Fx.Person" && a.Property == "Plain" && a.Summary == "Plain summary.");
        attrs.Should().Contain(a => a.Type.FullName == "Fx.Box`1" && a.Property == "Value");
    }

    [Fact]
    public void A_release_build_carries_no_description_attributes()
    {
        var generated = Generate(NoDocs);
        GeneratorHarness.EmitAndLoad(
                "Fx.Release",
                [Fixture, generated],
                [typeof(SparkAttributeDescriptionAttribute)],
                NoDocs.WithPreprocessorSymbols("RELEASE"))
            .GetCustomAttributes(typeof(SparkAttributeDescriptionAttribute), inherit: false)
            .Should().BeEmpty();
    }

    [Fact]
    public void No_documented_properties_produces_no_source()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            ["namespace Fx; public class Foo { public string Bar { get; set; } = \"\"; }"],
            referenceTypes: [typeof(SparkAttributeDescriptionAttribute)],
            rootNamespace: "Fx");

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Without_spark_abstractions_referenced_produces_no_source()
    {
        var result = GeneratorHarness.Run(
            GeneratorName,
            ["namespace Fx; public class Foo { /// <summary>Doc.</summary>\n public string Bar { get; set; } = \"\"; }"],
            referenceTypes: [],
            rootNamespace: "Fx");

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Malformed_xml_still_yields_the_summary_text()
    {
        var source = """
            namespace Fx;
            public class Foo
            {
                /// <summary>Less than 5 <b>is fine</summary>
                public int Bar { get; set; }
            }
            """;

        var result = GeneratorHarness.Run(
            GeneratorName,
            [source],
            referenceTypes: [typeof(SparkAttributeDescriptionAttribute)],
            rootNamespace: "Fx",
            parseOptions: NoDocs);

        result.GeneratedSources.Single().Source.Should().Contain("\"Bar\", \"Less than 5 is fine\"");
    }
}
