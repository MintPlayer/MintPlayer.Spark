using System.Security.Claims;
using CodeCoverage.Tests._Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Endpoints.PersistentObject;
using MintPlayer.Spark.Services;
using Xunit;

namespace CodeCoverage.Tests.Actions;

/// <summary>
/// The page an exclusion chip links to, <c>/po/forgecheck/&lt;name&gt;</c> (merge-queue PRD D117), through the real
/// composition root: the body of <c>POST /spark/po/load</c> resolves the type by its alias, checks the type-level
/// <c>Read</c> right, and hands the id to <c>ForgeCheckActions.OnLoadAsync</c>, which maps it with the framework's own
/// mapper.
/// </summary>
/// <remarks>
/// Driven in process under a signed-in principal rather than over HTTP: this host has no sign-in a test can drive
/// (GitHub OAuth only), the same reason <see cref="Controllers.AttributeDenialEndToEndTests"/> gives. The id travels in the
/// load's JSON body, never in a route, so a name with a slash or parentheses reaches the hook unchanged.
/// </remarks>
[Collection(CoverageWebHostCollection.Name)]
public class ForgeCheckLoadEndToEndTests
{
    private readonly CoverageWebHostFixture fixture;

    public ForgeCheckLoadEndToEndTests(CoverageWebHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("tests / other suites")]
    [InlineData("build (ubuntu-latest, 22.x)")]
    public async Task A_signed_in_user_opens_a_check_by_its_name(string name)
    {
        var po = await LoadAsync("forgecheck", name, authenticated: true);

        po.Should().NotBeNull("a check has no document, and its page must not 404");
        po!.Id.Should().Be(name);
        po.Name.Should().Be("ForgeCheck");
        po.Breadcrumb.Should().Be(name);
        var attribute = po.Attributes.Single(a => a.Name == "Name");
        attribute.Value.Should().Be(name);
        attribute.IsReadOnly.Should().BeTrue();
        po.Can.Should().NotBeNull();
        po.Can!.Edit.Should().BeFalse();
        po.Can.Delete.Should().BeFalse();
    }

    [Fact]
    public async Task A_visitor_is_refused_like_a_missing_object()
        => (await LoadAsync("forgecheck", "tests / other suites", authenticated: false))
            .Should().BeNull("only signed-in users hold QueryRead/ForgeCheck");

    private async Task<PersistentObject?> LoadAsync(string type, string id, bool authenticated)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(authenticated
                ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "forge-check-load-test")], "Test")
                : new ClaimsIdentity()),
        };
        try
        {
            return await scope.ServiceProvider.GetRequiredService<IPersistentObjectReadPath>()
                .LoadAsync(new PersistentObjectReferenceRequest { ObjectTypeId = type, Id = id }, CancellationToken.None);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }
}
