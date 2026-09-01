using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Authorization;

/// <summary>
/// #298 — what <c>security.json</c> is refused for, and why refusing beats accepting.
/// <para>
/// These all throw rather than warn, and the line is: <b>malformed configuration means the file does
/// not say what its author thinks it says</b>. A merely permissive posture — a real anonymous grant —
/// is a policy decision an application is entitled to make, and is logged by the startup summary
/// instead.
/// </para>
/// </summary>
public class SecurityConfigurationValidatorTests
{
    private static readonly Guid AnonymousId = Guid.Parse("00000000-0000-0000-0000-000000000000");
    private static readonly Guid AuthenticatedId = Guid.Parse("a1b2c3d4-0000-0000-0000-00000000000f");
    private static readonly Guid AdminsId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static SecurityConfiguration Config(
        Dictionary<Guid, string>? groups = null,
        Dictionary<string, string>? wellKnown = null,
        params Right[] rights)
        => new()
        {
            Groups = (groups ?? []).ToDictionary(g => g.Key.ToString(), g => TranslatedString.Create(g.Value)),
            WellKnown = wellKnown,
            Rights = [.. rights],
        };

    private static Dictionary<string, string> Migrated() => new()
    {
        ["anonymous"] = AnonymousId.ToString(),
        ["authenticated"] = AuthenticatedId.ToString(),
    };

    private static Dictionary<Guid, string> MigratedGroups() => new()
    {
        [AnonymousId] = "Public",
        [AuthenticatedId] = "Signed-in users",
    };

    [Fact]
    public void An_unmigrated_Everyone_group_fails_with_migration_text()
    {
        var config = Config(groups: new() { [AnonymousId] = "Everyone" });

        var act = () => SecurityConfigurationValidator.Validate(config);

        act.Should().Throw<SparkSecurityConfigurationException>()
            .WithMessage("*wellKnown*")
            // The instruction that prevents the obvious wrong migration: a deleted grant denies
            // signed-in users too, because type-level rights gate row rules.
            .And.Which.Message.Should().Contain("MOVE it to the authenticated group");
    }

    [Fact]
    public void A_group_named_Everyone_is_fine_once_the_roles_are_declared_by_id()
    {
        // Once roles come from ids, a display name carries no meaning — which is the point of the
        // change. Continuing to police the name afterwards would contradict it.
        var config = Config(
            groups: new() { [AnonymousId] = "Everyone", [AuthenticatedId] = "Signed-in users" },
            wellKnown: Migrated());

        var act = () => SecurityConfigurationValidator.Validate(config);

        act.Should().NotThrow();
    }

    [Fact]
    public void An_unknown_well_known_key_is_rejected()
    {
        var config = Config(
            groups: MigratedGroups(),
            wellKnown: new() { ["everyone"] = AnonymousId.ToString() });

        var act = () => SecurityConfigurationValidator.Validate(config);

        act.Should().Throw<SparkSecurityConfigurationException>().WithMessage("*unknown well-known group*");
    }

    [Fact]
    public void A_well_known_group_pointing_at_no_declared_group_is_rejected()
    {
        // Silently the worst case: the role stops applying and every grant to it stops taking
        // effect, with nothing to indicate it.
        var config = Config(
            groups: new() { [AdminsId] = "Admins" },
            wellKnown: new() { ["anonymous"] = AnonymousId.ToString() });

        var act = () => SecurityConfigurationValidator.Validate(config);

        act.Should().Throw<SparkSecurityConfigurationException>().WithMessage("*no group with that id is declared*");
    }

    [Fact]
    public void A_well_known_id_that_is_not_a_guid_is_rejected()
    {
        var config = Config(
            groups: MigratedGroups(),
            wellKnown: new() { ["anonymous"] = "the-public" });

        var act = () => SecurityConfigurationValidator.Validate(config);

        act.Should().Throw<SparkSecurityConfigurationException>().WithMessage("*not a group id*");
    }

    [Fact]
    public void One_group_cannot_be_both_roles()
    {
        var config = Config(
            groups: new() { [AnonymousId] = "Public" },
            wellKnown: new()
            {
                ["anonymous"] = AnonymousId.ToString(),
                ["authenticated"] = AnonymousId.ToString(),
            });

        var act = () => SecurityConfigurationValidator.Validate(config);

        act.Should().Throw<SparkSecurityConfigurationException>().WithMessage("*cannot be both*");
    }

    /// <summary>
    /// This used to be rejected, because expansion was grant-only and such a denial matched the
    /// literal string and therefore denied nothing. Expansion is symmetric now, so the shape the
    /// rule refused is the shape that works — keeping the rule would refuse valid files.
    /// </summary>
    [Fact]
    public void A_combined_action_in_a_denial_is_accepted()
    {
        var config = Config(
            groups: MigratedGroups(),
            wellKnown: Migrated(),
            new Right { Id = Guid.NewGuid(), GroupId = AdminsId, Resource = "EditNewDelete/Person", IsDenied = true });

        var act = () => SecurityConfigurationValidator.Validate(config);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_combined_action_in_a_grant_is_accepted()
    {
        var config = Config(
            groups: MigratedGroups(),
            wellKnown: Migrated(),
            new Right { Id = Guid.NewGuid(), GroupId = AdminsId, Resource = "EditNewDelete/Person" });

        var act = () => SecurityConfigurationValidator.Validate(config);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Person")]
    [InlineData("/Person")]
    [InlineData("Read/")]
    public void A_malformed_resource_is_rejected(string resource)
    {
        var config = Config(
            groups: MigratedGroups(),
            wellKnown: Migrated(),
            new Right { Id = Guid.NewGuid(), GroupId = AdminsId, Resource = resource });

        var act = () => SecurityConfigurationValidator.Validate(config);

        act.Should().Throw<SparkSecurityConfigurationException>().WithMessage("*action*/*target*");
    }

    [Fact]
    public void A_duplicated_right_id_is_rejected()
    {
        // Found in the HR demo. Nothing reads Right.Id today, so it is currently harmless — which is
        // exactly why it should be caught before something does.
        var id = Guid.Parse("b0000002-0000-0000-0000-000000000012");
        var config = Config(
            groups: MigratedGroups(),
            wellKnown: Migrated(),
            new Right { Id = id, GroupId = AdminsId, Resource = "ReadEditNew/Person" },
            new Right { Id = id, GroupId = AdminsId, Resource = "Replicate/Companies" });

        var act = () => SecurityConfigurationValidator.Validate(config);

        act.Should().Throw<SparkSecurityConfigurationException>().WithMessage("*two rights with id*");
    }

    [Fact]
    public void A_configuration_with_no_well_known_block_and_no_Everyone_is_accepted()
    {
        var config = Config(
            groups: new() { [AdminsId] = "Admins" },
            wellKnown: null,
            new Right { Id = Guid.NewGuid(), GroupId = AdminsId, Resource = "QueryRead/Person" });

        var act = () => SecurityConfigurationValidator.Validate(config);

        act.Should().NotThrow("declaring no well-known groups is a valid configuration, not a broken one");
    }
}
