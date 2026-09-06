using System.Net;
using CodeCoverage.Tests._Infrastructure;
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
public class SparkAuthorizeEndToEndTests : CoverageRavenTest
{
    // These were skipped for one session with a note saying the model hash was unstable across
    // hosting models. It was: booting in-process threw SparkModelOutOfSyncException for exactly
    // Account, Build and Repository while --spark-verify-model on the same build reported the model
    // in sync. The cause was Assembly.GetEntryAssembly() seeding index discovery -- under a test
    // host that is the test runner, not the application, so the index catalog came up empty and the
    // querytype/index lines vanished from every projection-backed entity shape. Fixed in
    // SparkExtensions.UseContext, which now anchors discovery on the context assembly.
    //
    // Kept as a comment because the failure was invisible in exactly the way this whole test class
    // exists to catch: the app reported a model problem, and the actual defect was in discovery.

    /// <summary>
    /// Booting the real composition root is itself the assertion here. `Program.cs` is 291 lines
    /// that no test had ever executed, and the failure modes it hides are startup-only: a missing
    /// DI registration, a middleware ordering analyzer violation, a security.json that no longer
    /// matches the model. None of those can be caught by testing a controller in isolation.
    /// </summary>
    [Fact]
    public async Task The_application_starts()
    {
        using var store = GetDocumentStore();
        await using var factory = new CoverageWebAppFactory(store);

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    /// <summary>
    /// The badge endpoint carries [AllowAnonymous], which beats the type-level [SparkAuthorize].
    /// Anonymous access is the entire point of a coverage badge, so this pins it: anything other
    /// than a challenge means the public surface still answers.
    /// </summary>
    [Fact]
    public async Task An_anonymous_badge_request_is_not_challenged()
    {
        using var store = GetDocumentStore();
        await using var factory = new CoverageWebAppFactory(store);

        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/badge/acme/does-not-exist.svg");

        // 404 or an "unknown" badge are both fine — what must NOT happen is 401/403, which would
        // mean authorization is being applied to a deliberately public endpoint.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The other half: an endpoint that is NOT anonymous must refuse an anonymous caller. With
    /// GitHub as the only provider, a challenge is a redirect to GitHub rather than a bare 401,
    /// so both shapes are accepted — what is asserted is that the request does not simply succeed.
    /// </summary>
    [Fact]
    public async Task An_anonymous_caller_cannot_reach_an_authorized_endpoint()
    {
        using var store = GetDocumentStore();
        await using var factory = new CoverageWebAppFactory(store);

        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/me/accounts");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.Found
                or HttpStatusCode.Redirect,
            $"an anonymous caller reached /api/me/accounts and got {(int)response.StatusCode} "
            + $"{response.StatusCode}; the authorization filter did not run.");
    }
}
