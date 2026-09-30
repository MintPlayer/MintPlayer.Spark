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

    /// <summary>
    /// The assemblies whose [assembly: SparkReservedActions] declarations the analyzer reads as the verbs
    /// Spark asks for (Q2): core's (with the combined verbs) and Moderation's — what an app referencing
    /// Spark and Moderation compiles against.
    /// </summary>
    internal static readonly Type[] ReservedVerbSources =
        [typeof(MintPlayer.Spark.Abstractions.Authorization.SparkCoreActions), typeof(MintPlayer.Spark.Moderation.ModerationRights)];

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
            referenceTypes: ReservedVerbSources,
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

    // ---------- SPARK014: the attribute-level right validator (M2c-1) ----------

    [Theory]
    [InlineData("Edit/Person/Brand")]
    [InlineData("queryread/person/brand")]
    [InlineData("QueryReadEditNew/Person/Brand")]
    [InlineData("EditNew/Person/Brand")]
    public async Task A_valid_attribute_right_is_not_reported(string resource)
    {
        (await RunAsync(resource)).Should().BeEmpty();
    }

    /// <summary>
    /// Every shape the host refuses at startup, reported at build as an error with the reason.
    /// </summary>
    [Theory]
    [InlineData("Edit/Person/Salary", "does not declare")]
    [InlineData("Delete/Person/Brand", "no attribute-level form")]
    [InlineData("EditNewDelete/Person/Brand", "no attribute-level form")]
    [InlineData("QueryReadEditNewDelete/Person/Brand", "no attribute-level form")]
    [InlineData("Approve/Person/Brand", "no attribute-level form")]
    [InlineData("Edit/Persno/Brand", "not a persistent object")]
    [InlineData("Read/LookupReferences/Brand", "not a persistent object")]
    [InlineData("Edit/Person/Brand/Name", "never with a longer path")]
    public async Task An_invalid_attribute_right_is_an_error(string resource, string reason)
    {
        var diagnostics = await RunAsync(resource);

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK014");
        diagnostic.Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain(reason);
    }

    /// <summary>
    /// The model is one build behind: a property added in this build is an attribute the next
    /// synchronize writes, so the CLR type's properties count too — the build synchronize needs must
    /// not be blocked by the model it has yet to write.
    /// </summary>
    [Fact]
    public async Task An_attribute_the_CLR_type_declares_but_the_model_does_not_yet_is_not_reported()
    {
        const string source = """
            namespace App.Entities
            {
                public class Person
                {
                    public string Brand { get; set; }
                    public decimal Salary { get; set; }
                }
            }
            """;

        (await RunAsync("Edit/Person/Salary", source: source)).Should().BeEmpty();
    }

    /// <summary>
    /// The latent false negative: the old name regex matched every <c>"name"</c> in a model file, so
    /// an attribute name (Brand) counted as a known TYPE target.
    /// </summary>
    [Fact]
    public async Task An_attribute_name_is_not_a_known_type_target()
    {
        var diagnostics = await RunAsync("QueryRead/Brand");

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK012");
    }

    // ---------- SPARK024: the stale-deny warning ----------

    private const string SongModelJson = """
        {
          "persistentObject": {
            "id": "33333333-3333-3333-3333-333333333333",
            "name": "Song",
            "tabs": [ { "id": "44444444-4444-4444-4444-444444444444", "name": "General" } ],
            "attributes": [
              { "name": "Title" },
              { "name": "Lyrics" },
              { "name": "Genre" }
            ]
          }
        }
        """;

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunRightsAsync(params (string Resource, bool Denied)[] rights)
    {
        var entries = string.Join(",\n", rights.Select((r, i) =>
            $$"""{ "id": "22222222-2222-2222-2222-00000000000{{i}}", "resource": "{{r.Resource}}", "groupId": "00000000-0000-0000-0000-000000000001", "isDenied": {{(r.Denied ? "true" : "false")}} }"""));
        var security = $$"""
            {
              "wellKnown": { "authenticated": "00000000-0000-0000-0000-000000000001" },
              "groups": { "00000000-0000-0000-0000-000000000001": { "en": "Signed-in users" } },
              "rights": [
                {{entries}}
              ]
            }
            """;

        return GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            ["class Placeholder { }"],
            referenceTypes: ReservedVerbSources,
            additionalTexts:
            [
                ("C:\\app\\App_Data\\security.json", security),
                ("C:\\app\\App_Data\\Model\\Song.json", SongModelJson),
            ]);
    }

    [Fact]
    public async Task A_partial_attribute_restriction_is_a_warning_naming_what_is_left()
    {
        var diagnostics = await RunRightsAsync(
            ("QueryReadEdit/Song", false), ("Edit/Song/Title", true), ("Edit/Song/Lyrics", true));

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK024");
        diagnostic.Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
        diagnostic.GetMessage().Should().Be(
            "Group 'Signed-in users' restricts Edit on 2 of 3 attributes of 'Song'; 'Genre' is still editable through the type-level right — intended?");
    }

    [Fact]
    public async Task A_restriction_that_mentions_every_attribute_is_not_reported()
    {
        var diagnostics = await RunRightsAsync(
            ("QueryReadEdit/Song", false), ("Edit/Song/Title", true), ("Edit/Song/Lyrics", true), ("Edit/Song/Genre", false));

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_combined_deny_is_reported_per_verb()
    {
        var diagnostics = await RunRightsAsync(("QueryReadEdit/Song", false), ("QueryRead/Song/Lyrics", true));

        diagnostics.Select(d => d.GetMessage()).Should().HaveCount(2)
            .And.Contain(m => m.Contains("restricts Query on 1 of 3") && m.Contains("'Genre', 'Title' are still queryable"))
            .And.Contain(m => m.Contains("restricts Read on 1 of 3"));
    }

    /// <summary>
    /// D3 (#460): the runtime refuses a wildcard at startup, so the build does too — as an error,
    /// and as the only diagnostic, since judging a wildcard's halves as unknown names would only
    /// bury the real message.
    /// </summary>
    [Theory]
    [InlineData("*/Person")]
    [InlineData("Query/*")]
    [InlineData("*/*")]
    public async Task A_wildcard_right_is_an_error(string resource)
    {
        var diagnostics = await RunAsync(resource);

        var diagnostic = diagnostics.Should().ContainSingle().Which;
        diagnostic.Id.Should().Be("SPARK021");
        diagnostic.Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("combined action");
    }

    // ---------- the false positives it must NOT report ----------

    [Theory]
    [InlineData("Query/Person")]
    [InlineData("Read/Person")]
    [InlineData("QueryReadEditNewDelete/Person")]
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
    /// The Moderation package's rights (#460 M12) are asked for in code through <c>IPermissionService</c>,
    /// never through a <c>[SparkAuthorize]</c> the analyzer could harvest, and <c>Moderation</c> is the
    /// package's own target with no model file. QnA (M13), the first app to grant them, got a SPARK011
    /// per right before the list knew them.
    /// </summary>
    [Theory]
    [InlineData("Vote/Person")]
    [InlineData("Downvote/Person")]
    [InlineData("Flag/Person")]
    [InlineData("Lock/Person")]
    [InlineData("Review/Moderation")]
    [InlineData("Suspend/Moderation")]
    [InlineData("Audit/Moderation")]
    public async Task A_moderation_right_is_not_reported(string resource)
    {
        (await RunAsync(resource)).Should().BeEmpty();
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
            referenceTypes: ReservedVerbSources,
            additionalTexts: [("C:\\app\\App_Data\\security.json", SecurityJson("QueryRead/Anything"))]);

        diagnostics.Should().BeEmpty();
    }

    /// <summary>
    /// Q2: the verbs Spark asks for are read from the referenced assemblies' reserved-verb
    /// declarations, not a hand-kept list — so a package's verb is known where the package is
    /// referenced, and unknown where it is not.
    /// </summary>
    [Fact]
    public async Task A_verb_declared_by_a_referenced_package_is_not_reported()
    {
        var package = GeneratorHarness.CompileToMetadataReference(
            "Acme.Spark.Package",
            [ReservedActionNameAnalyzerTests.StubPackageSource],
            [typeof(MintPlayer.Spark.Abstractions.Authorization.SparkReservedActionsAttribute)]);

        var diagnostics = await GeneratorHarness.RunAnalyzerAsync(
            AnalyzerName,
            ["class Placeholder { }"],
            referenceTypes: ReservedVerbSources,
            additionalTexts:
            [
                ("C:\\app\\App_Data\\security.json", SecurityJson("Frobnicate/Person")),
                ("C:\\app\\App_Data\\Model\\Person.json", ModelJson),
            ],
            additionalReferences: [package]);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task A_verb_of_a_package_that_is_not_referenced_is_reported()
    {
        // Restore is SoftDelete's; this compilation references core and Moderation only.
        var diagnostics = await RunAsync("Restore/Person");

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("SPARK011");
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
