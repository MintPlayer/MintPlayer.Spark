using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Layering;

/// <summary>
/// Composition M6 (D4): <c>security.json</c> as a keyed set over the rights libraries ship, the
/// group tokens and slot bindings, the guard rails on library layers, and the per-library opt-out.
/// The libraries are fixtures (no library ships rights yet; M9 migrates the real ones), passed to the
/// same <see cref="SparkSecurityFiles.Compose"/> the loader calls.
/// </summary>
public class SparkSecurityLayersTests
{
    private const string Anonymous = "00000000-0000-0000-0000-000000000000";
    private const string SignedIn = "00000000-0000-0000-0000-000000000001";
    private const string Moderators = "4e130000-0000-4000-8000-000000000001";
    private const string Admins = "4e130000-0000-4000-8000-000000000002";

    /// <summary>A library shipping the type <c>Passkeys</c> (its model layer) and a right on it.</summary>
    private static SparkLibrary Authorization(string rights = """[ { "key": "passkeys-read", "resource": "QueryRead/Passkeys", "groupId": "@authenticated" } ]""")
        => new("authorization", "Fixture.Authorization", [],
        [
            new SparkLibraryLayer("model", "Model/Passkeys.json", """{ "persistentObject": { "name": "Passkeys", "attributes": [] } }"""),
            new SparkLibraryLayer("security", "security.json", $$"""{ "rights": {{rights}} }"""),
        ]);

    /// <summary>A library with no model of its own, granting on the pseudo-type it reserves, to its own slot.</summary>
    private static SparkLibrary Moderation(string security = """{ "reservedTargets": [ "Moderation" ], "rights": [ { "key": "review", "resource": "Review/Moderation", "groupId": "moderation:moderators" } ] }""")
        => new("moderation", "Fixture.Moderation", ["Fixture.Authorization"],
        [
            new SparkLibraryLayer("security", "security.json", security),
        ]);

    private static string App(string rights = "[]", string extra = "")
        => $$"""
        {
          "wellKnown": { "anonymous": "{{Anonymous}}", "authenticated": "{{SignedIn}}" },
          "groups": {
            "{{Anonymous}}": "Anonymous visitors",
            "{{SignedIn}}": "Signed-in users",
            "{{Moderators}}": "Moderators",
            "{{Admins}}": "Admins"
          },
          {{extra}}
          "rights": {{rights}}
        }
        """;

    private const string BindModerators = """ "bindings": { "moderation:moderators": [ "Moderators" ] }, """;

    private static SparkSecurityComposition Compose(string app, params SparkLibrary[] libraries)
        => SparkSecurityFiles.Compose(app, libraries, modelTypeNames: ["Passkeys", "Question"]);

    // ---------- the keyed set ----------

    [Fact]
    public void A_library_grant_composes_under_its_alias_with_its_token_resolved()
    {
        var composed = Compose(App(extra: BindModerators), Authorization(), Moderation());

        composed.Problems.Should().BeEmpty();
        var passkeys = composed.Configuration.Rights.Should().ContainSingle(r => r.Key == "authorization:passkeys-read").Which;
        passkeys.Layer.Should().Be("authorization");
        passkeys.Group.Should().Be("@authenticated");
        passkeys.GroupId.Should().Be(Guid.Parse(SignedIn), "@authenticated resolves through wellKnown");

        var review = composed.Configuration.Rights.Should().ContainSingle(r => r.Key == "moderation:review").Which;
        review.GroupId.Should().Be(Guid.Parse(Moderators), "a slot resolves through bindings, here by the group's name");
    }

    [Fact]
    public void The_application_keys_its_own_rights_and_may_name_groups_by_token()
    {
        var composed = Compose(App("""[ { "key": "c1", "resource": "QueryRead/Question", "groupId": "@anonymous" }, { "key": "c2", "resource": "Read/Question", "groupId": "4e130000-0000-4000-8000-000000000002" } ]"""));

        composed.Problems.Should().BeEmpty();
        composed.Configuration.Rights.Select(r => (r.Key, r.GroupId, r.Layer)).Should().BeEquivalentTo(
            [("c1", Guid.Parse(Anonymous), (string?)null), ("c2", Guid.Parse(Admins), (string?)null)]);
    }

