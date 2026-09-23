using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// ⚠️ <b>Pins a claim the repository had been asserting in prose and never measured:</b> that an
/// analyzer observes source-generator output within one compilation.
/// <para>
/// <c>SortCompanionAnalyzer</c>'s class comment rests its whole correctness argument on it
/// ("analyzers run <em>after</em> generators within a single compilation … its generated members are
/// still present in the symbol model"), <c>DefaultIndexAnalyzer</c> opts in with
/// <c>GeneratedCodeAnalysisFlags.Analyze</c>, and <c>DefaultIndexAnalyzerTests</c> says outright that
/// "these fixtures never run the generator". Every other analyzer fixture in this project attaches the
/// analyzer to the hand-written compilation alone, so nothing here demonstrated the claim either way.
/// </para>
/// <para>
/// These tests run the generator and the analyzer over <em>one</em> compilation and record the
/// measured answer. The short version, which the individual tests state precisely:
/// </para>
/// <list type="bullet">
/// <item><b>Generated symbols are always in the symbol model.</b> Any analyzer, whatever its
/// <c>GeneratedCodeAnalysisFlags</c>, can look a generated type or member up through the compilation
/// or through a hand-written symbol's members. This is the property <c>SortCompanionAnalyzer</c>
/// actually depends on, and it holds.</item>
/// <item><b>Whether an <em>action</em> fires for a generated declaration is a different question</b>
/// and does depend on the flags — <c>None</c> skips those declarations entirely.</item>
/// <item><b>A diagnostic whose location lies in a generated tree is dropped without a trace</b> unless
/// the analyzer opts in with <c>ReportDiagnostics</c>.</item>
/// <item><b>"Generated" is decided per tree, by file name and by a leading <c>&lt;auto-generated&gt;</c>
/// comment</b> — not by the fact that a generator produced the tree.</item>
/// <item><b>A diagnostic pointed at a referenced project's source location is not merely lost, it
/// throws</b> — re-measuring what <c>ValueObjectCompletenessAnalyzer.cs:136-150</c> recorded.</item>
/// </list>
/// <para>
/// ⚠️ <b>Scope.</b> Everything here is measured against the Roslyn analyzer driver
/// (<c>Microsoft.CodeAnalysis.CSharp</c> 5.3.0, the version the generators are compiled against),
/// which is the same driver <c>csc</c> hosts. It is <b>not</b> a measurement of Visual Studio's live
/// analysis: whether the IDE has re-run the generator recently enough for its output to be in the
/// compilation the squiggles come from is a host-scheduling question this project cannot observe.
/// The conclusion that survives either way is the one the rules should be written to:
/// <b>anchor a rule on a hand-written declaration, and let the generated symbol only ever turn a
/// diagnostic off.</b>
/// </para>
/// </summary>
public class AnalyzerSeesGeneratedCodeTests
{
    private const string GeneratorName = "GenerateIndexGenerator";

    /// <summary>
    /// A hand-written index pair of exactly the shape the corpus uses: a <c>partial</c> index entity
    /// carrying <c>[Search]</c>, and a <c>partial</c> index whose constructor declares the indexing and
    /// maps the <em>generated</em> <c>ModelSort</c> companion.
    /// </summary>
    /// <remarks>
    /// The map assigns <c>ModelSort</c>, a member that exists only when the generator has run — so
    /// without generation this fixture does not even compile, which is the coupling under test.
    /// Analyzers run regardless of compile errors, so both halves are measurable.
    /// </remarks>
    private const string HandWrittenPair = """
        using System.Collections.Generic;
        using System.Linq;
        using MintPlayer.Spark.Abstractions;
        using Raven.Client.Documents.Indexes;

        namespace TestApp.Data;

        public class Car
        {
            public string? Id { get; set; }
            public string? Model { get; set; }
        }

        public partial class Cars_Overview : AbstractIndexCreationTask<Car>
        {
            public Cars_Overview()
            {
                Map = cars => from car in cars
                              select new VCar { Model = car.Model, ModelSort = car.Model };

                Index(nameof(VCar.Model), FieldIndexing.Search);
            }
        }

        [FromIndex(typeof(Cars_Overview))]
        public partial class VCar
        {
            [Search] public string? Model { get; set; }
        }
        """;

