using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authorization;

public class SecurityFileAccessControlTests
{
    private readonly ISecurityConfigurationLoader _configLoader = Substitute.For<ISecurityConfigurationLoader>();
    private readonly IGroupMembershipProvider _groupMembership = Substitute.For<IGroupMembershipProvider>();
    private readonly ILogger<SecurityFileAccessControl> _logger = NullLogger<SecurityFileAccessControl>.Instance;

    private static readonly Guid AdminsId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EditorsId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AnonymousId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AuthenticatedId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private SecurityFileAccessControl CreateService(
        SecurityConfiguration config,
        IEnumerable<string> userGroups,
        bool? authenticated = null)
    {
        _configLoader.GetConfiguration().Returns(config);
        _groupMembership.GetCurrentUserGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(userGroups));

        // The real expansion, not a stub of it. Faking GetResolvedRights would remove the only
        // logic these tests are about — which right covers which resource — and leave them
        // asserting that a substitute returns what it was told to.
        _configLoader.GetResolvedRights(Arg.Any<IReadOnlySet<Guid>>())
            .Returns(ci => RightsDecision.For(config, ci.Arg<IReadOnlySet<Guid>>()));

        return CreateService(authenticated, composed: []);
    }

    /// <summary>
    /// The service over the real per-request membership snapshot, with <paramref name="composed"/>
    /// as providers added by <c>AddGroupMembershipProvider</c> (#460, D12).
    /// </summary>
    private SecurityFileAccessControl CreateService(bool? authenticated, IGroupMembershipProvider[] composed)
    {
        var accessor = authenticated is null ? null : HttpContextFor(authenticated.Value);
        var membership = new SparkGroupMembership(
            _groupMembership,
            composed.Select(p => (IComposedGroupMembershipProvider)new ComposedGroupMembershipProvider<IGroupMembershipProvider>(p)).ToList(),
            accessor);

        return new SecurityFileAccessControl(_configLoader, membership, _logger, accessor);
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
        Dictionary<Guid, string>? groups = null,
        params Right[] rights)
        => ConfigWith(groups, wellKnown: null, rights);

    private static SecurityConfiguration ConfigWith(
        Dictionary<Guid, string>? groups,
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

    private static string En(string value) => value;

    [Fact]
    public async Task IsAllowedAsync_NoUserGroups_NoAnonymousGroup_ReturnsFalse()
    {
        var service = CreateService(ConfigWith(), userGroups: []);

        (await service.IsAllowedAsync("Read/Person")).Should().BeFalse();
    }

    /// <summary>
    /// D3 (#460): wildcard rights are refused when the file loads (see
    /// <c>SecurityConfigurationValidatorTests</c>). The evaluator no longer knows the token either,
    /// so a configuration that reached it without passing the validator still grants only what it
    /// names — <c>*</c> is an ordinary, unmatchable character, never "everything".
    /// </summary>
    [Theory]
    [InlineData("*/*", "Read/Person")]
    [InlineData("*/*", "AnythingAtAll/Whatever")]
    [InlineData("Read/*", "Read/Car")]
    [InlineData("*/Person", "Delete/Person")]
    public async Task A_wildcard_right_covers_nothing(string granted, string requested)
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = granted });

        var service = CreateService(config, ["Admins"]);

        (await service.IsAllowedAsync(requested)).Should().BeFalse();
    }

    /// <summary>
    /// The replacement for <c>*/Person</c>: a combined action names every action it covers, so an
    /// access review can read it, and it still composes with denial-first precedence.
    /// </summary>
    [Fact]
    public async Task A_denial_survives_a_combined_grant()
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = "QueryReadEditNewDelete/Car" },
            new Right { GroupId = AdminsId, Resource = "Delete/Car", IsDenied = true });

        var service = CreateService(config, ["Admins"]);

        (await service.IsAllowedAsync("Edit/Car")).Should().BeTrue();
        (await service.IsAllowedAsync("Delete/Car")).Should().BeFalse();
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
    public async Task IsAllowedAsync_CombinedAction_TargetMismatch_IsRefused()
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = "EditNewDelete/Person" });

        var service = CreateService(config, ["Admins"]);

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

    /// <summary>
    /// #467 D24: a claim matches the group's untranslated name only. A translated label is display
    /// text in translations.json and must never grant membership.
    /// </summary>
    [Fact]
    public async Task IsAllowedAsync_GroupNameMatch_never_uses_a_translation()
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = "Admins" },
            new Right { GroupId = AdminsId, Resource = "Read/Person" });

        var service = CreateService(config, ["Beheerders"]);

        (await service.IsAllowedAsync("Read/Person")).Should().BeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_NoMatchingRight_IsRefused()
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = "Read/Person" });

        var service = CreateService(config, ["Admins"]);

        (await service.IsAllowedAsync("Read/Car")).Should().BeFalse();
    }

    /// <summary>
    /// The ordering trap, pinned. A per-right chain answers TRUE here: the exact grant matches at
    /// step 2 and the combined denial is never expanded. All denial matching must precede all
    /// grant matching, over the whole group set.
    /// </summary>
    [Fact]
    public async Task A_combined_denial_beats_an_exact_grant_of_one_of_its_parts()
    {
        var config = ConfigWith(
            groups: new()
            {
                [AdminsId] = En("Admins"),
                [EditorsId] = En("Editors"),
            },
            new Right { GroupId = AdminsId, Resource = "Read/Car" },
            new Right { GroupId = EditorsId, Resource = "QueryReadEditNewDelete/Car", IsDenied = true });

        var service = CreateService(config, ["Admins", "Editors"]);

        (await service.IsAllowedAsync("Read/Car")).Should().BeFalse();
    }

    /// <summary>
    /// The other half of symmetry: a combined denial denies every action it names, not the literal
    /// string. Until this change it denied nothing at all, and the loader refused to accept the
    /// shape rather than making it work.
    /// </summary>
    [Theory]
    [InlineData("Edit/Car")]
    [InlineData("New/Car")]
    [InlineData("Delete/Car")]
    public async Task A_combined_denial_expands(string resource)
    {
        var config = ConfigWith(
            groups: new() { [AdminsId] = En("Admins") },
            new Right { GroupId = AdminsId, Resource = "QueryReadEditNewDelete/Car" },
            new Right { GroupId = AdminsId, Resource = "EditNewDelete/Car", IsDenied = true });

        var service = CreateService(config, ["Admins"]);

        (await service.IsAllowedAsync(resource)).Should().BeFalse();
        (await service.IsAllowedAsync("Query/Car")).Should().BeTrue();
    }

    /// <summary>
    /// IsImportant is a precedence tier, not an audit marker (D8). An important grant survives an
    /// ordinary denial; an important denial survives everything.
    /// </summary>
    [Fact]
    public async Task An_important_grant_overrides_an_ordinary_denial()
    {
        var config = ConfigWith(
            groups: new()
            {
                [AdminsId] = En("Admins"),
                [EditorsId] = En("Editors"),
            },
            new Right { GroupId = AdminsId, Resource = "Read/Person", IsImportant = true },
            new Right { GroupId = EditorsId, Resource = "Read/Person", IsDenied = true });

        var service = CreateService(config, ["Admins", "Editors"]);

        (await service.IsAllowedAsync("Read/Person")).Should().BeTrue();
    }

    [Fact]
    public async Task An_important_denial_overrides_an_important_grant()
    {
        var config = ConfigWith(
            groups: new()
            {
                [AdminsId] = En("Admins"),
                [EditorsId] = En("Editors"),
            },
            new Right { GroupId = AdminsId, Resource = "Read/Person", IsImportant = true },
            new Right { GroupId = EditorsId, Resource = "Read/Person", IsImportant = true, IsDenied = true });

        var service = CreateService(config, ["Admins", "Editors"]);

        (await service.IsAllowedAsync("Read/Person")).Should()
            .BeFalse("within the important tier the safer answer wins, so the outcome cannot depend on file order");
    }

    [Fact]
    public async Task IsAllowedAsync_EmptyRightsList_IsRefused()
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
    public async Task IsAllowedAsync_UserGroupNotInConfig_NoAnonymousGroup_IsRefused()
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
        new Dictionary<Guid, string>
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
            new Dictionary<Guid, string> { [AnonymousId] = En("Public") },
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
            new Dictionary<Guid, string> { [AdminsId] = En("Admins") },
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
            new Dictionary<Guid, string>
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
    /// M2c-1: the attribute-level table is computed over the same resolved groups as the type-level
    /// decision — here a claim-named group plus the authenticated role.
    /// </summary>
    [Fact]
    public async Task GetAttributeRightsAsync_composes_over_the_callers_resolved_groups()
    {
        var config = ConfigWith(
            groups: new() { [EditorsId] = En("Editors"), [AuthenticatedId] = En("Signed-in users") },
            wellKnown: new() { ["authenticated"] = AuthenticatedId },
            new Right { GroupId = AuthenticatedId, Resource = "QueryReadEdit/Song" },
            new Right { GroupId = EditorsId, Resource = "Edit/Song/Lyrics", IsDenied = true });

        var service = CreateService(config, ["Editors"], authenticated: true);

        var edit = await service.GetAttributeRightsAsync("Edit", "Song");
        edit.TypeAllowed.Should().BeTrue();
        edit.IsAllowed("Lyrics").Should().BeFalse();
        edit.IsAllowed("Title").Should().BeTrue();

        var anonymous = await CreateService(config, [], authenticated: false).GetAttributeRightsAsync("Edit", "Song");
        anonymous.TypeAllowed.Should().BeFalse();
        anonymous.IsAllowed("Title").Should().BeFalse();
    }

    // ---------- #460 D12: composed providers, provider-returned ids, the per-request cache ----------

    private sealed class NamesProvider(params string[] names) : IGroupMembershipProvider
    {
        public int Calls { get; private set; }

        public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IEnumerable<string>>(names);
        }
    }

    /// <summary>A provider that knows groups only by id — the shape earned privileges take.</summary>
    private sealed class IdsProvider(params Guid[] ids) : IGroupMembershipProvider, IGroupIdMembershipProvider
    {
        public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Enumerable.Empty<string>());

        public Task<IEnumerable<Guid>> GetCurrentUserGroupIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<Guid>>(ids);
    }

    private SecurityConfiguration ComposedConfig()
    {
        var config = ConfigWith(
            groups: new()
            {
                [AdminsId] = En("Admins"),
                [EditorsId] = En("Editors"),
                [AuthenticatedId] = En("Signed-in users"),
            },
            wellKnown: new() { ["authenticated"] = AuthenticatedId },
            new Right { GroupId = AdminsId, Resource = "Delete/Car" },
            new Right { GroupId = EditorsId, Resource = "Edit/Car" });

        _configLoader.GetConfiguration().Returns(config);
        _configLoader.GetResolvedRights(Arg.Any<IReadOnlySet<Guid>>())
            .Returns(ci => RightsDecision.For(config, ci.Arg<IReadOnlySet<Guid>>()));
        return config;
    }

    [Fact]
    public async Task A_composed_provider_adds_to_the_primary_one_rather_than_replacing_it()
    {
        ComposedConfig();
        _groupMembership.GetCurrentUserGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<string>>(["Admins"]));

        var service = CreateService(authenticated: true, composed: [new NamesProvider("Editors")]);

        (await service.IsAllowedAsync("Delete/Car")).Should().BeTrue("the primary provider's group still counts");
        (await service.IsAllowedAsync("Edit/Car")).Should().BeTrue("the composed provider's group is merged in");
    }

    [Fact]
    public async Task A_provider_may_name_a_group_by_id()
    {
        ComposedConfig();
        _groupMembership.GetCurrentUserGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Enumerable.Empty<string>()));

        var service = CreateService(authenticated: true, composed: [new IdsProvider(EditorsId)]);

        (await service.IsAllowedAsync("Edit/Car")).Should().BeTrue();
        (await service.IsAllowedAsync("Delete/Car")).Should().BeFalse();
    }

    /// <summary>
    /// The reserved-id rule holds for ids exactly as for names: a provider cannot hand an anonymous
    /// caller the authenticated role, and an id security.json does not declare grants nothing.
    /// </summary>
    [Fact]
    public async Task A_provider_returned_id_cannot_assert_a_well_known_role()
    {
        var config = ComposedConfig();
        config.Rights.Add(new Right { GroupId = AuthenticatedId, Resource = "Read/Car" });
        _groupMembership.GetCurrentUserGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Enumerable.Empty<string>()));

        var service = CreateService(authenticated: false, composed: [new IdsProvider(AuthenticatedId, Guid.NewGuid())]);

        (await service.IsAllowedAsync("Read/Car")).Should().BeFalse();
    }

    [Fact]
    public async Task Every_provider_is_asked_once_per_request()
    {
        ComposedConfig();
        _groupMembership.GetCurrentUserGroupsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<string>>(["Admins"]));
        var composed = new NamesProvider("Editors");

        var service = CreateService(authenticated: true, composed: [composed]);

        for (var i = 0; i < 5; i++)
        {
            await service.IsAllowedAsync("Delete/Car");
            await service.IsAllowedAsync("Edit/Car");
        }

        composed.Calls.Should().Be(1);
        await _groupMembership.Received(1).GetCurrentUserGroupsAsync(Arg.Any<CancellationToken>());
    }
}
