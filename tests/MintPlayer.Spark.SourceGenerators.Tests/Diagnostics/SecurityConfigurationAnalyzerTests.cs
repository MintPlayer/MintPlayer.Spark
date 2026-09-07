using MintPlayer.Spark.SourceGenerators.Tests._Infrastructure;

namespace MintPlayer.Spark.SourceGenerators.Tests.Diagnostics;

/// <summary>
/// SPARK011-014: rights that load cleanly and grant nothing.
/// </summary>
/// <remarks>
/// <para>
/// Half of these exist because of what happened on the analyzer's first run against the real
/// applications: it reported eleven findings, and the majority were its own false positives. A
/// security analyzer that cries wolf gets suppressed, and a suppressed analyzer is worse than none —
/// so the negative cases below carry as much weight as the positive ones.
/// </para>
/// <para>
/// The runtime validator stays authoritative for anything malformed. This only catches the file that
/// is valid and wrong.
/// </para>
/// </remarks>
public class SecurityConfigurationAnalyzerTests
{
    // The harness matches on the SIMPLE type name.
    private const string AnalyzerName = "SecurityConfigurationAnalyzer";

    private const string ModelJson = """
        {
          "persistentObject": {
            "id": "11111111-1111-1111-1111-111111111111",
            "name": "Person",
            "clrType": "App.Entities.Person",
            "attributes": [
              { "name": "Brand", "lookupReferenceType": "CarBrand" }
            ]
          }
        }
        """;

    private static string SecurityJson(string resource, string groupId = "00000000-0000-0000-0000-000000000001") => $$"""
        {
          "wellKnown": {
            "authenticated": "00000000-0000-0000-0000-000000000001"
          },
          "groups": {
            "00000000-0000-0000-0000-000000000001": { "en": "Signed-in users" }
          },
          "rights": [
            { "id": "22222222-2222-2222-2222-222222222222", "resource": "{{resource}}", "groupId": "{{groupId}}" }
          ]
        }
        """;

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(
        string resource, string groupId = "00000000-0000-0000-0000-000000000001", string source = "class Placeholder { }")
        => GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            [source],
            additionalTexts:
            [
                ("C:\\app\\App_Data\\security.json", SecurityJson(resource, groupId)),
                ("C:\\app\\App_Data\\Model\\Person.json", ModelJson),
            ]);

    // ---------- the findings ----------

    [Fact]
    public async Task A_misspelled_action_is_reported()
    {
        var diagnostics = await RunAsync("Raed/Person");

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK011");
    }

    [Fact]
    public async Task A_misspelled_target_is_reported()
    {
        var diagnostics = await RunAsync("QueryRead/Persno");

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK012");
    }

    [Fact]
    public async Task A_right_granted_to_an_undeclared_group_is_reported()
    {
        var diagnostics = await RunAsync("QueryRead/Person", groupId: "99999999-9999-9999-9999-999999999999");

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK013");
    }

    /// <summary>
    /// Three segments parse and can never match: the resource splits on the FIRST slash, so the
    /// target becomes "Person/Salary". Per-attribute rights are not expressed this way, and someone
    /// reaching for that syntax gets silence instead of an error.
    /// </summary>
    [Fact]
    public async Task A_three_segment_resource_is_reported()
    {
        var diagnostics = await RunAsync("Edit/Person/Salary");

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK014");
    }

    // ---------- the false positives it must NOT report ----------

    [Theory]
    [InlineData("Query/Person")]
    [InlineData("Read/Person")]
    [InlineData("QueryReadEditNewDelete/Person")]
    [InlineData("*/Person")]
    [InlineData("Query/*")]
    public async Task A_correct_right_is_not_reported(string resource)
    {
        (await RunAsync(resource)).Should().BeEmpty();
    }

    /// <summary>
    /// A <c>[SparkAuthorize]</c> resource is invented by the application: the action is a verb no
    /// model declares and the target names a controller's surface rather than a persistent object.
    /// Both looked like typos to the first version of this analyzer, which reported seven of them
    /// against the production app.
    /// </summary>
    [Fact]
    public async Task A_SparkAuthorize_resource_is_not_reported()
    {
        const string Controller = """
            namespace MintPlayer.Spark.Services
            {
                public sealed class SparkAuthorizeAttribute : System.Attribute
                {
                    public SparkAuthorizeAttribute(string action, string target) { }
                }
            }

            [MintPlayer.Spark.Services.SparkAuthorize("Manage", "UploadToken")]
            public class TokensController { }
            """;

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            [Controller],
            additionalTexts:
            [
                ("C:\\app\\App_Data\\security.json", SecurityJson("Manage/UploadToken")),
                ("C:\\app\\App_Data\\Model\\Person.json", ModelJson),
            ]);

        diagnostics.Should().BeEmpty(
            "the action and the target are both declared by the attribute, in the compilation");
    }

    /// <summary>
    /// A lookup reference type is a legitimate target with no model file of its own — it is named by
    /// a <c>lookupReferenceType</c> on an attribute. Two demo apps grant rights on theirs.
    /// </summary>
    [Fact]
    public async Task A_lookup_reference_target_is_not_reported()
    {
        (await RunAsync("QueryRead/CarBrand")).Should().BeEmpty();
    }

    /// <summary>
    /// Replicate targets a RavenDB COLLECTION, not a model type — conventionally the plural. Judging
    /// it against model names reported every correct replication right in two demo apps.
    /// </summary>
    [Fact]
    public async Task A_replication_right_naming_a_collection_is_not_reported()
    {
        (await RunAsync("Replicate/People")).Should().BeEmpty();
    }

    /// <summary>
    /// With no model files visible there is nothing to compare a target against, and reporting every
    /// one as unknown would be far worse than reporting none.
    /// </summary>
    [Fact]
    public async Task Nothing_is_reported_about_targets_when_the_model_is_not_wired()
    {
        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            ["class Placeholder { }"],
            additionalTexts: [("C:\\app\\App_Data\\security.json", SecurityJson("QueryRead/Anything"))]);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>A project with no security.json at all gets silence, not a diagnostic.</summary>
    [Fact]
    public async Task Nothing_is_reported_when_security_json_is_not_wired()
    {
        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName, ["class Placeholder { }"], additionalTexts: []);

        diagnostics.Should().BeEmpty();
    }
}
