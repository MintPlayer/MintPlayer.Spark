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

        return new SecurityPostureReporter(loader, Substitute.For<IModelLoader>());
    }

    private static SecurityConfiguration ConfigWithAnonymousGrants(params string[] resources)
        => new()
        {
            Groups =
            {
                [AnonymousId.ToString()] = "Public",
                [AdminsId.ToString()] = "Admins",
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
            Groups = { [AdminsId.ToString()] = "Admins" },
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
    /// D3 (#460): the "floor rather than a ceiling" warning for a wildcard grant is gone with the
    /// wildcard itself — the validator refuses <c>*</c> before the reporter runs — so the listing is
    /// the whole anonymous surface and carries no caveat.
    /// </summary>
    [Fact]
    public void The_listing_is_a_ceiling_and_carries_no_warning()
    {
        var posture = Reporter(ConfigWithAnonymousGrants("QueryReadEditNewDelete/Company")).Describe();

        posture.Warnings.Should().BeEmpty();
        posture.AnonymouslyReachable.Should().BeEquivalentTo(
            ["Query/Company", "Read/Company", "Edit/Company", "New/Company", "Delete/Company"]);
    }

    // ---------- M2c-1: attribute-level rights ----------

    private static IModelLoader SongModel()
    {
        var model = Substitute.For<IModelLoader>();
        var song = new EntityTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Song",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Title" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Lyrics" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Genre" },
            ],
        };
        model.GetEntityTypeByName(Arg.Any<string>())
            .Returns(ci => string.Equals(ci.Arg<string>(), "Song", StringComparison.OrdinalIgnoreCase) ? song : null);
        return model;
    }

    private static SecurityPosture DescribeWithModel(SecurityConfiguration config)
    {
        var loader = Substitute.For<ISecurityConfigurationLoader>();
        loader.GetConfiguration().Returns(config);
        return new SecurityPostureReporter(loader, SongModel()).Describe();
    }

    private static Right AdminRight(string resource, bool denied = false)
        => new() { Id = Guid.NewGuid(), GroupId = AdminsId, Resource = resource, IsDenied = denied };

    /// <summary>
    /// The stale-deny trap (PRD §5 Q13): a group restricts Edit on some attributes of Song, and Genre
    /// — mentioned by none of its attribute rights — still inherits the type-level grant. Reported as
    /// an information-level note, never in the fingerprint.
    /// </summary>
    [Fact]
    public void A_partial_attribute_restriction_is_noted()
    {
        var config = ConfigWithAnonymousGrants();
        config.Rights.Add(AdminRight("Edit/Song/Title", denied: true));
        config.Rights.Add(AdminRight("Edit/Song/Lyrics", denied: true));

        var posture = DescribeWithModel(config);

        posture.Notes.Should().ContainSingle().Which.Should().Be(
            "Group 'Admins' restricts Edit on 2 of 3 attributes of 'Song'; 'Genre' is still editable through the type-level right — intended?");
        posture.Warnings.Should().BeEmpty();
        posture.Fingerprint.Should().NotContain("Song");
    }

    [Fact]
    public void An_attribute_restriction_that_mentions_every_attribute_is_not_noted()
    {
        var config = ConfigWithAnonymousGrants();
        config.Rights.Add(AdminRight("Edit/Song/Title", denied: true));
        config.Rights.Add(AdminRight("Edit/Song/Lyrics", denied: true));
        config.Rights.Add(AdminRight("Edit/Song/Genre"));

        DescribeWithModel(config).Notes.Should().BeEmpty();
    }

    /// <summary>
    /// An attribute grant never unlocks what its type right withholds, so the anonymous surface lists
    /// it only when the type-level right is reachable too.
    /// </summary>
    [Fact]
    public void An_anonymous_attribute_grant_without_the_type_grant_reaches_nothing()
    {
        var withoutType = DescribeWithModel(ConfigWithAnonymousGrants("Read/Song/Lyrics"));
        withoutType.AnonymouslyReachable.Should().BeEmpty();

        var withType = DescribeWithModel(ConfigWithAnonymousGrants("Read/Song", "Read/Song/Lyrics"));
        withType.AnonymouslyReachable.Should().Contain("Read/Song").And.Contain("Read/Song/Lyrics");
    }
}