    [Fact]
    public void The_application_removes_a_library_grant_by_key()
    {
        var composed = Compose(App("""[ { "key": "authorization:passkeys-read", "$remove": true } ]"""), Authorization());

        composed.Problems.Should().BeEmpty();
        composed.Configuration.Rights.Should().BeEmpty();
    }

    [Fact]
    public void Removing_a_key_no_library_ships_is_refused()
    {
        // A renamed library grant would otherwise come back unnoticed behind a removal that removes nothing.
        var composed = Compose(App("""[ { "key": "authorization:passkey-read", "$remove": true } ]"""), Authorization());

        composed.Problems.Should().ContainSingle().Which.Should().Contain("removes 'authorization:passkey-read', which 'authorization' does not ship");
    }

    [Fact]
    public void The_application_never_edits_a_library_grant()
    {
        var composed = Compose(App("""[ { "key": "authorization:passkeys-read", "resource": "QueryRead/Passkeys", "groupId": "@anonymous" } ]"""), Authorization());

        composed.Problems.Should().ContainSingle().Which.Should().Contain("never changes a library's grant");
    }

    [Fact]
    public void A_key_with_a_colon_must_name_a_referenced_library()
    {
        var composed = Compose(App("""[ { "key": "billing:invoices-read", "$remove": true } ]"""), Authorization());

        composed.Problems.Should().ContainSingle().Which.Should().Contain("no referenced library has the alias 'billing'");
    }

