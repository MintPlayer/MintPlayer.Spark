using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authorization;

/// <summary>
/// #298 — the startup summary that makes the anonymous surface visible without reading
/// <c>security.json</c> and reasoning about group resolution.
/// <para>
/// A startup check rather than an analyzer, and that is the mirror image of SPARK004: middleware
/// order is a property of the code and undetectable at runtime, so it ships as a diagnostic; the
/// anonymous surface lives in a hot-reloadable data file that is not in the compilation, so it is
/// trivially computable at runtime and barely computable at build time.
/// </para>
/// </summary>
public class SecurityPostureReporterTests
{
    private static readonly Guid AnonymousId = Guid.Parse("00000000-0000-0000-0000-000000000000");
    private static readonly Guid AdminsId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static SecurityPostureReporter Reporter(SecurityConfiguration config)
    {
        var loader = Substitute.For<ISecurityConfigurationLoader>();
        loader.GetConfiguration().Returns(config);

        return new SecurityPostureReporter(loader);
    }

    private static SecurityConfiguration ConfigWithAnonymousGrants(params string[] resources)
        => new()
        {
            Groups =
            {
                [AnonymousId.ToString()] = TranslatedString.Create("Public"),
                [AdminsId.ToString()] = TranslatedString.Create("Admins"),
            },
            WellKnown = new() { ["anonymous"] = AnonymousId.ToString() },
            Rights =
            [
                .. resources.Select(r => new Right { Id = Guid.NewGuid(), GroupId = AnonymousId, Resource = r }),
                new Right { Id = Guid.NewGuid(), GroupId = AdminsId, Resource = "QueryReadEditNewDelete/Secret" },
            ],
        };

    [Fact]
    public void The_summary_lists_anonymously_reachable_rights()
    {
        var posture = Reporter(ConfigWithAnonymousGrants("QueryRead/Company", "Query/CarBrand")).Describe();

        // Expanded, not literal. Printing "QueryRead/Company" would be one line standing for two
        // rights, and — worse — would leave a right listed that a combined denial takes away.
        posture.AnonymouslyReachable.Should().BeEquivalentTo(
            ["Query/CarBrand", "Query/Company", "Read/Company"]);
        posture.AnonymouslyReachable.Should().NotContain(r => r.EndsWith("/Secret"));
    }

    [Fact]
    public void The_listing_is_stable_regardless_of_declaration_order()
    {
        // The fingerprint is compared against a committed baseline, so a reordering of security.json
        // must not read as a widened surface.
        var a = Reporter(ConfigWithAnonymousGrants("QueryRead/Company", "Query/CarBrand")).Describe();
        var b = Reporter(ConfigWithAnonymousGrants("Query/CarBrand", "QueryRead/Company")).Describe();

        a.Fingerprint.Should().Be(b.Fingerprint);
    }

    [Fact]
    public void The_summary_is_empty_when_nothing_is_anonymous()
    {
        var posture = Reporter(ConfigWithAnonymousGrants()).Describe();

        posture.AnonymouslyReachable.Should().BeEmpty();
        posture.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_config_declaring_no_anonymous_group_reaches_nothing()
    {
        var config = new SecurityConfiguration
        {
            Groups = { [AdminsId.ToString()] = TranslatedString.Create("Admins") },
            Rights = [new Right { Id = Guid.NewGuid(), GroupId = AdminsId, Resource = "QueryRead/Person" }],
        };

        Reporter(config).Describe().AnonymouslyReachable.Should().BeEmpty();
    }

    [Fact]
    public void A_denial_to_the_anonymous_group_is_not_reported_as_reachable()
    {
        var config = ConfigWithAnonymousGrants("QueryRead/Company");
        config.Rights.Add(new Right
        {
            Id = Guid.NewGuid(),
            GroupId = AnonymousId,
            Resource = "Delete/Company",
            IsDenied = true,
        });

        Reporter(config).Describe().AnonymouslyReachable.Should()
            .BeEquivalentTo(["Query/Company", "Read/Company"]);
    }

    /// <summary>
    /// The reason the reporter expands rather than listing literals: a combined denial takes away
    /// what a combined grant gave, and a literal listing would still show it as reachable.
    /// </summary>
    [Fact]
    public void A_combined_denial_removes_every_action_it_names()
    {
        var config = ConfigWithAnonymousGrants("QueryReadEditNewDelete/Company");
        config.Rights.Add(new Right
        {
            Id = Guid.NewGuid(),
            GroupId = AnonymousId,
            Resource = "EditNewDelete/Company",
            IsDenied = true,
        });

        Reporter(config).Describe().AnonymouslyReachable.Should()
            .BeEquivalentTo(["Query/Company", "Read/Company"]);
    }

    /// <summary>
    /// Replaces the DefaultBehavior=AllowAll warning. Permissiveness is data now, so the thing to
    /// warn about is a wildcard grant — which covers resources that do not exist yet.
    /// </summary>
    [Fact]
    public void A_wildcard_granted_to_anonymous_is_reported_as_a_floor_rather_than_a_ceiling()
    {
        var posture = Reporter(ConfigWithAnonymousGrants("*/*")).Describe();

        posture.Warnings.Should().ContainSingle().Which.Should().Contain("*/*");
    }
}
