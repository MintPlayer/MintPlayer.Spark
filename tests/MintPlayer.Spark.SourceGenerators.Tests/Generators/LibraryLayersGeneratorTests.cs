using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;
using GeneratorRunResult = MintPlayer.Spark.SourceGenerators.Tests._Infrastructure.GeneratorRunResult;

namespace MintPlayer.Spark.SourceGenerators.Tests.Generators;

/// <summary>
/// Composition M3 (D1, D16): one generator compiles a library's App_Data layer files into
/// <c>[assembly: SparkLayer(alias, kind, path, json)]</c>. The alias is required and checked; an
/// executable embeds nothing (S3); and the layered assemblies the library was compiled against are
/// recorded, so the run time can stack it above them.
/// </summary>
public class LibraryLayersGeneratorTests
{
    private const string GeneratorName = "LibraryLayersGenerator";
    private const string PathMetadata = "build_metadata.AdditionalFiles.SparkLayerPath";

    private const string Translations = """
        {
          "greeting": { "en": "Hello", "nl": "Hallo" },
          "farewell": { "en": "Bye", "nl": "Tot ziens" }
        }
        """;

    private const string Actions = """{ "Archive": { "icon": "box", "offset": 2, "refreshOnCompleted": true } }""";

    /// <summary>
    /// Runs the generator over files as the library targets hand them over: each AdditionalFile under
    /// App_Data with its <c>SparkLayerPath</c> metadata. A <see langword="null"/> layer path is a plain
    /// AdditionalFile, as an application's own files are.
    /// </summary>
    private static GeneratorRunResult Run(
        string? alias,
        (string File, string? LayerPath, string Text)[] files,
        OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary,
        params MetadataReference[] references)
    {
        var globalOptions = new Dictionary<string, string>();
        if (alias is not null) globalOptions["build_property.SparkLibraryAlias"] = alias;

        return GeneratorHarness.Run(
            GeneratorName,
            sources: [],
            referenceTypes: [typeof(SparkLayerAttribute)],
            additionalTexts: files.Select(f => (f.File, f.Text)),
            outputKind: outputKind,
            additionalReferences: references,
            globalOptions: globalOptions,
            additionalTextOptions: files
                .Where(f => f.LayerPath is not null)
                .ToDictionary(f => f.File, f => (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { [PathMetadata] = f.LayerPath! }));
    }

    private static Assembly Compile(GeneratorRunResult result)
        => GeneratorHarness.EmitAndLoad(
            "LayersProbe" + Guid.NewGuid().ToString("N"),
            result.GeneratedSources.Select(s => s.Source),
            referenceTypes: [typeof(SparkLayerAttribute)]);

    private static MetadataReference LayeredLibrary(string assemblyName, string alias)
        => GeneratorHarness.CompileToMetadataReference(
            assemblyName,
            [$"[assembly: MintPlayer.Spark.Abstractions.SparkLayer({SymbolDisplay.FormatLiteral(alias, quote: true)}, \"translations\", \"translations.json\", \"{{}}\")]"],
            [typeof(SparkLayerAttribute)]);

    [Fact]
    public void Every_kind_is_embedded_with_its_alias_kind_and_relative_path()
    {
        var result = Run("authorization",
        [
            (@"C:\src\Lib\App_Data\actions.json", "actions.json", Actions),
            (@"C:\src\Lib\App_Data\translations.json", "translations.json", Translations),
            (@"C:\src\Lib\App_Data\Model\SparkUser.json", "Model/SparkUser.json", """{ "persistentObject": { "id": "0d3faefa-624c-5135-bca7-652aa9053e0e", "name": "SparkUser" } }"""),
            (@"C:\src\Lib\App_Data\security.json", "security.json", """{ "rights": [] }"""),
            (@"C:\src\Lib\App_Data\programUnits.json", "programUnits.json", """{ "programUnitGroups": [] }"""),
            (@"C:\src\Lib\App_Data\moderation.json", "moderation.json", """{ "privileges": {} }"""),
        ]);

        result.GeneratorDiagnostics.Should().BeEmpty();
        var layers = Compile(result).GetCustomAttributes<SparkLayerAttribute>().ToList();

        // Emitted in ordinal path order, so "Model/…" sorts before the lower-case files.
        layers.Select(l => (l.Kind, l.Path)).Should().Equal(
            ("model", "Model/SparkUser.json"),
            ("actions", "actions.json"),
            ("moderation", "moderation.json"),
            ("programUnits", "programUnits.json"),
            ("security", "security.json"),
            ("translations", "translations.json"));
        layers.Should().OnlyContain(l => l.Alias == "authorization");
        layers.Single(l => l.Kind == "actions").Json.Should().Be(Actions, "the text is passed through unparsed, numbers and booleans included");
        layers.Should().NotContain(l => l.Path.Contains('\\') || l.Path.Contains(':'), "no machine paths are embedded");
    }

    [Fact]
    public void Plain_additional_files_are_never_embedded()
    {
        var result = Run("authorization", [("App_Data/translations.json", null, Translations), ("App_Data/other.json", null, "{}")]);

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void A_path_no_kind_ships_is_ignored()
    {
        var result = Run("authorization", [("App_Data/culture.json", "culture.json", """{ "languages": ["en"] }""")]);

        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Empty_layer_files_are_skipped()
    {
        var result = Run("authorization", [("App_Data/translations.json", "translations.json", "  ")]);

        result.GeneratedSources.Should().BeEmpty();
        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK041");
    }

    [Fact]
    public void An_executable_embeds_nothing_even_with_layer_files()
    {
        // S3: an application embedding its own translations would be a library layer under itself.
        var result = Run(null, [("App_Data/translations.json", "translations.json", Translations)], OutputKind.ConsoleApplication);

        result.GeneratedSources.Should().BeEmpty();
        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK041");
    }

    [Theory]
    [InlineData(null, "is not set")]
    [InlineData("", "is not set")]
    [InlineData("Authorization", "'Authorization' is not well-formed")]
    [InlineData("my_lib", "'my_lib' is not well-formed")]
    [InlineData("1lib", "'1lib' is not well-formed")]
    [InlineData("my--lib", "'my--lib' is not well-formed")]
    public void A_library_with_layers_and_no_valid_alias_is_SPARK041_and_embeds_nothing(string? alias, string problem)
    {
        var result = Run(alias, [("App_Data/translations.json", "translations.json", Translations)]);

        var diagnostic = result.GeneratorDiagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK041");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain(problem).And.Contain("1 App_Data layer file(s)");
        result.GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void A_library_without_layer_files_needs_no_alias()
    {
        var result = Run(null, []);

        result.GeneratorDiagnostics.Should().BeEmpty();
        result.GeneratedSources.Should().BeEmpty();
    }

    [Theory]
    [InlineData("core")]
    [InlineData("common")]
    [InlineData("lib")]
    public void A_generic_alias_warns_SPARK042_and_still_embeds(string alias)
    {
        var result = Run(alias, [("App_Data/translations.json", "translations.json", Translations)]);

        var diagnostic = result.GeneratorDiagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK042");
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        Compile(result).GetCustomAttributes<SparkLayerAttribute>().Should().ContainSingle();
    }

    [Theory]
    [InlineData("spark")]
    [InlineData("authorization")]
    [InlineData("fleet-tracking2")]
    public void A_specific_alias_is_accepted_silently(string alias)
    {
        var result = Run(alias, [("App_Data/translations.json", "translations.json", Translations)]);

        result.GeneratorDiagnostics.Should().BeEmpty();
        Compile(result).GetCustomAttributes<SparkLayerAttribute>().Single().Alias.Should().Be(alias);
    }

    [Fact]
    public void The_layered_assemblies_the_library_was_compiled_against_are_recorded()
    {
        var result = Run("extension",
            [("App_Data/translations.json", "translations.json", Translations)],
            OutputKind.DynamicallyLinkedLibrary,
            LayeredLibrary("Zeta.Lib", "zeta"),
            LayeredLibrary("MintPlayer.Spark", "spark"),
            GeneratorHarness.CompileToMetadataReference("Plain.Lib", ["public class Plain { }"]));

        var dependencies = Compile(result).GetCustomAttribute<SparkLayerDependenciesAttribute>();

        dependencies.Should().NotBeNull();
        dependencies!.AssemblyNames.Should().Equal("MintPlayer.Spark", "Zeta.Lib");
    }

    [Fact]
    public void A_library_above_no_layered_assembly_records_no_dependencies()
    {
        var result = Run("extension", [("App_Data/translations.json", "translations.json", Translations)]);

        Compile(result).GetCustomAttribute<SparkLayerDependenciesAttribute>().Should().BeNull();
    }

    // ── translations.json checks (SPARK_TRANS_001–004), for every file the compiler sees ────────────

    [Fact]
    public void Schema_and_underscore_comments_are_neither_translations_nor_mixed_namespaces()
    {
        // #264 G-Q12/Q17: the root's $schema and _-prefixed comments sit beside namespaces and inside
        // leaves. Counted as members, the root would be a "mixed leaf/namespace" (SPARK_TRANS_002).
        var translations = """
            {
              "$schema": "../../../../schemas/translations.schema.json",
              "_comment": "Greetings shown on the home page.",
              "greeting": { "_note": "keep it short", "en": "Hello", "nl": "Hallo" }
            }
            """;

        var result = Run("probe", [("App_Data/translations.json", "translations.json", translations)]);

        result.GeneratorDiagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Malformed_json_in_an_application_file_reports_a_diagnostic_and_emits_no_source()
    {
        var result = Run(null, [("App_Data/translations.json", null, "{ not: valid json")], OutputKind.ConsoleApplication);

        result.GeneratedSources.Should().BeEmpty();
        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_TRANS_001");
    }

    [Fact]
    public void Malformed_json_in_a_library_layer_is_reported_too()
    {
        var result = Run("probe", [("App_Data/translations.json", "translations.json", "{ not: valid json")]);

        result.GeneratorDiagnostics.Should().Contain(d => d.Id == "SPARK_TRANS_001");
    }

    private const string PasskeysModel = """{ "persistentObject": { "id": "x", "name": "Passkeys", "attributes": [] } }""";

    [Fact]
    public void A_shipped_security_json_that_keeps_to_the_guard_rails_reports_nothing()
    {
        // Composition M6 (D4): grants only, on the library's own type (its model layer) or reserved
        // target, to a token or its own slot.
        var result = Run("authorization",
        [
            (@"C:\src\Lib\App_Data\Model\Passkeys.json", "Model/Passkeys.json", PasskeysModel),
            (@"C:\src\Lib\App_Data\security.json", "security.json", """
                { "reservedTargets": [ "Account" ],
                  "rights": [ { "key": "passkeys-read", "resource": "QueryRead/Passkeys", "groupId": "@authenticated" },
                              { "key": "account", "resource": "Manage/Account", "groupId": "authorization:account-holders" } ] }
                """),
        ]);

        result.GeneratorDiagnostics.Should().NotContain(d => d.Id == "SPARK047");
    }

    [Fact]
    public void A_shipped_security_json_breaking_a_guard_rail_is_reported_in_the_library_build()
    {
        // Where the author can fix it: a deny, a foreign type, a group by id, another library's slot.
        var result = Run("authorization",
        [
            (@"C:\src\Lib\App_Data\Model\Passkeys.json", "Model/Passkeys.json", PasskeysModel),
            (@"C:\src\Lib\App_Data\security.json", "security.json", """
                { "rights": [
                    { "key": "deny", "resource": "Delete/Passkeys", "groupId": "@authenticated", "isDenied": true },
                    { "key": "foreign", "resource": "Read/Person", "groupId": "@authenticated" },
                    { "key": "by-id", "resource": "Read/Passkeys", "groupId": "00000000-0000-0000-0000-000000000001" },
                    { "key": "other-slot", "resource": "Read/Passkeys", "groupId": "moderation:moderators" } ] }
                """),
        ]);

        result.GeneratorDiagnostics.Where(d => d.Id == "SPARK047").Should().HaveCount(4);
    }
}
