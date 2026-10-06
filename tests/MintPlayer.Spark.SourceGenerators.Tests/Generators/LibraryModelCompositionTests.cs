using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Composition M4 (D5, D6): a library ships its model types with UUIDv5 ids its generator verifies, and
/// an application gets <c>PersistentObjectIds</c> and <c>PersistentObjectNames</c> for them from the
/// composed model, without a copy of its own. An application file naming a library type is a delta.
/// </summary>
public class LibraryModelCompositionTests
{
    private const string NamesGenerator = "PersistentObjectNamesGenerator";
    private const string LayersGenerator = "LibraryLayersGenerator";
    private const string PathMetadata = "build_metadata.AdditionalFiles.SparkLayerPath";

    /// <summary>UUIDv5 of <c>authorization:SparkUser</c> in the library-model namespace.</summary>
    private const string SparkUserId = "0d3faefa-624c-5135-bca7-652aa9053e0e";

    /// <summary>UUIDv5 of <c>authorization:SparkUser.attributes.UserName</c>.</summary>
    private const string UserNameId = "78698642-85ae-5c15-bcd7-8885dc64c60b";

    private const string KnowsSparkSource = """
        namespace MintPlayer.Spark.Actions
        {
            public abstract class DefaultPersistentObjectActions<T> { }
        }
        namespace TestApp
        {
            public class Car { public string Plate { get; set; } }
            public class CarActions : MintPlayer.Spark.Actions.DefaultPersistentObjectActions<Car> { }
        }
        """;

    private static string SparkUser(string id = SparkUserId, string attributeId = UserNameId) => $$"""
        {
          "persistentObject": {
            "id": "{{id}}",
            "name": "SparkUser",
            "attributes": [ { "id": "{{attributeId}}", "name": "UserName", "dataType": "string" } ]
          }
        }
        """;

    private static MetadataReference AuthorizationLibrary(string json)
        => GeneratorHarness.CompileToMetadataReference(
            "MintPlayer.Spark.Authorization",
            [$"[assembly: MintPlayer.Spark.Abstractions.SparkLayer(\"authorization\", \"model\", \"Model/SparkUser.json\", {SymbolDisplay.FormatLiteral(json, quote: true)})]"],
            [typeof(SparkLayerAttribute)]);

    private static GeneratorRunResult RunApp(MetadataReference library, params (string Path, string Text)[] appFiles)
        => GeneratorHarness.Run(
            NamesGenerator,
            [KnowsSparkSource],
            referenceTypes: [typeof(SparkLayerAttribute)],
            rootNamespace: "TestApp",
            additionalTexts: appFiles,
            outputKind: OutputKind.ConsoleApplication,
            additionalReferences: [library]);

    private static string Generated(GeneratorRunResult result, string hintName)
        => result.GeneratedSources.Single(s => s.HintName == hintName).Source;

    [Fact]
    public void A_library_type_gets_its_id_and_name_in_the_application_without_a_copy()
    {
        var result = RunApp(
            AuthorizationLibrary(SparkUser()),
            ("App_Data/Model/Car.json", """{ "persistentObject": { "id": "27768be5-2ff5-4782-8b22-c0e8d163050e", "name": "Car" } }"""));

        var ids = Generated(result, "PersistentObjectIds.g.cs");
        ids.Should().Contain($"public const string SparkUser = \"{SparkUserId}\";");
        ids.Should().Contain("public const string Car = \"27768be5-2ff5-4782-8b22-c0e8d163050e\";");
        Generated(result, "PersistentObjectNames.g.cs").Should().Contain("public const string SparkUser = \"SparkUser\";");
    }

    [Fact]
    public void An_application_delta_on_a_library_type_keeps_the_library_id()
    {
        var result = RunApp(
            AuthorizationLibrary(SparkUser()),
            ("App_Data/Model/SparkUser.json", """{ "persistentObject": { "name": "SparkUser", "attributes": [ { "name": "UserName", "showedOn": "PersistentObject" } ] } }"""));

        Generated(result, "PersistentObjectIds.g.cs").Should().Contain($"public const string SparkUser = \"{SparkUserId}\";");
    }

