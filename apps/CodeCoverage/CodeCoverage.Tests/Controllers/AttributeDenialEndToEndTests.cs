using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CodeCoverage.Tests._Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Authorization;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The attribute denies <c>security.json</c> took over from <c>isVisible</c> (#264, M5), checked
/// against the real composition root rather than against the file's text.
/// </summary>
/// <remarks>
/// <para>
/// The Home counts are the case worth an HTTP round-trip. Home is a virtual type: its attributes are
/// whatever <c>HomeActions.OnLoadAsync</c> returned, and nothing guarantees a deny applies to an
/// object no document stands behind — except that <c>/spark/po/load</c> runs
/// <c>attributeRights.PresentAsync</c> on whatever the load produced. Before #264 the hook hid the
/// counts itself, with a runtime <c>ShowedOn</c>; a missing deny would now show "0 accounts" to every
/// signed-out visitor, which reads as a fact about their GitHub rather than about their being signed
/// out.
/// </para>
/// <para>
/// The signed-in half asks <see cref="IAttributeRights"/> under an authenticated principal instead of
/// loading the page: this host has no sign-in a test can drive (GitHub OAuth only), and the signed-in
/// load also fans out to GitHub for the caller's accounts.
/// </para>
/// </remarks>
[Collection(CoverageWebHostCollection.Name)]
public class AttributeDenialEndToEndTests
{
    /// <summary>Home's model id (<c>App_Data/Model/Home.json</c>).</summary>
    private const string HomeTypeId = "149ab426-ae90-4c96-ae26-3fdde3554754";

    private readonly CoverageWebHostFixture fixture;

    public AttributeDenialEndToEndTests(CoverageWebHostFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task An_anonymous_home_page_carries_no_account_or_repository_count()
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync("/spark/po/load", new StringContent(
            JsonSerializer.Serialize(new { objectTypeId = HomeTypeId, id = "home" }), Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "anonymous holds Read/Home");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = body.RootElement.GetProperty("attributes").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString())
            .ToArray();

        names.Should().Contain("Subtitle");
        names.Should().NotContain("AccountCount");
        names.Should().NotContain("RepoCount");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_home_counts_are_denied_to_visitors_only(bool authenticated)
    {
        var rights = await EffectiveAsync("Home", "Read", authenticated);

        rights.IsAllowed("AccountCount").Should().Be(authenticated);
        rights.IsAllowed("RepoCount").Should().Be(authenticated);
        rights.IsAllowed("Subtitle").Should().BeTrue();
    }

    /// <summary>
    /// Server-side keys: index queries and row filters use them, so they stay on the entity, and
    /// every caller is denied them (a deny on both well-known groups is "everyone", PRD §2.4).
    /// </summary>
    [Theory]
    [InlineData("Account", "OwnerKey", "Login", false)]
    [InlineData("Account", "OwnerKey", "Login", true)]
    [InlineData("Repository", "OwnerKey", "FullName", false)]
    [InlineData("Repository", "OwnerKey", "FullName", true)]
    [InlineData("Repository", "PreviousFullNames", "FullName", false)]
    [InlineData("Repository", "PreviousFullNames", "FullName", true)]
    public async Task Server_side_keys_are_denied_to_every_caller(string type, string attribute, string sibling, bool authenticated)
    {
        (await EffectiveAsync(type, "Read", authenticated)).IsAllowed(attribute).Should().BeFalse();
        (await EffectiveAsync(type, "Query", authenticated)).IsAllowed(attribute).Should().BeFalse();
        (await EffectiveAsync(type, "Read", authenticated)).IsAllowed(sibling).Should().BeTrue(
            "the denies name single attributes; the type-level grant still covers the rest");
    }

    [Fact]
    public async Task The_forge_accounts_provider_is_denied_to_its_only_readers()
        => (await EffectiveAsync("ForgeAccounts", "Read", authenticated: true)).IsAllowed("Provider").Should().BeFalse();

    private async Task<EffectiveAttributeRights> EffectiveAsync(string type, string verb, bool authenticated)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(authenticated
                ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "attribute-denial-test")], "Test")
                : new ClaimsIdentity()),
        };
        try
        {
            return await scope.ServiceProvider.GetRequiredService<IAttributeRights>().GetEffectiveAsync(type, verb);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }
}
