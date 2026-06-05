using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Authorization.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Authorization;

public class ClaimsGroupMembershipProviderTests
{
    private readonly IHttpContextAccessor _accessor = Substitute.For<IHttpContextAccessor>();

    private ClaimsGroupMembershipProvider CreateProvider() => new(_accessor);

    private void SetUser(ClaimsPrincipal? user)
    {
        var ctx = user is null ? null : new DefaultHttpContext { User = user };
        _accessor.HttpContext.Returns(ctx);
    }

    private static ClaimsPrincipal Authenticated(params (string type, string value)[] claims)
    {
        var identity = new ClaimsIdentity(
            claims.Select(c => new Claim(c.type, c.value)),
            authenticationType: "Test");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task Returns_empty_when_HttpContext_is_null()
    {
        SetUser(null);
        var provider = CreateProvider();

        var groups = await provider.GetCurrentUserGroupsAsync();

        groups.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_empty_when_user_is_not_authenticated()
    {
        SetUser(new ClaimsPrincipal(new ClaimsIdentity())); // no auth type → unauthenticated
        var provider = CreateProvider();

        var groups = await provider.GetCurrentUserGroupsAsync();

        groups.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_group_values_from_group_claim()
    {
        SetUser(Authenticated(("group", "Admins"), ("group", "Editors")));
        var provider = CreateProvider();

        var groups = await provider.GetCurrentUserGroupsAsync();

        groups.Should().BeEquivalentTo(["Admins", "Editors"]);
    }

    [Fact]
    public async Task Returns_group_values_from_groups_claim()
    {
        SetUser(Authenticated(("groups", "Admins")));
        var provider = CreateProvider();

        var groups = await provider.GetCurrentUserGroupsAsync();

        groups.Should().ContainSingle().Which.Should().Be("Admins");
    }

    [Fact]
    public async Task Recognizes_Microsoft_role_claim_type()
    {
        SetUser(Authenticated(
            ("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", "Admins")));
        var provider = CreateProvider();

        var groups = await provider.GetCurrentUserGroupsAsync();

        groups.Should().ContainSingle().Which.Should().Be("Admins");
    }

    [Fact]
    public async Task Recognizes_XML_SOAP_Group_claim_type()
    {
        SetUser(Authenticated(
            ("http://schemas.xmlsoap.org/claims/Group", "Admins")));
        var provider = CreateProvider();

        var groups = await provider.GetCurrentUserGroupsAsync();

        groups.Should().ContainSingle().Which.Should().Be("Admins");
    }

    [Fact]
    public async Task Claim_type_match_is_case_insensitive()
    {
        SetUser(Authenticated(("GROUP", "Admins"), ("Groups", "Editors")));
        var provider = CreateProvider();

        var groups = await provider.GetCurrentUserGroupsAsync();

        groups.Should().BeEquivalentTo(["Admins", "Editors"]);
    }

    [Fact]
    public async Task Duplicate_group_claims_are_deduped()
    {
        SetUser(Authenticated(("group", "Admins"), ("groups", "Admins")));
        var provider = CreateProvider();

        var groups = await provider.GetCurrentUserGroupsAsync();

        groups.Should().ContainSingle().Which.Should().Be("Admins");
    }

    [Fact]
    public async Task Ignores_claims_with_unrecognized_types()
    {
        SetUser(Authenticated(("group", "Admins"), ("email", "user@example.com"), ("custom", "value")));
        var provider = CreateProvider();

        var groups = await provider.GetCurrentUserGroupsAsync();

        groups.Should().BeEquivalentTo(["Admins"]);
    }

    [Fact]
    public async Task Returns_empty_when_authenticated_user_has_no_group_claims()
    {
        SetUser(Authenticated(("email", "user@example.com")));
        var provider = CreateProvider();

        var groups = await provider.GetCurrentUserGroupsAsync();

        groups.Should().BeEmpty();
    }
}