    private static Task<GeneratorThenAnalyzerResult> RunAsync(
        string? generatorTypeName,
        IEnumerable<DiagnosticAnalyzer> analyzers,
        params (string Path, string Text)[] sources)
        => GeneratorHarness.RunGeneratorThenAnalyzersAsync(
            generatorTypeName,
            analyzers,
            sources,
            referenceTypes:
            [
                typeof(FromIndexAttribute),
                typeof(GenerateIndexAttribute),
                typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask),
            ],
            rootNamespace: "TestApp");

    // ---------------------------------------------------------------------------------------------
    // 1. The claim itself: a real analyzer changes its answer because a generated member exists.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The control. Without the generator, <c>VCar</c> has no <c>ModelSort</c> and SPARK005 fires —
    /// which is what every other fixture in this project measures.
    /// </summary>
    [Fact]
    public async Task Without_the_generator_the_missing_companion_is_flagged()
    {
        var result = await RunAsync(
            generatorTypeName: null,
            [GeneratorHarness.CreateAnalyzer("SortCompanionAnalyzer")],
            ("Pair.cs", HandWrittenPair));

        result.GeneratedTreePaths.Should().BeEmpty();
        result.Diagnostics.Where(d => d.Id == "SPARK005").Should().ContainSingle(
            "the companion only exists once the generator has contributed it");
    }

    /// <summary>
    /// ⚠️ <b>The measurement.</b> The same sources, with <c>GenerateIndexGenerator</c> run first, go
    /// quiet: the analyzer found <c>VCar.ModelSort</c>, a property that exists nowhere in the
    /// hand-written trees. <b>An analyzer does see generator output.</b>
    /// </summary>
    [Fact]
    public async Task With_the_generator_the_analyzer_finds_the_generated_companion_and_stays_quiet()
    {
        var result = await RunAsync(
            GeneratorName,
            [GeneratorHarness.CreateAnalyzer("SortCompanionAnalyzer")],
            ("Pair.cs", HandWrittenPair));

        result.GeneratedTreePaths.Should().ContainSingle(p => p.EndsWith("SparkIndexEntitySortFields.g.cs"));

        // The generated member really is the only source of ModelSort.
        var vcar = result.Compilation.GetTypeByMetadataName("TestApp.Data.VCar");
        vcar.Should().NotBeNull();
        vcar!.GetMembers("ModelSort").Should().ContainSingle();

        result.Diagnostics.Where(d => d.Id is "SPARK005" or "SPARK006").Should().BeEmpty(
            "SortCompanionAnalyzer resolves the companion from the generated partial half");
    }

    // ---------------------------------------------------------------------------------------------
    // 2. Symbol-model visibility vs. action invocation — two different questions.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>Visibility does not depend on <c>GeneratedCodeAnalysisFlags</c>.</b> A generated type is
    /// resolvable through <c>Compilation.GetTypeByMetadataName</c> from an analyzer configured with
    /// <c>None</c> just as from one configured with <c>Analyze</c> — the flags gate which declarations
    /// an <em>action</em> visits, never what the symbol model contains.
    /// </summary>
    [Theory]
    [InlineData(GeneratedCodeAnalysisFlags.None)]
    [InlineData(GeneratedCodeAnalysisFlags.Analyze)]
    [InlineData(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics)]
    public async Task A_generated_type_is_resolvable_from_the_compilation_under_every_flag(
        GeneratedCodeAnalysisFlags flags)
    {
        var probe = new CompilationProbeAnalyzer(flags, "TestApp.Data.VCar", "ModelSort");

        await RunAsync(GeneratorName, [probe], ("Pair.cs", HandWrittenPair));

        probe.TypeResolved.Should().BeTrue();
        probe.MemberResolved.Should().BeTrue();
    }

    /// <summary>
    /// ⚠️ <b>The part that does depend on the flags.</b> A generated declaration is visited by a
    /// symbol or syntax-node action only when the analyzer asks for <c>Analyze</c>. With the default
    /// (<c>None</c>) the generated partial declaration of <c>VCar</c> and its <c>ModelSort</c>
    /// property are never handed to the action at all.
    /// </summary>
    /// <remarks>
    /// This is why <c>DefaultIndexAnalyzer</c> re-derives a generated index's claim from the
    /// <c>[GenerateIndex]</c> attribute on the entity instead of waiting for the generated index class
    /// to walk past: a rule that only ever fires from a generated declaration is a rule one flag — or
    /// one host that does not run the generator — away from going silent.
    /// </remarks>
    [Fact]
    public async Task Symbol_and_syntax_actions_visit_generated_declarations_only_under_Analyze()
    {
        var withNone = new DeclarationProbeAnalyzer(GeneratedCodeAnalysisFlags.None);
        var withAnalyze = new DeclarationProbeAnalyzer(GeneratedCodeAnalysisFlags.Analyze);

        await RunAsync(GeneratorName, [withNone], ("Pair.cs", HandWrittenPair));
        await RunAsync(GeneratorName, [withAnalyze], ("Pair.cs", HandWrittenPair));

        // Hand-written declarations are visited either way.
        withNone.SyntaxClassesIn("Pair.cs").Should().Contain("VCar");
        withAnalyze.SyntaxClassesIn("Pair.cs").Should().Contain("VCar");

        // Generated ones are not, unless the analyzer opted in.
        withNone.SyntaxClassesIn("SparkIndexEntitySortFields.g.cs").Should().BeEmpty();
        withAnalyze.SyntaxClassesIn("SparkIndexEntitySortFields.g.cs").Should().Contain("VCar");

        // The symbol action is the same story, measured on the member that exists only in the
        // generated half: a member declared only in the generated tree is not visited under None.
        withNone.SymbolMembers.Should().NotContain("ModelSort");
        withAnalyze.SymbolMembers.Should().Contain("ModelSort");
    }

    /// <summary>
    /// ✅ <b>The property M5 depends on.</b> A <c>partial</c> type with one hand-written declaration is
    /// still visited by a symbol action under the default <c>GeneratedCodeAnalysisFlags.None</c>, even
    /// after a generator has contributed a second, generated declaration of the same type — Roslyn
    /// treats a symbol as generated only when <em>every</em> one of its declarations is.
    /// </summary>
    /// <remarks>
    /// A rule over a partial index class therefore keeps firing whether or not the generator ran and
    /// whatever the host's generated-code setting, provided it anchors on the hand-written declaration.
    /// </remarks>
    [Fact]
    public async Task A_partial_type_with_a_hand_written_half_is_visited_even_under_None()
    {
        var probe = new DeclarationProbeAnalyzer(GeneratedCodeAnalysisFlags.None);

        var result = await RunAsync(GeneratorName, [probe], ("Pair.cs", HandWrittenPair));

        result.GeneratedTreePaths.Should().NotBeEmpty();
        probe.SymbolTypes.Should().Contain("VCar", "the generator contributed a second partial declaration of it");
        probe.SymbolTypes.Should().Contain("Cars_Overview");
    }

    /// <summary>
    /// ✅ <b>The other property M5 depends on.</b> The generated <c>IndexSearchFields()</c> method is
    /// on the index symbol's member list even for an analyzer configured with <c>None</c>, so a rule
    /// can ask "did the generator emit this method?" without opting into generated-code analysis.
    /// </summary>
    [Fact]
    public async Task The_generated_IndexSearchFields_method_is_on_the_index_symbol_under_None()
    {
        var probe = new CompilationProbeAnalyzer(
            GeneratedCodeAnalysisFlags.None, "TestApp.Data.Cars_Overview", "IndexSearchFields");

        await RunAsync(GeneratorName, [probe], ("Pair.cs", HandWrittenPair));

        probe.TypeResolved.Should().BeTrue();
        probe.MemberResolved.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------------
    // 4. The cross-project drop — the same measurement ValueObjectCompletenessAnalyzer:136-150 records.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// ⚠️ <b>A diagnostic whose location lives in a referenced project's syntax tree never reaches the
    /// developer.</b> Re-measured here because it decides whether an M5-style rule can be trusted in
    /// the <em>entity library + application</em> topology: a dropped diagnostic is indistinguishable
    /// from a passing check.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The mechanism is harsher than "dropped".</b> On Roslyn 5.3.0
    /// <c>SymbolAnalysisContext.ReportDiagnostic</c> <em>throws</em> <see cref="ArgumentException"/>
    /// ("… is not part of the compilation being analyzed") the moment it is handed such a location. An
    /// analyzer that does not expect it therefore dies mid-action and loses every <em>other</em>
    /// diagnostic it would have reported for that symbol, leaving only <c>AD0001</c> — informational,
    /// and routinely never looked at. <c>ValueObjectCompletenessAnalyzer.cs:136-150</c> records the
    /// same outcome ("a two-project fixture reported zero diagnostics") and its repair — report at a
    /// location the analyzed symbol owns — is the right one either way.
    /// <para>
    /// Built on a real <c>AdhocWorkspace</c> with a <c>ProjectReference</c>, which resolves to a
    /// <c>CompilationReference</c> — the shape a loaded IDE solution has, and the reason this is an
    /// IDE-only disappearance. At <c>dotnet build</c> the same reference arrives as a .dll, the
    /// library's types carry no source location at all, and a diagnostic reported for one renders
    /// without a file or line (measured separately, see the repo's cross-assembly notes).
    /// </para>
    /// <para>
    /// ✅ <b>M5 is not exposed to this.</b> Its inputs — the index class, its constructor, the
    /// <c>[FromIndex]</c> projection and its <c>[Search]</c> attributes — are all application-owned in
    /// every index in the corpus (<c>apps/DemoApp/DemoApp/Indexes/</c>); only the mapped entity lives
    /// in a library, and the rule never needs to point at it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_diagnostic_located_in_a_referenced_project_is_dropped()
    {
        const string library = """
            namespace EntityLib;

            public class Car { public string? Model { get; set; } }
            """;

        const string app = """
            namespace TestApp;

            public class Cars_Overview { }
            """;

        using var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;

        var libId = ProjectId.CreateNewId("EntityLib");
        var appId = ProjectId.CreateNewId("App");
        var references = GeneratorHarness.BuildReferences([]).ToList();

        solution = solution
            .AddProject(ProjectInfo.Create(
                libId, VersionStamp.Create(), "EntityLib", "EntityLib", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: references))
            .AddDocument(DocumentId.CreateNewId(libId), "Car.cs",
                Microsoft.CodeAnalysis.Text.SourceText.From(library), filePath: "/EntityLib/Car.cs")
            .AddProject(ProjectInfo.Create(
                appId, VersionStamp.Create(), "App", "App", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: references,
                projectReferences: [new ProjectReference(libId)]))
            .AddDocument(DocumentId.CreateNewId(appId), "Cars_Overview.cs",
                Microsoft.CodeAnalysis.Text.SourceText.From(app), filePath: "/App/Cars_Overview.cs");

        var compilation = await solution.GetProject(appId)!.GetCompilationAsync();
        compilation.Should().NotBeNull();

        // The library really did arrive as a CompilationReference, not a .dll — otherwise this test
        // would be measuring the dotnet-build shape instead and pass for the wrong reason.
        compilation!.References.OfType<CompilationReference>().Should().NotBeEmpty();

        var libraryType = compilation.GetTypeByMetadataName("EntityLib.Car");
        libraryType.Should().NotBeNull();
        libraryType!.Locations.Should().Contain(l => l.IsInSource,
            "a CompilationReference keeps the library's source locations alive");

        // (a) A probe that swallows the failure: the library diagnostic never lands, the app one does.
        var catching = new TwoLocationReportingAnalyzer { CatchReportFailures = true };
        var catchingDiagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(catching))
            .GetAnalyzerDiagnosticsAsync(default);

        catching.ReportedAtLibrary.Should().BeTrue("the analyzer did call ReportDiagnostic for the library type");
        catching.ReportedAtApp.Should().BeTrue();

        // ⚠️ Measured mechanism, Roslyn 5.3.0: ReportDiagnostic REJECTS the out-of-compilation
        // location outright rather than filtering it later. The analyzer author sees an
        // ArgumentException; the developer sees nothing.
        catching.LibraryReportThrew.Should().Be("ArgumentException");
        catching.LibraryReportMessage.Should().Contain("not part of the compilation");

        var surviving = catchingDiagnostics.Where(d => d.Id == TwoLocationReportingAnalyzer.Id).ToList();
        surviving.Should().ContainSingle("only the diagnostic inside this compilation survives");
        surviving[0].GetMessage().Should().Be("App");

        // (b) What a real rule would do — not catch. The whole symbol action dies on the first
        // report, so the diagnostic that WOULD have been valid is lost too, and the only trace is
        // AD0001, which is informational and routinely invisible.
        var uncatching = new TwoLocationReportingAnalyzer { CatchReportFailures = false };
        var uncatchingDiagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(uncatching))
            .GetAnalyzerDiagnosticsAsync(default);

        uncatchingDiagnostics.Where(d => d.Id == TwoLocationReportingAnalyzer.Id).Should().BeEmpty();
        uncatchingDiagnostics.Where(d => d.Id == "AD0001").Should().NotBeEmpty(
            "an analyzer that reports across a project boundary crashes rather than warns");
    }

    /// <summary>
    /// ⚠️ <b>A diagnostic located in a generated tree is dropped silently</b> unless the analyzer opts
    /// in with <c>ReportDiagnostics</c>. This is the mechanism <c>SortCompanionAnalyzer:82-84</c>
    /// guards against by refusing to report at anything but an in-source, hand-written location.
    /// </summary>
    [Fact]
    public async Task A_diagnostic_located_in_a_generated_tree_is_dropped_without_ReportDiagnostics()
    {
        var suppressed = new GeneratedLocationReportingAnalyzer(GeneratedCodeAnalysisFlags.Analyze);
        var reporting = new GeneratedLocationReportingAnalyzer(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

        var suppressedRun = await RunAsync(GeneratorName, [suppressed], ("Pair.cs", HandWrittenPair));
        var reportingRun = await RunAsync(GeneratorName, [reporting], ("Pair.cs", HandWrittenPair));

        // Both analyzers reached the generated member and called ReportDiagnostic.
        suppressed.Reported.Should().BeTrue();
        reporting.Reported.Should().BeTrue();

        // Only one of the two diagnostics survived the driver.
        suppressedRun.Diagnostics.Where(d => d.Id == GeneratedLocationReportingAnalyzer.Id)
            .Should().BeEmpty("the generated-code filter drops diagnostics by location");
        reportingRun.Diagnostics.Where(d => d.Id == GeneratedLocationReportingAnalyzer.Id)
            .Should().ContainSingle();
    }

    // ---------------------------------------------------------------------------------------------
    // 3. What makes a tree "generated" at all.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// ⚠️ <b>"Generated" is a property of the tree, not of its origin.</b> Roslyn decides per syntax
    /// tree, from the file name (<c>.g.cs</c>, <c>.designer.cs</c>, …) and from a leading
    /// <c>&lt;auto-generated&gt;</c> comment. A hand-written file named <c>*.g.cs</c> is treated as
    /// generated; so is one whose first comment says so. A generator whose output carried neither
    /// marker would be analyzed like ordinary code.
    /// </summary>
    /// <remarks>
    /// This repo's generators emit both markers — the <c>MintPlayer.SourceGenerators.Tools</c> writer
    /// puts an <c>&lt;auto-generated&gt;</c> banner at the top and every hint name ends in
    /// <c>.g.cs</c> — so their output is squarely on the "generated" side of this test, twice over.
    /// <c>[GeneratedCode]</c> is a third, per-symbol marker; none of this repo's generators emit it.
    /// </remarks>
    [Fact]
    public async Task Generated_code_is_recognised_by_file_name_and_by_the_auto_generated_banner()
    {
        var probe = new DeclarationProbeAnalyzer(GeneratedCodeAnalysisFlags.None);

        await RunAsync(
            generatorTypeName: null,
            [probe],
            ("Ordinary.cs", "namespace T; public class OrdinaryOne { }"),
            ("ByName.g.cs", "namespace T; public class ByNameOne { }"),
            ("ByBanner.cs", "// <auto-generated/>\nnamespace T; public class ByBannerOne { }"));

        probe.SyntaxClassesIn("Ordinary.cs").Should().Contain("OrdinaryOne");
        probe.SyntaxClassesIn("ByName.g.cs").Should().BeEmpty("the .g.cs suffix alone marks a tree generated");
        probe.SyntaxClassesIn("ByBanner.cs").Should().BeEmpty("the <auto-generated/> banner alone marks a tree generated");
    }

    // ---------------------------------------------------------------------------------------------
    // Probes
    // ---------------------------------------------------------------------------------------------

    private static readonly DiagnosticDescriptor ProbeRule = new(
        id: "PROBE001",
        title: "probe",
        messageFormat: "{0}",
        category: "Probe",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>Records which declarations a symbol/syntax action was actually handed.</summary>
    private sealed class DeclarationProbeAnalyzer(GeneratedCodeAnalysisFlags flags) : DiagnosticAnalyzer
    {
        private readonly ConcurrentBag<(string File, string Name)> _syntax = [];
        private readonly ConcurrentBag<string> _members = [];
        private readonly ConcurrentBag<string> _types = [];

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [ProbeRule];

        public IEnumerable<string> SyntaxClassesIn(string fileSuffix)
            => _syntax.Where(x => x.File.EndsWith(fileSuffix, StringComparison.Ordinal)).Select(x => x.Name);

        public IEnumerable<string> SymbolMembers => _members;

        public IEnumerable<string> SymbolTypes => _types;

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(flags);
            context.RegisterSyntaxNodeAction(
                c => _syntax.Add((c.Node.SyntaxTree.FilePath, ((ClassDeclarationSyntax)c.Node).Identifier.Text)),
                SyntaxKind.ClassDeclaration);
            context.RegisterSymbolAction(c => _members.Add(c.Symbol.Name), SymbolKind.Property);
            context.RegisterSymbolAction(c => _types.Add(c.Symbol.Name), SymbolKind.NamedType);
        }
    }

    /// <summary>
    /// Reports two diagnostics from one compilation action: one at a type declared in a referenced
    /// project, one at a type declared in this compilation.
    /// </summary>
    private sealed class TwoLocationReportingAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "PROBE003";

        private static readonly DiagnosticDescriptor Rule = new(
            Id, "probe", "{0}", "Probe", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public bool ReportedAtLibrary { get; private set; }
        public bool ReportedAtApp { get; private set; }

        /// <summary>The exception <c>ReportDiagnostic</c> threw for the library location, if it threw.</summary>
        public string? LibraryReportThrew { get; private set; }

        /// <summary>Its message, which is the only statement of the cause a developer would ever get.</summary>
        public string? LibraryReportMessage { get; private set; }

        /// <summary>
        /// When false the probe lets the exception escape, which is what a real rule would do — the
        /// symbol action then aborts and reports nothing at all.
        /// </summary>
        public bool CatchReportFailures { get; init; } = true;

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze);

            // A SYMBOL action on the application's own type — the shape ValueObjectCompletenessAnalyzer
            // uses, and the shape an M5-style index rule would use.
            context.RegisterSymbolAction(c =>
            {
                if (c.Symbol.Name != "Cars_Overview") return;

                var libraryLocation = c.Compilation.GetTypeByMetadataName("EntityLib.Car")?
                    .Locations.FirstOrDefault(l => l.IsInSource);

                if (libraryLocation is not null)
                {
                    ReportedAtLibrary = true;
                    try
                    {
                        c.ReportDiagnostic(Diagnostic.Create(Rule, libraryLocation, "EntityLib"));
                    }
                    catch (Exception ex) when (CatchReportFailures)
                    {
                        LibraryReportThrew = ex.GetType().Name;
                        LibraryReportMessage = ex.Message;
                    }
                }

                var appLocation = c.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
                if (appLocation is not null)
                {
                    ReportedAtApp = true;
                    c.ReportDiagnostic(Diagnostic.Create(Rule, appLocation, "App"));
                }
            }, SymbolKind.NamedType);
        }
    }

    /// <summary>Asks the compilation for a symbol by name, from a compilation-level action.</summary>
    private sealed class CompilationProbeAnalyzer(
        GeneratedCodeAnalysisFlags flags, string metadataName, string memberName) : DiagnosticAnalyzer
    {
        public bool TypeResolved { get; private set; }
        public bool MemberResolved { get; private set; }

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [ProbeRule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(flags);
            context.RegisterCompilationAction(c =>
            {
                var type = c.Compilation.GetTypeByMetadataName(metadataName);
                TypeResolved = type is not null;
                MemberResolved = type?.GetMembers(memberName).Length > 0;
            });
        }
    }

    /// <summary>
    /// Reaches a generated member through a hand-written symbol and reports a diagnostic <em>at the
    /// generated member's own location</em> — the case the generated-code filter drops.
    /// </summary>
    private sealed class GeneratedLocationReportingAnalyzer(GeneratedCodeAnalysisFlags flags) : DiagnosticAnalyzer
    {
        public const string Id = "PROBE002";

        private static readonly DiagnosticDescriptor Rule = new(
            Id, "probe", "{0}", "Probe", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public bool Reported { get; private set; }

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(flags);
            context.RegisterCompilationAction(c =>
            {
                var companion = c.Compilation
                    .GetTypeByMetadataName("TestApp.Data.VCar")?
                    .GetMembers("ModelSort")
                    .FirstOrDefault();

                var location = companion?.Locations.FirstOrDefault(l => l.IsInSource);
                if (location is null) return;

                Reported = true;
                c.ReportDiagnostic(Diagnostic.Create(Rule, location, "ModelSort"));
            });
        }
    }
}