    [Fact]
    public void One_layer_stating_a_key_twice_is_refused()
    {
        var act = () => Compose(App("""[ { "key": "c1", "resource": "Read/Question", "groupId": "@anonymous" }, { "key": "c1", "resource": "Query/Question", "groupId": "@anonymous" } ]"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*'c1' twice*");
    }

    [Fact]
    public void A_right_without_a_key_is_refused()
    {
        var act = () => Compose(App("""[ { "resource": "Read/Question", "groupId": "@anonymous" } ]"""));

        act.Should().Throw<InvalidOperationException>().WithMessage("*string 'key'*");
    }

    // ---------- opt-out ----------

    [Fact]
    public void Opting_out_of_a_library_makes_its_rights_inert_but_visible()
    {
        var composed = Compose(App(extra: """ "libraries": { "authorization": false }, """), Authorization());

        composed.Problems.Should().BeEmpty();
        composed.Configuration.Rights.Should().BeEmpty("a switched-off library grants nothing");
        var inert = composed.Configuration.InertRights.Should().ContainSingle().Which;
        inert.Key.Should().Be("authorization:passkeys-read");
        inert.Group.Should().Be("@authenticated");
        inert.Layer.Should().Be("authorization");
    }

    [Fact]
    public void Opting_in_explicitly_changes_nothing()
    {
        var composed = Compose(App(extra: """ "libraries": { "authorization": true }, """), Authorization());

        composed.Problems.Should().BeEmpty();
        composed.Configuration.Rights.Should().ContainSingle(r => r.Key == "authorization:passkeys-read");
    }

    [Fact]
    public void Opting_out_of_a_library_that_is_not_referenced_is_refused()
    {
        var composed = Compose(App(extra: """ "libraries": { "authorisation": false }, """), Authorization());

        composed.Problems.Should().ContainSingle().Which.Should().Contain("'authorisation'").And.Contain("referenced: authorization");
    }

    [Fact]
    public void Removing_a_grant_of_a_switched_off_library_is_allowed()
    {
        var composed = Compose(App("""[ { "key": "authorization:passkeys-read", "$remove": true } ]""", """ "libraries": { "authorization": false }, """), Authorization());

        composed.Problems.Should().BeEmpty();
    }

    // ---------- tokens and bindings ----------

    [Fact]
    public void An_unbound_slot_is_refused()
    {
        var composed = Compose(App(), Authorization(), Moderation());

        composed.Problems.Should().ContainSingle().Which.Should().Contain("slot 'moderation:moderators'").And.Contain("does not bind");
    }

    [Fact]
    public void A_token_without_its_well_known_group_is_refused()
    {
        const string app = """
            { "groups": { "4e130000-0000-4000-8000-000000000002": "Admins" }, "rights": [] }
            """;

        var composed = Compose(app, Authorization());

        composed.Problems.Should().ContainSingle().Which.Should().Contain("'@authenticated'").And.Contain("wellKnown");
    }

    [Fact]
    public void An_unknown_token_is_refused()
    {
        var composed = Compose(App("""[ { "key": "c1", "resource": "Read/Question", "groupId": "@everyone" } ]"""));

        composed.Problems.Should().ContainSingle().Which.Should().Contain("'@everyone', which is not a token");
    }

    [Fact]
    public void A_binding_to_a_group_that_does_not_exist_is_refused()
    {
        var composed = Compose(App(extra: """ "bindings": { "moderation:moderators": [ "Moderatorz" ] }, """), Authorization(), Moderation());

        composed.Problems.Should().Contain(p => p.Contains("binds 'moderation:moderators' to 'Moderatorz'"));
    }

    [Fact]
    public void A_binding_of_a_library_that_is_not_referenced_is_refused()
    {
        var composed = Compose(App(extra: """ "bindings": { "billing:clerks": [ "Admins" ] }, """), Authorization());

        composed.Problems.Should().ContainSingle().Which.Should().Contain("no referenced library has the alias 'billing'");
    }

    [Fact]
    public void A_slot_bound_to_two_groups_grants_both_by_id_or_name()
    {
        var composed = Compose(App(extra: $$""" "bindings": { "moderation:moderators": [ "Moderators", "{{Admins}}" ] }, """), Authorization(), Moderation());

        composed.Problems.Should().BeEmpty();
        composed.Configuration.Rights.Where(r => r.Key == "moderation:review").Select(r => r.GroupId)
            .Should().BeEquivalentTo([Guid.Parse(Moderators), Guid.Parse(Admins)]);
    }

    [Fact]
    public void Moderation_resolves_a_privilege_slot_through_the_same_bindings()
    {
        var configuration = Compose(App(extra: BindModerators), Authorization(), Moderation()).Configuration;

        SparkSecurityFiles.ResolveGroup(configuration, "moderation:moderators", out var problem)
            .Should().BeEquivalentTo([Guid.Parse(Moderators)]);
        problem.Should().BeNull();
        SparkSecurityFiles.ResolveGroup(configuration, "moderation:voters", out problem).Should().BeNull();
        problem.Should().Contain("does not bind");
    }

    // ---------- guard rails ----------

    [Fact]
    public void A_library_may_not_deny()
    {
        var composed = Compose(App(), Authorization("""[ { "key": "no-delete", "resource": "Delete/Passkeys", "groupId": "@authenticated", "isDenied": true } ]"""));

        composed.Problems.Should().ContainSingle().Which.Should().Contain("authorization:").And.Contain("may only grant");
    }

    [Fact]
    public void A_library_may_not_mark_a_right_important()
    {
        var composed = Compose(App(), Authorization("""[ { "key": "read", "resource": "Read/Passkeys", "groupId": "@authenticated", "isImportant": true } ]"""));

        composed.Problems.Should().ContainSingle().Which.Should().Contain("important");
    }

    [Fact]
    public void A_library_may_grant_only_on_what_it_ships()
    {
        var composed = Compose(App(), Authorization("""[ { "key": "questions", "resource": "QueryRead/Question", "groupId": "@authenticated" } ]"""));

        composed.Problems.Should().ContainSingle().Which.Should().Contain("on 'Question', which it does not ship");
    }

    [Fact]
    public void An_attribute_right_is_judged_by_its_type()
    {
        var composed = Compose(App(), Authorization("""[ { "key": "name", "resource": "Read/Passkeys/Name", "groupId": "@authenticated" } ]"""));

        composed.Problems.Should().BeEmpty();
    }

    [Fact]
    public void A_library_may_not_name_a_group_by_id()
    {
        var composed = Compose(App(), Authorization($$"""[ { "key": "read", "resource": "Read/Passkeys", "groupId": "{{Admins}}" } ]"""));

        composed.Problems.Should().ContainSingle().Which.Should().Contain("never by id");
    }

    [Fact]
    public void A_library_may_not_name_another_librarys_slot()
    {
        var composed = Compose(App(extra: BindModerators), Authorization("""[ { "key": "read", "resource": "Read/Passkeys", "groupId": "moderation:moderators" } ]"""), Moderation());

        composed.Problems.Should().ContainSingle().Which.Should().Contain("one of its own slots ('authorization:<slot>')");
    }

    [Fact]
    public void A_library_may_not_state_what_belongs_to_the_application()
    {
        var composed = Compose(App(), Moderation("""{ "groups": { "4e130000-0000-4000-8000-000000000009": "Hackers" }, "rights": [] }"""));

        composed.Problems.Should().ContainSingle().Which.Should().Contain("states 'groups'");
    }

    [Fact]
    public void A_library_writes_its_keys_without_the_alias_and_never_removes()
    {
        var composed = Compose(App(), Authorization("""[ { "key": "authorization:read", "resource": "Read/Passkeys", "groupId": "@authenticated" }, { "key": "other", "$remove": true } ]"""));

        composed.Problems.Should().HaveCount(2);
        composed.Problems.Should().Contain(p => p.Contains("without the alias"));
        composed.Problems.Should().Contain(p => p.Contains("only grants"));
    }

    [Fact]
    public void A_reserved_target_cannot_claim_a_model_type()
    {
        // Otherwise a library could declare an application's type "reserved" and grant on it.
        var composed = Compose(App(), Moderation("""{ "reservedTargets": [ "Question" ], "rights": [ { "key": "q", "resource": "Read/Question", "groupId": "@anonymous" } ] }"""));

        composed.Problems.Should().Contain(p => p.Contains("reserved target 'Question', which is a model type"));
        composed.Problems.Should().Contain(p => p.Contains("which it does not ship"));
    }

    // ---------- the loader and the posture ----------

    [Fact]
    public void The_loader_refuses_startup_with_every_problem()
    {
        var root = Directory.CreateTempSubdirectory("spark-security-layers-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "App_Data"));
            File.WriteAllText(Path.Combine(root, "App_Data", "security.json"),
                App("""[ { "key": "c1", "resource": "Read/Question", "groupId": "@everyone" }, { "key": "c2", "resource": "Query/Question", "groupId": "moderation:voters" } ]"""));

            var environment = Substitute.For<IHostEnvironment>();
            environment.ContentRootPath.Returns(root);
            using var loader = new SecurityConfigurationLoader(environment, NullLogger<SecurityConfigurationLoader>.Instance, Substitute.For<IModelLoader>());

            var act = () => loader.GetConfiguration();

            act.Should().Throw<SparkSecurityConfigurationException>()
                .Which.Message.Should().Contain("'@everyone'").And.Contain("'moderation:voters'");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_posture_lists_every_right_with_its_layer_and_the_inert_ones()
    {
        var composed = Compose(
            App($$"""[ { "key": "c1", "resource": "QueryRead/Question", "groupId": "{{Anonymous}}" } ]""", """ "libraries": { "moderation": false }, """),
            Authorization(), Moderation());
        composed.Problems.Should().BeEmpty("a switched-off library's slot needs no binding: it grants nothing");

        var loader = Substitute.For<ISecurityConfigurationLoader>();
        loader.GetConfiguration().Returns(composed.Configuration);
        var posture = new SecurityPostureReporter(loader, Substitute.For<IModelLoader>()).Describe();

        posture.Rights.Should().BeEquivalentTo(
        [
            new SecurityPostureRow("Anonymous visitors", "grant", "QueryRead/Question", "c1", "app", Anonymous: true),
            new SecurityPostureRow("Signed-in users (@authenticated)", "grant", "QueryRead/Passkeys", "authorization:passkeys-read", "authorization"),
        ]);
        posture.Inert.Should().BeEquivalentTo([new SecurityPostureRow("moderation:moderators", "grant", "Review/Moderation", "moderation:review", "moderation")]);
        posture.AnonymouslyReachable.Should().BeEquivalentTo(["Query/Question", "Read/Question"]);
    }
}
