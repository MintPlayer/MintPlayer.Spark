using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Layering;

namespace MintPlayer.Spark.Tests.Layering;

/// <summary>
/// Composition M2 (S2, D14): the shared engine reproduces the actions composition the hand-written
/// engine produced before it was ported. The golden files were captured from that engine, before the
/// port; <c>MintPlayer.Spark.SourceGenerators.Tests</c> runs the same files through the generators'
/// build of the engine.
/// </summary>
public class SparkLayersGoldenTests
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The core layer under QnA's own <c>actions.json</c>: the only real application layer with custom actions.</summary>
    internal static SparkActionsLayer[] QnaLayers() =>
    [
        new("MintPlayer.Spark", File.ReadAllText(RepoFile("libs", "spark", "MintPlayer.Spark", "App_Data", "actions.json")), IsLibrary: true),
        new("app", File.ReadAllText(RepoFile("apps", "QnA", "QnA", "App_Data", "actions.json")), IsLibrary: false),
    ];

    /// <summary>Two libraries over the core, then the app: overrides, a removal, a conflict, annotations and case.</summary>
    internal static SparkActionsLayer[] LayeredLayers() =>
    [
        QnaLayers()[0],
        new("A.Lib", Layered("A.Lib"), IsLibrary: true),
        new("B.Lib", Layered("B.Lib"), IsLibrary: true),
        new("app", Layered("app"), IsLibrary: false),
    ];

    private static string Layered(string layer) => File.ReadAllText(RepoFile("tests", "MintPlayer.Spark.Tests", "Layering", "Golden", "layered", layer + ".json"));

    [Fact]
    public void Qna_actions_compose_as_before_the_port()
        => Describe(SparkActionLayers.Compose(QnaLayers())).Should().Be(Golden("qna-actions.golden.txt"));

    [Fact]
    public void Layered_actions_compose_as_before_the_port()
        => Describe(SparkActionLayers.Compose(LayeredLayers())).Should().Be(Golden("layered-actions.golden.txt"));

    [Fact]
    public void The_engine_describes_the_qna_layers_as_the_golden()
        => Engine(QnaLayers()).Should().Be(Golden("qna-actions.golden.txt"));

    [Fact]
    public void The_engine_describes_the_layered_layers_as_the_golden()
        => Engine(LayeredLayers()).Should().Be(Golden("layered-actions.golden.txt"));

    [Fact]
    public void The_one_decided_difference_a_property_reset_and_stated_again_comes_last()
    {
        var composition = SparkActionLayers.Compose(
        [
            QnaLayers()[0],
            new("A.Lib", """{ "Edit": { "icon": null } }""", IsLibrary: true),
            new("app", """{ "Edit": { "icon": "pen" } }""", IsLibrary: false),
        ]);

        var edit = composition.Actions.Single(a => a.Name == "Edit");
        edit.Properties.Keys.Should().Equal("showedOn", "selectionRule", "icon");
        edit.Properties["icon"].Layer.Should().Be("app");
    }

    private static string Engine(IEnumerable<SparkActionsLayer> layers)
        => SparkLayers.Compose(layers.Select(l => SparkLayer.Parse(l.Name, l.Json, l.IsLibrary)), SparkKinds.Actions).Describe();

    /// <summary>The golden format, rendered from the public result shape that predates the engine.</summary>
    private static string Describe(SparkActionsComposition composition)
    {
        var builder = new StringBuilder();
        foreach (var action in composition.Actions)
        {
            builder.Append(action.Name).Append(" @").Append(action.DeclaredBy).Append('\n');
            foreach (var (property, value) in action.Properties)
                builder.Append(action.Name).Append('.').Append(property).Append(" = ").Append(value.Value.ToJsonString(Relaxed)).Append(" @").Append(value.Layer).Append('\n');
        }
        foreach (var conflict in composition.Conflicts)
            builder.Append("conflict ").Append(conflict.Action).Append('.').Append(conflict.Property).Append(": ").Append(conflict.WinnerLayer).Append(" over ").Append(conflict.LoserLayer).Append('\n');
        return builder.ToString();
    }

    internal static string Golden(string name)
        => File.ReadAllText(RepoFile("tests", "MintPlayer.Spark.Tests", "Layering", "Golden", name)).Replace("\r\n", "\n");

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