    [Fact]
    public void An_application_cannot_change_a_library_id()
    {
        var result = RunApp(
            AuthorizationLibrary(SparkUser()),
            ("App_Data/Model/SparkUser.json", """{ "persistentObject": { "id": "4e13c0de-0000-4000-8000-000000000001", "name": "SparkUser" } }"""));

        var ids = Generated(result, "PersistentObjectIds.g.cs");
        ids.Should().Contain($"public const string SparkUser = \"{SparkUserId}\";");
        ids.Should().NotContain("4e13c0de", "an id is immutable; the run time refuses the file at startup");
    }

    [Fact]
    public void A_library_compilation_gets_no_constants_for_a_referenced_library_type()
    {
        var result = GeneratorHarness.Run(
            NamesGenerator,
            [KnowsSparkSource],
            referenceTypes: [typeof(SparkLayerAttribute)],
            rootNamespace: "TestLib",
            additionalReferences: [AuthorizationLibrary(SparkUser())]);

        result.GeneratedSources.Should().NotContain(s => s.HintName == "PersistentObjectIds.g.cs");
        result.GeneratedSources.Where(s => s.HintName == "PersistentObjectNames.g.cs")
            .Should().OnlyContain(s => !s.Source.Contains("SparkUser"));
    }

    // ── SPARK045 / SPARK046: the library generator verifies the shipped ids ──────────────────────

    private static GeneratorRunResult RunLibrary(string json)
        => GeneratorHarness.Run(
            LayersGenerator,
            sources: [],
            referenceTypes: [typeof(SparkLayerAttribute)],
            additionalTexts: [(@"C:\src\Lib\App_Data\Model\SparkUser.json", json)],
            globalOptions: new Dictionary<string, string> { ["build_property.SparkLibraryAlias"] = "authorization" },
            additionalTextOptions: new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [@"C:\src\Lib\App_Data\Model\SparkUser.json"] = new Dictionary<string, string> { [PathMetadata] = "Model/SparkUser.json" },
            });

    [Fact]
    public void Derived_ids_are_accepted()
        => RunLibrary(SparkUser()).GeneratorDiagnostics.Should().BeEmpty();

    [Fact]
    public void A_minted_id_is_reported_with_the_expected_value()
    {
        var result = RunLibrary(SparkUser(id: "3f7c1d92-8e4a-4b16-9c05-2a7d6e0f4b83"));

        var diagnostic = result.GeneratorDiagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK045");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        var message = diagnostic.GetMessage();
        message.Should().Contain("persistentObject.id").And.Contain(SparkUserId).And.Contain("authorization:SparkUser")
            .And.Contain("3f7c1d92-8e4a-4b16-9c05-2a7d6e0f4b83");
    }

    [Fact]
    public void A_missing_member_id_is_reported_with_the_expected_value()
    {
        var result = RunLibrary("""
            { "persistentObject": { "id": "0d3faefa-624c-5135-bca7-652aa9053e0e", "name": "SparkUser", "attributes": [ { "name": "UserName" } ] } }
            """);

        var diagnostic = result.GeneratorDiagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK045");
        diagnostic.GetMessage().Should().Contain("persistentObject.attributes[UserName].id").And.Contain(UserNameId)
            .And.Contain("authorization:SparkUser.attributes.UserName").And.Contain("missing");
    }

    [Fact]
    public void A_model_layer_that_does_not_parse_is_reported()
    {
        var result = RunLibrary("""{ "persistentObject": { "name": "SparkUser", } }""");

        result.GeneratorDiagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK046");
    }

    [Fact]
    public void A_model_layer_without_a_name_is_reported()
    {
        var result = RunLibrary("""{ "persistentObject": { "id": "0d3faefa-624c-5135-bca7-652aa9053e0e" } }""");

        result.GeneratorDiagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK046");
    }
}
