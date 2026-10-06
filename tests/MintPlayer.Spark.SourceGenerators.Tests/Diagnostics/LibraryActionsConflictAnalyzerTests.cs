using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK036 (#467, D7): two referenced libraries state the same property of the same action
/// differently. Composition M2 (D14) moved the analyzer onto the run time's layering engine, linked
/// into the generator assembly from the same source.
/// </summary>
public class LibraryActionsConflictAnalyzerTests
{
    private const string AnalyzerName = "LibraryActionsConflictAnalyzer";

    private static MetadataReference Library(string assemblyName, string json)
        => GeneratorHarness.CompileToMetadataReference(
            assemblyName,
            [$"[assembly: MintPlayer.Spark.Abstractions.SparkLayer({SymbolDisplay.FormatLiteral(assemblyName.ToLowerInvariant().Replace('.', '-'), quote: true)}, \"actions\", \"actions.json\", {SymbolDisplay.FormatLiteral(json, quote: true)})]"],
            [typeof(SparkLayerAttribute)]);

    private static Task<IReadOnlyList<Diagnostic>> RunAsync(params MetadataReference[] libraries)
        => GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            ["public class App { }"],
            referenceTypes: [typeof(SparkLayerAttribute)],
            additionalReferences: libraries);

    [Fact]
    public async Task A_library_overriding_a_library_it_depends_on_is_not_SPARK036()
    {
        // Grill Q3: overriding a dependency is intended; only unrelated libraries conflict.
        var extension = GeneratorHarness.CompileToMetadataReference(
            "A.Ext",
            [
                """[assembly: MintPlayer.Spark.Abstractions.SparkLayer("a-ext", "actions", "actions.json", "{ \"Archive\": { \"icon\": \"archive\" } }")]""",
                """[assembly: MintPlayer.Spark.Abstractions.SparkLayerDependencies("B.Lib")]""",
            ],
            [typeof(SparkLayerAttribute)]);

        var diagnostics = await RunAsync(extension, Library("B.Lib", """{ "Archive": { "icon": "box" } }"""));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_libraries_stating_a_property_differently_is_SPARK036_naming_both()
    {
        var diagnostics = await RunAsync(
            Library("A.Lib", """{ "Archive": { "icon": "box", "showedOn": "both" } }"""),
            Library("B.Lib", """{ "archive": { "icon": "archive", "showedOn": "both" } }"""));

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK036");
        diagnostic.GetMessage().Should().Contain("'B.Lib' and 'A.Lib'").And.Contain("'icon' of the action 'Archive'");
    }

    [Fact]
    public async Task The_same_value_in_two_libraries_is_no_conflict()
    {
        var diagnostics = await RunAsync(
            Library("A.Lib", """{ "Archive": { "offset": 1.0, "label": { "en": "a", "nl": "b" } } }"""),
            Library("B.Lib", """{ "Archive": { "offset": 1, "label": { "nl": "b", "en": "a" } } }"""));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_property_a_later_library_resets_is_no_conflict_for_the_one_after_it()
    {
        var diagnostics = await RunAsync(
            Library("A.Lib", """{ "Archive": { "icon": "box" } }"""),
            Library("B.Lib", """{ "Archive": { "icon": null } }"""),
            Library("C.Lib", """{ "Archive": { "icon": "archive" } }"""));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task Annotations_are_not_actions_or_properties()
    {
        var diagnostics = await RunAsync(
            Library("A.Lib", """{ "$schema": "a", "_comment": "a", "Archive": { "_note": "a" } }"""),
            Library("B.Lib", """{ "$schema": "b", "_comment": "b", "Archive": { "_note": "b" } }"""));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_layer_that_does_not_compose_is_left_to_the_run_time()
    {
        var diagnostics = await RunAsync(
            Library("A.Lib", """{ "Archive": { "icon": "box" } }"""),
            Library("B.Lib", """{ "Archive": { "icon": "archive" }, "archive": {} }"""));

        diagnostics.Should().BeEmpty();
    }

    // ── One engine, two builds (D14) ────────────────────────────────────────────────────────────

    [Fact]
    public void The_generators_build_of_the_engine_reproduces_the_qna_golden()
        => GeneratorEngine(
            ("MintPlayer.Spark", RepoFile("libs", "spark", "MintPlayer.Spark", "App_Data", "actions.json"), true),
            ("app", RepoFile("apps", "QnA", "QnA", "App_Data", "actions.json"), false))
            .Should().Be(Golden("qna-actions.golden.txt"));

    [Fact]
    public void The_generators_build_of_the_engine_reproduces_the_layered_golden()
        => GeneratorEngine(
            ("MintPlayer.Spark", RepoFile("libs", "spark", "MintPlayer.Spark", "App_Data", "actions.json"), true),
            ("A.Lib", GoldenFile("layered", "A.Lib.json"), true),
            ("B.Lib", GoldenFile("layered", "B.Lib.json"), true),
            ("app", GoldenFile("layered", "app.json"), false))
            .Should().Be(Golden("layered-actions.golden.txt"));

    /// <summary>
    /// The layers composed by the engine compiled into the generator assembly. Its types are internal
    /// and this project does not reference the assembly at compile time, hence reflection; the
    /// run-time build runs the same files in <c>MintPlayer.Spark.Tests</c> (SparkLayersGoldenTests).
    /// </summary>
    private static string GeneratorEngine(params (string Name, string Path, bool IsLibrary)[] layers)
    {
        var assembly = Assembly.Load("MintPlayer.Spark.SourceGenerators");
        var layerType = assembly.GetType("MintPlayer.Spark.Layering.SparkLayer", throwOnError: true)!;
        var parse = layerType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)!;

        var parsed = Array.CreateInstance(layerType, layers.Length);
        for (var i = 0; i < layers.Length; i++)
            parsed.SetValue(parse.Invoke(null, [layers[i].Name, File.ReadAllText(layers[i].Path), layers[i].IsLibrary]), i);

        var actions = assembly.GetType("MintPlayer.Spark.Layering.SparkKinds", throwOnError: true)!
            .GetField("Actions", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        var composition = assembly.GetType("MintPlayer.Spark.Layering.SparkLayers", throwOnError: true)!
            .GetMethod("Compose", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [parsed, actions])!;

        return (string)composition.GetType().GetMethod("Describe")!.Invoke(composition, null)!;
    }

    private static string Golden(string name) => File.ReadAllText(GoldenFile(name)).Replace("\r\n", "\n");

    private static string GoldenFile(params string[] parts)
        => RepoFile(["tests", "MintPlayer.Spark.Tests", "Layering", "Golden", .. parts]);

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MintPlayer.Spark.slnx")))
            directory = directory.Parent;

        return directory is null
            ? throw new InvalidOperationException($"Could not locate the repository root above '{AppContext.BaseDirectory}'.")
            : Path.Combine([directory.FullName, .. parts]);
    }
}
