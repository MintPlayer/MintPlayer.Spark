using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using VerifyXunit;
using OutputKind = Microsoft.CodeAnalysis.OutputKind;
using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;

namespace MintPlayer.Spark.SourceGenerators.Tests.Snapshots;

/// <summary>
/// Verify-based golden-file snapshots of the four highest-traffic generators. The structural
/// "Should().Contain(...)" tests in the sibling Generators/ folder catch shape regressions;
/// these snapshots additionally pin the <em>exact</em> emitted source so that any unintended
/// drift (renamed helpers, dropped translation keys, attribute name mismatches) shows up as
/// a reviewable diff in the PR rather than being noticed weeks later when something breaks
/// downstream. Snapshot files live in <c>Snapshots/SourceGeneratorSnapshots.*.verified.txt</c>;
/// any change requires the dev to inspect the diff and accept (rename received → verified).
/// </summary>
public class SourceGeneratorSnapshots
{
    /// <summary>Format the harness output so Verify produces a readable, scrubbed-friendly snapshot.</summary>
    private static string Render(GeneratorRunResult result)
    {
        if (result.GeneratedSources.Count == 0)
            return "<no generated sources>";

        var parts = result.GeneratedSources
            .OrderBy(s => s.HintName, StringComparer.Ordinal)
            .Select(s => $"=== {s.HintName} ==={Environment.NewLine}{s.Source}");
        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    [Fact]
    public Task ActionsRegistrationGenerator_emits_AddActions_for_two_actions_classes()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;
            using MintPlayer.Spark.Actions;

            namespace TestApp.Actions;

            public partial class CarActions : DefaultPersistentObjectActions<Car>
            {
                public CarActions() : base(null!, null!, null!, null!) { }
            }

            public partial class PersonActions : DefaultPersistentObjectActions<Person>
            {
                public PersonActions() : base(null!, null!, null!, null!) { }
            }

            public class Car : PersistentObject { }
            public class Person : PersistentObject { }
            """;

        var result = GeneratorHarness.Run(
            "ActionsRegistrationGenerator",
            [source],
            referenceTypes: [typeof(PersistentObject), typeof(DefaultPersistentObjectActions<>)],
            rootNamespace: "TestApp");

        return Verifier.Verify(Render(result));
    }

    [Fact]
    public Task CustomActionsRegistrationGenerator_emits_AddCustomActions_for_one_action()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using MintPlayer.Spark.Abstractions.Actions;

            namespace TestApp.Actions;

            public class ExportAction : ICustomAction
            {
                public Task ExecuteAsync(CustomActionArgs args, CancellationToken ct = default)
                    => Task.CompletedTask;
            }
            """;

        var result = GeneratorHarness.Run(
            "CustomActionsRegistrationGenerator",
            [source],
            referenceTypes: [typeof(ICustomAction)],
            rootNamespace: "TestApp");

        return Verifier.Verify(Render(result));
    }

    [Fact]
    public Task PersistentObjectNamesGenerator_emits_ids_and_names_for_two_models()
    {
        const string knowsSparkSource = """
            namespace MintPlayer.Spark.Actions
            {
                public abstract class DefaultPersistentObjectActions<T> { }
            }
            namespace TestApp
            {
                public class Dummy : MintPlayer.Spark.Actions.DefaultPersistentObjectActions<object> { }
            }
            """;

        static string Model(string id, string name) =>
            $$"""
            {
              "persistentObject": {
                "id": "{{id}}",
                "name": "{{name}}",
                "description": { "en": "{{name}}" },
                "attributes": [
                  { "id": "11111111-1111-1111-1111-111111111111", "name": "Foo" }
                ]
              }
            }
            """;

        var result = GeneratorHarness.Run(
            "PersistentObjectNamesGenerator",
            [knowsSparkSource],
            rootNamespace: "TestApp",
            additionalTexts:
            [
                ("App_Data/Model/Car.json", Model("27768be5-2ff5-4782-8b22-c0e8d163050e", "Car")),
                ("App_Data/Model/Person.json", Model("11111111-2222-3333-4444-555555555555", "Person")),
            ]);

        return Verifier.Verify(Render(result));
    }

    [Fact]
    public Task LibraryLayersGenerator_emits_one_attribute_per_layer_file()
    {
        const string actions = """{ "Edit": { "icon": "pen" } }""";
        const string translations = """{ "greeting": { "en": "Hello" } }""";

        var result = GeneratorHarness.Run(
            "LibraryLayersGenerator",
            sources: [],
            rootNamespace: "TestLib",
            additionalTexts: [("App_Data/translations.json", translations), ("App_Data/actions.json", actions)],
            globalOptions: new Dictionary<string, string> { ["build_property.SparkLibraryAlias"] = "spark" },
            additionalTextOptions: new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["App_Data/translations.json"] = new Dictionary<string, string> { ["build_metadata.AdditionalFiles.SparkLayerPath"] = "translations.json" },
                ["App_Data/actions.json"] = new Dictionary<string, string> { ["build_metadata.AdditionalFiles.SparkLayerPath"] = "actions.json" },
            });

        return Verifier.Verify(Render(result));
    }

    /// <summary>
    /// Pins the whole emitted index / index-entity pair. The structural tests cover individual rules;
    /// this makes any drift in the generated shape a reviewable diff — which matters more here than for
    /// the registration generators, because <c>ProjectionPropertyAnalyzer</c> excludes generated code, so
    /// nothing else validates this output.
    /// </summary>
    [Fact]
    public Task GenerateIndexGenerator_emits_an_index_and_index_entity_pair()
    {
        var source = """
            using MintPlayer.Spark.Abstractions;

            namespace TestApp.Entities;

            [GenerateIndex]
            public class Car
            {
                public string? Id { get; set; }
                public string LicensePlate { get; set; } = string.Empty;
                public string? Model { get; set; }
                public int Year { get; set; }
                [IgnoreProperty] public string? RegistrySyncEtag { get; set; }
            }
            """;

        var result = GeneratorHarness.Run(
            "GenerateIndexGenerator",
            [source],
            referenceTypes: [typeof(GenerateIndexAttribute), typeof(Raven.Client.Documents.Indexes.AbstractIndexCreationTask)],
            rootNamespace: "TestApp",
            outputKind: OutputKind.ConsoleApplication);

        return Verifier.Verify(Render(result));
    }
}
