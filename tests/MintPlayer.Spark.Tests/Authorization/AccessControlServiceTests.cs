using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Models;
using MintPlayer.Spark.Authorization.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authorization;

public class AccessControlServiceTests
{
    private readonly ISecurityConfigurationLoader _configLoader = Substitute.For<ISecurityConfigurationLoader>();
    private readonly IGroupMembershipProvider _groupMembership = Substitute.For<IGroupMembershipProvider>();
    private readonly ILogger<AccessControlService> _logger = NullLogger<AccessControlService>.Instance;

    private static readonly Guid AdminsId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EditorsId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AnonymousId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AuthenticatedId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private AccessControlService CreateService(
        SecurityConfiguration config,
        IEnumerable<string> userGroups,
        DefaultAccessBehavior defaultBehavior = DefaultAccessBehavior.DenyAll,
        bool? authenticated = null)
    {
        _configLoader.GetConfiguration().Returns(config);
        _groupMembership.GetCurrentUserGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(userGroups));

        var options = Options.Create(new AuthorizationOptions { DefaultBehavior = defaultBehavior });

        return new AccessControlService(_configLoader, _groupMembership, options, _logger,
            authenticated is null ? null : HttpContextFor(authenticated.Value));
    }

    /// <summary>
    /// A caller carrying no group claims whatsoever — the case that made an authenticated user
    /// indistinguishable from an anonymous one before the Authenticated group existed.
    /// </summary>
    private static IHttpContextAccessor HttpContextFor(bool authenticated)
    {
        var identity = authenticated ? new ClaimsIdentity(authenticationType: "TestScheme") : new ClaimsIdentity();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = new ClaimsPrincipal(identity) });
        return accessor;
    }

    private static SecurityConfiguration ConfigWith(
        Dictionary<Guid, TranslatedString>? groups = null,
        params Right[] rights)
        => ConfigWith(groups, wellKnown: null, rights);

    private static SecurityConfiguration ConfigWith(
        Dictionary<Guid, TranslatedString>? groups,
        Dictionary<string, Guid>? wellKnown,
        params Right[] rights)
    {
        var config = new SecurityConfiguration
        {
            Rights = rights.ToList(),
            WellKnown = wellKnown?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString()),
        };

        if (groups != null)
        {
            foreach (var kvp in groups)
            {
                config.Groups[kvp.Key.ToString()] = kvp.Value;
            }
        }

        return config;
    }

    private static TranslatedString En(string value) => TranslatedString.Create(value);

    [Fact]
    public async Task IsAllowedAsync_NoUserGroups_NoAnonymousGroup_DenyAllDefault_ReturnsFalse()
    {
        var service = CreateService(ConfigWith(), userGroups: []);

        (await service.IsAllowedAsync("Read/Person")).Should().BeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_NoUserGroups_NoAnonymousGroup_AllowAllDefault_ReturnsTrue()
    {
        var service = CreateService(ConfigWith(), userGroups: [], DefaultAccessBehavior.AllowAll);

        (await service.IsAllowedAsync("Read/Person")).Should().BeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_AnonymousUser_AnonymousGroupHasGrant_ReturnsTrue()
    {
        var config = ConfigWith(
            groups: new() { [AnonymousId] = En("Public") },
            wellKnown: new() { ["anonymous"] = AnonymousId },
            new Right { GroupId = AnonymousId, Resource = "Read/Person" });

        var service = CreateService(config, userGroups: []);

        (await service.IsAllowedAsync("Read/Person")).Should().BeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_ExactResourceMatch_IsCaseInsensitive()
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = "Read/Person" });

        var service = CreateService(config, ["Admins"]);

        (await service.IsAllowedAsync("read/person")).Should().BeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_ExplicitDenial_OverridesGrant()
    {
        var config = ConfigWith(
            groups: new()
            {
                [AdminsId] = En("Admins"),
                [EditorsId] = En("Editors"),
            },
            new Right { GroupId = AdminsId, Resource = "Read/Person" },
            new Right { GroupId = EditorsId, Resource = "Read/Person", IsDenied = true });

        var service = CreateService(config, ["Admins", "Editors"]);

        (await service.IsAllowedAsync("Read/Person")).Should().BeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_CombinedAction_EditNewDelete_IncludesEdit()
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = "EditNewDelete/Person" });

        var service = CreateService(config, ["Admins"]);

        (await service.IsAllowedAsync("Edit/Person")).Should().BeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_CombinedAction_EditNewDelete_DoesNotIncludeQuery()
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = "EditNewDelete/Person" });

        var service = CreateService(config, ["Admins"]);

        (await service.IsAllowedAsync("Query/Person")).Should().BeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_CombinedAction_TargetMismatch_FallsToDefault()
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = "EditNewDelete/Person" });

        var service = CreateService(config, ["Admins"]);

        // Request target "Car" doesn't match right target "Person" — no match, default DenyAll
        (await service.IsAllowedAsync("Edit/Car")).Should().BeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_GroupNameMatch_IsCaseInsensitive()
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = "Read/Person" });

        var service = CreateService(config, ["admins"]);

        (await service.IsAllowedAsync("Read/Person")).Should().BeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_GroupNameMatch_UsesAnyTranslation()
    {
        var translated = new TranslatedString();
        translated.Translations["en"] = "Admins";
        translated.Translations["nl"] = "Beheerders";

        var config = ConfigWith(
            groups: new() { [AdminsId] = translated },
            new Right { GroupId = AdminsId, Resource = "Read/Person" });

        var service = CreateService(config, ["Beheerders"]);

        (await service.IsAllowedAsync("Read/Person")).Should().BeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_NoMatchingRight_FallsToDefault_DenyAll()
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = "Read/Person" });

        var service = CreateService(config, ["Admins"]);

        (await service.IsAllowedAsync("Read/Car")).Should().BeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_NoMatchingRight_FallsToDefault_AllowAll()
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = "Read/Person" });

        var service = CreateService(config, ["Admins"], DefaultAccessBehavior.AllowAll);

        (await service.IsAllowedAsync("Read/Car")).Should().BeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_EmptyRightsList_FallsToDefault()
    {
        var config = ConfigWith(groups: new() { [AdminsId] = En("Admins") });

        var service = CreateService(config, ["Admins"]);

        (await service.IsAllowedAsync("Read/Person")).Should().BeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_UserGroupNotInConfig_FallsToAnonymousIfPresent()
    {
        var config = ConfigWith(
            groups: new() { [AnonymousId] = En("Public") },
            wellKnown: new() { ["anonymous"] = AnonymousId },
            new Right { GroupId = AnonymousId, Resource = "Read/Person" });

        // Claims to be in "Random" — not in config — and has not signed in, so the anonymous
        // group is what is left.
        var service = CreateService(config, ["Random"]);

        (await service.IsAllowedAsync("Read/Person")).Should().BeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_UserGroupNotInConfig_NoAnonymousGroup_ReturnsDefault()
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = "Read/Person" });

        var service = CreateService(config, ["NotRegistered"]);

        (await service.IsAllowedAsync("Read/Person")).Should().BeFalse();
    }

    // --- Well-known groups: anonymous + authenticated (#298, #304) ----------

    /// <summary>
    /// Note the display names: nothing here is called "Anonymous" or "Authenticated". The roles come
    /// from the <c>wellKnown</c> id map, so a group's name is free to say whatever the UI needs - and
    /// renaming it can no longer un-declare its role.
    /// </summary>
    private static SecurityConfiguration AuthenticatedOnlyConfig() => ConfigWith(
        new Dictionary<Guid, TranslatedString>
        {
            [AnonymousId] = En("Public"),
            [AuthenticatedId] = En("Signed-in users"),
        },
        wellKnown: new() { ["anonymous"] = AnonymousId, ["authenticated"] = AuthenticatedId },
        new Right { Resource = "QueryRead/Person", GroupId = AuthenticatedId, IsDenied = false });

    /// <summary>
    /// The shape the group exists for: any signed-in user may query the type, and a row rule narrows
    /// it to their own rows. Before #304 this could not be written at all — the user carries no group
    /// claims, so granting to anything but Everyone denied them, and Everyone included anonymous.
    /// </summary>
    [Fact]
    public async Task A_signed_in_caller_belongs_to_the_authenticated_group()
    {
        var service = CreateService(AuthenticatedOnlyConfig(), userGroups: [], authenticated: true);

        (await service.IsAllowedAsync("Query/Person")).Should().BeTrue();
        (await service.IsAllowedAsync("Read/Person")).Should().BeTrue();
    }

    [Fact]
    public async Task An_anonymous_caller_is_not_in_the_authenticated_group()
    {
        var service = CreateService(AuthenticatedOnlyConfig(), userGroups: [], authenticated: false);

        (await service.IsAllowedAsync("Query/Person")).Should().BeFalse();
        (await service.IsAllowedAsync("Read/Person")).Should().BeFalse();
    }

    /// <summary>
    /// The genuinely new semantic, and the reason migration is "one grant becomes two". The old
    /// <c>Everyone</c> was the floor for <em>every</em> caller; <c>anonymous</c> applies only while
    /// the caller has not signed in. Moving a grant to <c>anonymous</c> alone therefore <b>narrows</b>
    /// it, and a signed-in user loses access that used to be theirs.
    /// </summary>
    [Fact]
    public async Task An_anonymous_grant_stops_applying_once_signed_in()
    {
        var config = ConfigWith(
            new Dictionary<Guid, TranslatedString> { [AnonymousId] = En("Public") },
            wellKnown: new() { ["anonymous"] = AnonymousId },
            new Right { Resource = "QueryRead/Person", GroupId = AnonymousId, IsDenied = false });

        var signedIn = CreateService(config, userGroups: [], authenticated: true);
        var anonymous = CreateService(config, userGroups: [], authenticated: false);

        (await anonymous.IsAllowedAsync("Query/Person")).Should().BeTrue();
        (await signedIn.IsAllowedAsync("Query/Person")).Should()
            .BeFalse("anonymous is not a floor - a right both should have is two grants");
    }

    /// <summary>
    /// Opt-in by definition. An application declaring no well-known groups behaves exactly as one
    /// declaring none always did.
    /// </summary>
    [Fact]
    public async Task A_config_with_no_well_known_groups_is_unaffected()
    {
        var config = ConfigWith(
            new Dictionary<Guid, TranslatedString> { [AdminsId] = En("Admins") },
            new Right { Resource = "QueryRead/Person", GroupId = AdminsId, IsDenied = false });

        // Asserted one at a time: CreateService restubs the shared group-membership substitute, so
        // building both services first would leave the earlier one answering the later one's groups.
        var signedIn = CreateService(config, userGroups: ["Admins"], authenticated: true);
        (await signedIn.IsAllowedAsync("Query/Person")).Should().BeTrue();

        var anonymous = CreateService(config, userGroups: [], authenticated: false);
        (await anonymous.IsAllowedAsync("Query/Person")).Should().BeFalse();
    }

    /// <summary>
    /// Nothing outside an HTTP request (a background job, a system context) should trip over the
    /// accessor being absent.
    /// </summary>
    [Fact]
    public async Task An_absent_http_context_does_not_grant_the_authenticated_group()
    {
        var service = CreateService(AuthenticatedOnlyConfig(), userGroups: [], authenticated: null);

        (await service.IsAllowedAsync("Query/Person")).Should().BeFalse();
    }

    /// <summary>
    /// An explicit denial must still win — the new group is an ordinary member of the group set, not
    /// a bypass.
    /// </summary>
    [Fact]
    public async Task A_denial_still_overrides_an_authenticated_grant()
    {
        var config = ConfigWith(
            new Dictionary<Guid, TranslatedString>
            {
                [AuthenticatedId] = En("Signed-in users"),
                [EditorsId] = En("Editors"),
            },
            wellKnown: new() { ["authenticated"] = AuthenticatedId },
            new Right { Resource = "QueryRead/Person", GroupId = AuthenticatedId, IsDenied = false },
            new Right { Resource = "Query/Person", GroupId = EditorsId, IsDenied = true });

        var service = CreateService(config, userGroups: ["Editors"], authenticated: true);

        (await service.IsAllowedAsync("Query/Person")).Should().BeFalse();
    }

    /// <summary>
    /// R12/A11 - RED before this change. <c>ResolveGroupIds</c> matched a provider-returned name
    /// against <em>any</em> translation of <em>any</em> group, well-known ones included, so a
    /// principal carrying the authenticated group's display name resolved its id at step 1 and never
    /// reached the IsAuthenticated test. A comment shipped in #304 asserted the opposite.
    /// </summary>
    [Fact]
    public async Task A_claim_naming_a_reserved_group_does_not_grant_it()
    {
        var service = CreateService(
            AuthenticatedOnlyConfig(),
            userGroups: ["Signed-in users"],   // exactly what the group is called
            authenticated: false);

        (await service.IsAllowedAsync("Query/Person")).Should()
            .BeFalse("authentication state decides the role, never a claim");
    }

    /// <summary>
    /// A12 - also RED before this change. The roles used to be matched through
    /// <c>TranslatedString.GetDefaultValue()</c>, which returns the first translation in FILE ORDER,
    /// so the same group with the same translations resolved or did not depending on which key the
    /// serializer happened to emit first.
    /// </summary>
    [Fact]
    public async Task Reordering_translations_changes_no_decision()
    {
        var englishFirst = TranslatedString.Create("Signed-in users");
        englishFirst.Translations["nl"] = "Aangemelde gebruikers";

        var dutchFirst = new TranslatedString();
        dutchFirst.Translations["nl"] = "Aangemelde gebruikers";
        dutchFirst.Translations["en"] = "Signed-in users";

        foreach (var name in new[] { englishFirst, dutchFirst })
        {
            var config = ConfigWith(
                new Dictionary<Guid, TranslatedString> { [AuthenticatedId] = name },
                wellKnown: new() { ["authenticated"] = AuthenticatedId },
                new Right { Resource = "QueryRead/Person", GroupId = AuthenticatedId, IsDenied = false });

            var service = CreateService(config, userGroups: [], authenticated: true);

            (await service.IsAllowedAsync("Query/Person")).Should().BeTrue();
        }
    }
}
