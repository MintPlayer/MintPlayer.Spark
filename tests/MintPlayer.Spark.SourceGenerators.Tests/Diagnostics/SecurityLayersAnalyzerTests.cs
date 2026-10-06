using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// Composition M6 (D4): library rights reported at build time, through the composer the host runs at
/// startup. SPARK047 in the library's own build (the guard rails), SPARK047–049 in the application's
/// (the composed set: a referenced library breaking a guard rail, a token or slot that resolves to no
/// group, an edit of a library grant).
/// </summary>
public class SecurityLayersAnalyzerTests
{
    private const string AnalyzerName = "SecurityConfigurationAnalyzer";

    /// <summary>A compiled library shipping the type <c>Passkeys</c> and <paramref name="rights"/>, as its generator embeds them.</summary>
    private static MetadataReference Library(string rights)
    {
        static string Layer(string kind, string path, string json)
            => $"[assembly: MintPlayer.Spark.Abstractions.SparkLayer(\"authorization\", {SymbolDisplay.FormatLiteral(kind, quote: true)}, {SymbolDisplay.FormatLiteral(path, quote: true)}, {SymbolDisplay.FormatLiteral(json, quote: true)})]";

        return GeneratorHarness.CompileToMetadataReference(
            "Fixture.Authorization" + Guid.NewGuid().ToString("N"),
            [
                Layer("model", "Model/Passkeys.json", """{ "persistentObject": { "name": "Passkeys", "attributes": [] } }"""),
                Layer("security", "security.json", $$"""{ "rights": {{rights}} }"""),
            ],
            [typeof(SparkLayerAttribute)]);
    }

    private const string Granting = """[ { "key": "passkeys-read", "resource": "QueryRead/Passkeys", "groupId": "@authenticated" } ]""";

    private static string App(string rights = "[]", string extra = "") => $$"""
        {
          "wellKnown": { "authenticated": "00000000-0000-0000-0000-000000000001" },
          "groups": { "00000000-0000-0000-0000-000000000001": "Signed-in users" },
          {{extra}}
          "rights": {{rights}}
        }
        """;

    private static Task<IReadOnlyList<Diagnostic>> RunAsync(string app, string libraryRights = Granting)
        => GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            ["class Placeholder { }"],
            referenceTypes: [.. SecurityConfigurationAnalyzerTests.ReservedVerbSources, typeof(SparkLayerAttribute)],
            additionalTexts: [("C:\\app\\App_Data\\security.json", app)],
            additionalReferences: [Library(libraryRights)]);

    [Fact]
    public async Task A_library_grant_that_keeps_to_the_guard_rails_reports_nothing()
    {
        var diagnostics = await RunAsync(App());

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_referenced_library_that_denies_is_reported()
    {
        var diagnostics = await RunAsync(App(), """[ { "key": "x", "resource": "Delete/Passkeys", "groupId": "@authenticated", "isDenied": true } ]""");

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK047");
    }

    [Fact]
    public async Task A_referenced_library_granting_on_a_foreign_type_or_to_a_group_id_is_reported()
    {
        var diagnostics = await RunAsync(App(), """[ { "key": "x", "resource": "Read/Person", "groupId": "00000000-0000-0000-0000-000000000001" } ]""");

        diagnostics.Select(d => d.Id).Should().BeEquivalentTo(["SPARK047", "SPARK047"]);
        diagnostics.Should().Contain(d => d.GetMessage().Contains("on 'Person', which it does not ship"));
        diagnostics.Should().Contain(d => d.GetMessage().Contains("never by id"));
    }

    /// <summary>
    /// Composition M9: a library's own security.json is a layer, judged by LibraryLayersGenerator
    /// (SPARK047), not an application file. Moderation's slots are bound by the app, never by itself.
    /// </summary>
    [Fact]
    public async Task A_library_s_own_security_layer_is_not_judged_as_an_application_file()
    {
        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            ["class Placeholder { }"],
            referenceTypes: [.. SecurityConfigurationAnalyzerTests.ReservedVerbSources, typeof(SparkLayerAttribute)],
            additionalTexts: [("C:\\lib\\App_Data\\security.json",
                """{ "reservedTargets": [ "Moderation" ], "rights": [ { "key": "review", "resource": "Review/Moderation", "groupId": "moderation:moderators" } ] }""")],
            globalOptions: new Dictionary<string, string> { ["build_property.SparkLibraryAlias"] = "moderation" });

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unbound_slot_is_reported()
    {
        var diagnostics = await RunAsync(App("""[ { "key": "c1", "resource": "QueryRead/Passkeys", "groupId": "authorization:account-holders" } ]"""));

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK048");
    }

    [Fact]
    public async Task A_bound_slot_reports_nothing_and_is_not_a_dangling_group()
    {
        var diagnostics = await RunAsync(App(
            """[ { "key": "c1", "resource": "QueryRead/Passkeys", "groupId": "authorization:account-holders" } ]""",
            """ "bindings": { "authorization:account-holders": [ "Signed-in users" ] }, """));

        diagnostics.Should().BeEmpty("a slot is not a group id, so SPARK013 does not judge it either");
    }

    [Fact]
    public async Task Editing_a_library_grant_is_reported_and_removing_it_is_not()
    {
        var edit = await RunAsync(App("""[ { "key": "authorization:passkeys-read", "resource": "QueryRead/Passkeys", "groupId": "00000000-0000-0000-0000-000000000001" } ]"""));
        var remove = await RunAsync(App("""[ { "key": "authorization:passkeys-read", "$remove": true } ]"""));

        edit.Should().ContainSingle().Which.Id.Should().Be("SPARK049");
        remove.Should().BeEmpty();
    }

    [Fact]
    public async Task Opting_out_of_an_unknown_library_is_reported()
    {
        var diagnostics = await RunAsync(App(extra: """ "libraries": { "authorisation": false }, """));

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK049");
    }
}
