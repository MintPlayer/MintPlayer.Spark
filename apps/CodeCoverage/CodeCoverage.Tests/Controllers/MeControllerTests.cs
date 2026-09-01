using CodeCoverage.Controllers;
using CodeCoverage.Services;
using CodeCoverage.Tests.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents.Session;
using CodeCoverage.Tests;
using Raven.TestDriver;
using Xunit;

namespace CodeCoverage.Tests.Controllers;

/// <summary>
/// The reauth flag travels as a field on a 200 response — never as a status
/// code, because the SPA's auth interceptor hijacks 401s into /login.
/// </summary>
public class MeControllerTests : CoverageRavenTest
{
    private static MeController CreateController(IAsyncDocumentSession session, GitHubVisibility visibility)
    {
        var services = new ServiceCollection();
        services.AddSingleton(session);
        services.AddSingleton<IGitHubAccessService>(new ScriptedAccessService(visibility));
        services.AddSingleton(GitHubAuthTestFakes.TestConfiguration());
        services.AddSingleton<IWebHostEnvironment>(new FakeWebHostEnvironment());
        // The real aggregation, not a stub: it is shared with the Custom.MyAccounts
        // Spark query, and these tests are about what that aggregation reports —
        // stubbing it would leave the reauth flag asserted against nothing.
        services.AddScoped<IMyAccountsService, MyAccountsService>();
        services.AddScoped<MeController>();
        return services.BuildServiceProvider().GetRequiredService<MeController>();
    }

    private static MeController.AccountsResponse Body(ActionResult<MeController.AccountsResponse> result)
        => (MeController.AccountsResponse)((OkObjectResult)result.Result!).Value!;

    [Fact]
    public async Task Reauth_required_visibility_sets_the_flag_and_still_lists_the_degraded_account()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, new(["pieterjan"], GitHubTokenState.ReauthRequired));

        var response = Body(await controller.GetAccounts(CancellationToken.None));

        response.GitHubReauthRequired.Should().BeTrue();
        response.Accounts.Should().ContainSingle().Which.Login.Should().Be("pieterjan");
    }

    [Fact]
    public async Task Healthy_visibility_reports_no_reauth()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, new(["pieterjan"], GitHubTokenState.Ok));

        var response = Body(await controller.GetAccounts(CancellationToken.None));

        response.GitHubReauthRequired.Should().BeFalse();
    }

    [Fact]
    public async Task Unavailable_is_not_reauth__transient_failure_must_not_summon_the_reconnect_banner()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var controller = CreateController(session, new(["pieterjan"], GitHubTokenState.Unavailable));

        var response = Body(await controller.GetAccounts(CancellationToken.None));

        response.GitHubReauthRequired.Should().BeFalse();
    }
}
