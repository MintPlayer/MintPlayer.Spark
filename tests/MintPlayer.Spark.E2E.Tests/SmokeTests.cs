using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests;

/// <summary>
/// Broadly exercises every library that has a browser-visible surface:
/// <list type="bullet">
/// <item>MintPlayer.Spark (HTTP endpoints + antiforgery)</item>
/// <item>MintPlayer.Spark.Authorization (identity + /spark/auth/me)</item>
/// <item>@mintplayer/ng-spark (Angular SPA served from dist/)</item>
/// <item>@mintplayer/ng-spark-auth (login/register routes)</item>
/// </list>
/// Each test spins up a fresh <see cref="IBrowserContext"/> (fresh cookies). The
/// <see cref="FleetE2ECollectionFixture"/> owns Fleet + Raven for the whole session.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class SmokeTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public SmokeTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Fleet_serves_the_Angular_SPA_at_root()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var response = await page.GotoAsync("/");

        response!.Status.Should().BeLessThan(500);
        var html = await page.ContentAsync();
        html.Should().Contain("<app-root", "the ng-spark app shell should be rendered");
    }

    [Fact]
    public async Task Spark_auth_me_reports_unauthenticated_for_a_fresh_context()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        var response = await page.APIRequest.GetAsync($"{_fixture.Host.FleetUrl}/spark/auth/me");

        response.Status.Should().Be(200);
        var body = await response.JsonAsync();
        body!.Value.GetProperty("isAuthenticated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Login_with_admin_credentials_grants_an_authenticated_session_visible_through_me()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        // Through the shared helper rather than a raw POST: /spark/auth/login is antiforgery-gated
        // since 11.0.0 (login CSRF), so signing in takes a primed token and BrowserSignIn owns that.
        // It throws on a non-OK status, so the assertion it replaces is not lost.
        await BrowserSignIn.SignInAsync(
            page, _fixture.Host.FleetUrl, _fixture.Host.AdminEmailAddress, _fixture.Host.AdminPass);

        // Subsequent /me call on the same context (cookie-backed) should now report authenticated.
        var meResponse = await page.APIRequest.GetAsync($"{_fixture.Host.FleetUrl}/spark/auth/me");
        meResponse.Status.Should().Be(200);
        var me = await meResponse.JsonAsync();
        me!.Value.GetProperty("isAuthenticated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Ng_spark_auth_login_route_renders_a_sign_in_form()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();

        // Default ng-spark-auth path is /login (sparkAuthRoutes()).
        await page.GotoAsync("/login");

        // Angular lazy-loads the component; wait for at least one password field to appear.
        var passwordField = page.Locator("input[type='password']").First;
        await passwordField.WaitForAsync(new() { Timeout = 15_000 });
        await passwordField.IsVisibleAsync().ContinueWith(_ => { });

        (await passwordField.CountAsync()).Should().BeGreaterThan(0);
    }
}
