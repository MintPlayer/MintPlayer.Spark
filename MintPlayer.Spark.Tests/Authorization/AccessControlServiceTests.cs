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

    private AccessControlService CreateService(
        SecurityConfiguration config,
        IEnumerable<string> userGroups,
        DefaultAccessBehavior defaultBehavior = DefaultAccessBehavior.DenyAll)
    {
        _configLoader.GetConfiguration().Returns(config);
        _groupMembership.GetCurrentUserGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(userGroups));

        var options = Options.Create(new AuthorizationOptions { DefaultBehavior = defaultBehavior });

        return new AccessControlService(_configLoader, _groupMembership, options, _logger);
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
}
