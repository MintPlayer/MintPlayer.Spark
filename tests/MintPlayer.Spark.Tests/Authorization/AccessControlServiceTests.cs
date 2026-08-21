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
    private static readonly Guid EveryoneId = Guid.Parse("33333333-3333-3333-3333-333333333333");
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
    {
        var config = new SecurityConfiguration
        {
            Rights = rights.ToList()
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
    public async Task IsAllowedAsync_NoUserGroups_NoEveryone_DenyAllDefault_ReturnsFalse()
    {
        var service = CreateService(ConfigWith(), userGroups: []);

        (await service.IsAllowedAsync("Read/Person")).Should().BeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_NoUserGroups_NoEveryone_AllowAllDefault_ReturnsTrue()
    {
        var service = CreateService(ConfigWith(), userGroups: [], DefaultAccessBehavior.AllowAll);

        (await service.IsAllowedAsync("Read/Person")).Should().BeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_AnonymousUser_EveryoneGroupHasGrant_ReturnsTrue()
    {
        var config = ConfigWith(
            groups: new() { [EveryoneId] = En("Everyone") },
            new Right { GroupId = EveryoneId, Resource = "Read/Person" });

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
    public async Task IsAllowedAsync_UserGroupNotInConfig_FallsToEveryoneIfPresent()
    {
        var config = ConfigWith(
            groups: new() { [EveryoneId] = En("Everyone") },
            new Right { GroupId = EveryoneId, Resource = "Read/Person" });

        // User claims to be in "Random" — not in config. Should still match via Everyone.
        var service = CreateService(config, ["Random"]);

        (await service.IsAllowedAsync("Read/Person")).Should().BeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_UserGroupNotInConfig_NoEveryone_ReturnsDefault()
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = "Read/Person" });

        var service = CreateService(config, ["NotRegistered"]);

        (await service.IsAllowedAsync("Read/Person")).Should().BeFalse();
    }

    // --- Well-known "Authenticated" group (#304) ---------------------------

    private static SecurityConfiguration AuthenticatedOnlyConfig() => ConfigWith(
        new Dictionary<Guid, TranslatedString>
        {
            [EveryoneId] = En("Everyone"),
            [AuthenticatedId] = En("Authenticated"),
        },
        new Right { Resource = "QueryRead/Person", GroupId = AuthenticatedId, IsDenied = false });

    /// <summary>
    /// The shape the group exists for: any signed-in user may query the type, and a row rule narrows
    /// it to their own rows. Before #304 this could not be written at all — the user carries no group
    /// claims, so granting to anything but Everyone denied them, and Everyone includes anonymous.
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
    /// Opt-in by definition, exactly as Everyone is. An app that never declares the group keeps its
    /// existing behaviour, so the change cannot alter an existing deployment's access decisions.
    /// </summary>
    [Fact]
    public async Task A_config_without_an_authenticated_group_is_unaffected()
    {
        var config = ConfigWith(
            new Dictionary<Guid, TranslatedString> { [EveryoneId] = En("Everyone") },
            new Right { Resource = "QueryRead/Person", GroupId = EveryoneId, IsDenied = false });

        var signedIn = CreateService(config, userGroups: [], authenticated: true);
        var anonymous = CreateService(config, userGroups: [], authenticated: false);

        (await signedIn.IsAllowedAsync("Query/Person")).Should().BeTrue();
        (await anonymous.IsAllowedAsync("Query/Person")).Should().BeTrue("Everyone still includes anonymous");
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
                [AuthenticatedId] = En("Authenticated"),
                [EditorsId] = En("Editors"),
            },
            new Right { Resource = "QueryRead/Person", GroupId = AuthenticatedId, IsDenied = false },
            new Right { Resource = "Query/Person", GroupId = EditorsId, IsDenied = true });

        var service = CreateService(config, userGroups: ["Editors"], authenticated: true);

        (await service.IsAllowedAsync("Query/Person")).Should().BeFalse();
    }
}
