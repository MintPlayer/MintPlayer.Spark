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
    /// <summary>
    /// BLOCKED, and deliberately skipped rather than deleted: the infrastructure works and the
    /// remaining obstacle is a framework defect worth fixing, not a dead end.
    /// <para>
    /// Booting the app in-process throws <c>SparkModelOutOfSyncException</c> for exactly three
    /// entities -- Account, Build and Repository -- while <c>dotnet run --spark-verify-model</c>
    /// on the same build reports the model perfectly in sync, under the same environment name and
    /// against the same App_Data. So the model hash is NOT stable across hosting models, and the
    /// startup gate is therefore hostile to in-process integration testing of any Spark app.
    /// </para>
    /// <para>
    /// Ruled out by measurement: content root (fixed, and pointed at the app project),
    /// configuration timing (fixed with UseSetting), the environment name (verify is in sync as
    /// IntegrationTest), attribute descriptions (both builds load the identical
    /// CodeCoverage.Library.dll, and it carries them), test-parallelism (a single test in
    /// isolation fails the same way), and HasRefreshOverride (it feeds a diagnostic warning, not
    /// the hash).
    /// </para>
    /// <para>
    /// Do not work around this with SPARK_MODEL_HASH_OVERRIDE: the override deliberately takes a
    /// specific hash so it cannot become permanent, and baking one into a test would break on the
    /// next model change. The fix belongs in ModelSynchronizer.
    /// </para>
    /// </summary>
    private const string BlockedReason =
        "Blocked: the Spark model hash is not stable across hosting models, so the startup gate "
        + "rejects an in-process test host that dotnet run accepts. See the class remarks.";

    /// <summary>
    /// Booting the real composition root is itself the assertion here. `Program.cs` is 291 lines
    /// that no test had ever executed, and the failure modes it hides are startup-only: a missing
    /// DI registration, a middleware ordering analyzer violation, a security.json that no longer
    /// matches the model. None of those can be caught by testing a controller in isolation.
    /// </summary>
    [Fact(Skip = BlockedReason)]
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
    [Fact(Skip = BlockedReason)]
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
    [Fact(Skip = BlockedReason)]
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
