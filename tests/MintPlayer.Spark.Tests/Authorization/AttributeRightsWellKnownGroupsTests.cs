using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authorization;

/// <summary>
/// "Hide this attribute for everyone" through security.json (#264 replaces the model's
/// <c>isVisible</c>): what an attribute Read-deny on the <c>wellKnown</c> groups means, over the real
/// <see cref="SecurityFileAccessControl"/> and <see cref="RightsDecision"/>.
/// </summary>
/// <remarks>
/// The well-known roles are exclusive: a signed-in caller is in <c>authenticated</c> and NOT in
/// <c>anonymous</c> (SecurityFileAccessControl.ResolveDecisionAsync). "Everyone" is therefore two
/// denies, and an ordinary deny outranks every ordinary grant of every other group the caller holds;
/// only an important grant (type or attribute) outranks it.
/// </remarks>
public class AttributeRightsWellKnownGroupsTests
{
    private static readonly Guid AdminsId = Guid.Parse("5a000000-0000-4000-8000-000000000001");
    private static readonly Guid AnonymousId = Guid.Parse("5a000000-0000-4000-8000-000000000002");
    private static readonly Guid AuthenticatedId = Guid.Parse("5a000000-0000-4000-8000-000000000003");

    private static Right Grant(string resource, Guid group, bool important = false)
        => new() { Id = Guid.NewGuid(), Resource = resource, GroupId = group, IsImportant = important };

    private static Right Deny(string resource, Guid group)
        => new() { Id = Guid.NewGuid(), Resource = resource, GroupId = group, IsDenied = true };

    private static SecurityConfiguration Config(params Right[] rights)
    {
        var config = new SecurityConfiguration
        {
            Rights = [.. rights],
            WellKnown = new Dictionary<string, string>
            {
                [SparkWellKnownGroups.Anonymous] = AnonymousId.ToString(),
                [SparkWellKnownGroups.Authenticated] = AuthenticatedId.ToString(),
            },
        };
        config.Groups[AdminsId.ToString()] = "Admins";
        config.Groups[AnonymousId.ToString()] = "Anonymous visitors";
        config.Groups[AuthenticatedId.ToString()] = "Signed-in users";
        return config;
    }

    private static async Task<bool> CanReadLastNameAsync(SecurityConfiguration config, bool authenticated, params string[] groups)
    {
        var loader = Substitute.For<ISecurityConfigurationLoader>();
        loader.GetConfiguration().Returns(config);
        loader.GetResolvedRights(Arg.Any<IReadOnlySet<Guid>>())
            .Returns(ci => RightsDecision.For(config, ci.Arg<IReadOnlySet<Guid>>()));

        var provider = Substitute.For<IGroupMembershipProvider>();
        provider.GetCurrentUserGroupsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IEnumerable<string>>(groups));

        var identity = authenticated ? new ClaimsIdentity(authenticationType: "TestScheme") : new ClaimsIdentity();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = new ClaimsPrincipal(identity) });

        var service = new SecurityFileAccessControl(
            loader, new SparkGroupMembership(provider, [], accessor), NullLogger<SecurityFileAccessControl>.Instance, accessor);

        var rights = await service.GetAttributeRightsAsync("Read", "Person");
        return rights.IsAllowed("LastName");
    }

    private static readonly Right[] TypeGrants =
    [
        Grant("QueryRead/Person", AnonymousId),
        Grant("QueryRead/Person", AuthenticatedId),
        Grant("QueryReadEditNewDelete/Person", AdminsId),
    ];

    [Fact]
    public async Task A_deny_on_anonymous_only_hides_the_attribute_from_visitors_but_not_from_signed_in_callers()
    {
        var config = Config([.. TypeGrants, Deny("Read/Person/LastName", AnonymousId)]);

        (await CanReadLastNameAsync(config, authenticated: false)).Should().BeFalse();
        (await CanReadLastNameAsync(config, authenticated: true)).Should().BeTrue(
            "a signed-in caller is in 'authenticated' and not in 'anonymous'");
    }

    [Fact]
    public async Task A_deny_on_both_well_known_groups_hides_the_attribute_from_every_caller_including_admins()
    {
        var config = Config([.. TypeGrants,
            Grant("Read/Person/LastName", AdminsId),
            Deny("Read/Person/LastName", AnonymousId),
            Deny("Read/Person/LastName", AuthenticatedId)]);

        (await CanReadLastNameAsync(config, authenticated: false)).Should().BeFalse();
        (await CanReadLastNameAsync(config, authenticated: true)).Should().BeFalse();
        (await CanReadLastNameAsync(config, authenticated: true, "Admins")).Should().BeFalse(
            "an ordinary deny outranks every ordinary grant, the admin's attribute grant included");
    }

    [Theory]
    [InlineData("Read/Person/LastName")]
    [InlineData("Read/Person")]
    public async Task An_important_admin_grant_outranks_the_everyone_deny(string importantResource)
    {
        var config = Config([.. TypeGrants,
            Grant(importantResource, AdminsId, important: true),
            Deny("Read/Person/LastName", AnonymousId),
            Deny("Read/Person/LastName", AuthenticatedId)]);

        (await CanReadLastNameAsync(config, authenticated: true, "Admins")).Should().BeTrue();
        (await CanReadLastNameAsync(config, authenticated: true)).Should().BeFalse("only the admin holds the important grant");
    }
}
