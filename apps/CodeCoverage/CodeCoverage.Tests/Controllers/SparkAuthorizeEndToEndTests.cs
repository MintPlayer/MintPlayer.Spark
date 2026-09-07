using System.Net;
using CodeCoverage.Tests._Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The first tests in this application that actually execute its <c>[SparkAuthorize]</c> filters.
/// <para>
/// Everything else constructs a controller through DI and calls the method, which cannot run a
/// filter — so the attribute protecting six production files was, until now, enforced by nothing
/// in this suite. Both halves matter and only one of them is usually remembered: that a caller
/// without the right is refused, AND that an <c>[AllowAnonymous]</c> endpoint is still reachable.
/// The badge is public by design, and a change that made the whole app require a signed-in user
/// would break every README badge in the wild while every existing test stayed green.
/// </para>
/// </summary>
/// <remarks>
/// These were skipped for one session with a note claiming the model hash was unstable across
/// hosting models. It was: booting in-process threw <c>SparkModelOutOfSyncException</c> for exactly
/// Account, Build and Repository while <c>--spark-verify-model</c> on the same build reported the
/// model in sync. The cause was <c>Assembly.GetEntryAssembly()</c> seeding index discovery — under
/// a test host that is the test runner, not the application, so the catalog came up empty and the
/// querytype/index lines vanished from every projection-backed entity's shape. Fixed in
/// <c>SparkExtensions.UseContext</c>, which now anchors discovery on the context assembly.
/// <para>
/// The host is shared via <see cref="CoverageWebHostFixture"/>. <b>Do not construct a factory per
/// test:</b> Spark's registry, index catalog and model loader are process-wide, and concurrent
/// boots throw "Collection was modified; enumeration operation may not execute".
/// </para>
/// </remarks>
public class SparkAuthorizeEndToEndTests : IClassFixture<CoverageWebHostFixture>
{
    private readonly CoverageWebHostFixture fixture;

    public SparkAuthorizeEndToEndTests(CoverageWebHostFixture fixture) => this.fixture = fixture;

    private HttpClient CreateClient() => fixture.Factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// Booting the real composition root is itself the assertion. <c>Program.cs</c> is 291 lines
    /// that no test had ever executed, and the failure modes it hides are startup-only: a missing
    /// DI registration, a middleware ordering violation, a security.json that no longer matches the
    /// model. None of those can be caught by testing a controller in isolation.
    /// </summary>
    [Fact]
    public void The_application_starts()
    {
        using var client = CreateClient();

        Assert.NotNull(client);
    }

    /// <summary>
    /// The badge endpoint carries [AllowAnonymous], which beats the type-level [SparkAuthorize].
    /// Anonymous access is the entire point of a coverage badge.
    /// </summary>
    [Fact]
    public async Task An_anonymous_badge_request_is_not_challenged()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/badge/acme/does-not-exist.svg");

        // 404, or an "unknown" badge, are both fine. What must NOT happen is 401/403 — that would
        // mean authorization is being applied to a deliberately public endpoint.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The other half: an endpoint that is NOT anonymous must refuse an anonymous caller. With
    /// GitHub as the only provider a challenge is a redirect rather than a bare 401, so both shapes
    /// are accepted — what is asserted is that the request does not simply succeed.
    /// </summary>
    [Fact]
    public async Task An_anonymous_caller_cannot_reach_an_authorized_endpoint()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/me/accounts");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.Found
                or HttpStatusCode.Redirect,
            $"an anonymous caller reached /api/me/accounts and got {(int)response.StatusCode} "
            + $"{response.StatusCode}; the authorization filter did not run.");
    }

    /// <summary>
    /// Token and settings management are not anonymous either. Worth their own cases because both
    /// controllers carry <c>[SparkAuthorize]</c> at the TYPE level rather than per method — a
    /// refactor that moved the attribute onto individual actions could leave one uncovered, and
    /// unit tests of the methods would never notice, because a method call runs no filter.
    /// </summary>
    [Theory]
    [InlineData("/api/tokens?account=acme")]
    [InlineData("/api/repos/acme/widget/settings/gate")]
    public async Task Management_endpoints_refuse_anonymous_callers(string path)
    {
        using var client = CreateClient();

        var response = await client.GetAsync(path);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.Found
                or HttpStatusCode.Redirect,
            $"{path} answered {(int)response.StatusCode} to an anonymous caller.");
    }
}
