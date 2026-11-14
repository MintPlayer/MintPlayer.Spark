using Microsoft.CodeAnalysis;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using MintPlayer.Spark.SourceGenerators.Tests.Generators;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK061/SPARK062 (#660 G17, <c>docs/code-coverage/command-reactions-PRD.md</c>): an actions class that takes over,
/// or collides with, a referenced library's actions class of the same name.
/// </summary>
public class ActionsOverrideAnalyzerTests
{
    private const string AnalyzerName = "ActionsOverrideAnalyzer";
    private static readonly Type[] References = ActionsRegistrationGeneratorTests.LibraryReferences;

    private static Task<IReadOnlyList<Diagnostic>> RunAsync(string source, params MetadataReference[] libraries)
        => GeneratorHarness.RunAnalyzerAsync(AnalyzerName, [source], References, additionalReferences: libraries);

    [Fact]
    public async Task Deriving_from_the_library_class_reports_nothing()
    {
        var diagnostics = await RunAsync("""
            namespace TestApp;
            public partial class WidgetActions : WidgetLibrary.WidgetActions { }
            """, ActionsRegistrationGeneratorTests.WidgetLibrary());

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_same_named_class_that_does_not_derive_from_the_library_class_is_SPARK062()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Actions;
            namespace TestApp;
            public partial class WidgetActions : DefaultPersistentObjectActions<WidgetLibrary.Widget>
            {
                public WidgetActions() : base(null!) { }
            }
            """, ActionsRegistrationGeneratorTests.WidgetLibrary());

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK062");
        diagnostic.GetMessage().Should().Contain("WidgetLibrary.WidgetActions").And.Contain("does not derive from it");
    }

    [Fact]
    public async Task A_differently_named_class_reports_nothing()
    {
        var diagnostics = await RunAsync("""
            using MintPlayer.Spark.Actions;
            namespace TestApp;
            public partial class GadgetActions : DefaultPersistentObjectActions<WidgetLibrary.Widget>
            {
                public GadgetActions() : base(null!) { }
            }
            """, ActionsRegistrationGeneratorTests.WidgetLibrary());

        diagnostics.Should().BeEmpty();
    }

    /// <summary>A downstream library that already took WidgetActions over (the generator's attribute, written out).</summary>
    private static MetadataReference Downstream(MetadataReference library) => GeneratorHarness.CompileToMetadataReference(
        "DownstreamLibrary",
        ["""
        [assembly: MintPlayer.Spark.Abstractions.SparkActionsOverride(typeof(WidgetLibrary.Widget), typeof(DownstreamLibrary.WidgetActions))]
        namespace DownstreamLibrary;
        public partial class WidgetActions : WidgetLibrary.WidgetActions { }
        """],
        References.Append(typeof(SparkActionsOverrideAttribute)),
        [library]);

    [Fact]
    public async Task A_sibling_of_an_override_already_recorded_by_a_referenced_assembly_is_SPARK061()
    {
        var library = ActionsRegistrationGeneratorTests.WidgetLibrary();
        var downstream = Downstream(library);

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            ["""
            namespace TestApp;
            public partial class WidgetActions : WidgetLibrary.WidgetActions { }
            """],
            References,
            additionalReferences: [library, downstream]);

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK061");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("DownstreamLibrary.WidgetActions");
    }

    [Fact]
    public async Task Continuing_a_chain_from_the_downstream_override_reports_nothing()
    {
        var library = ActionsRegistrationGeneratorTests.WidgetLibrary();
        var downstream = Downstream(library);

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            ["""
            namespace TestApp;
            public partial class WidgetActions : DownstreamLibrary.WidgetActions { }
            """],
            References,
            additionalReferences: [library, downstream]);

        diagnostics.Should().BeEmpty();
    }
}
