using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Services;

namespace MintPlayer.Spark.Tests.Authorization;

/// <summary>
/// #301 — <c>[SparkAuthorize]</c>. Before it, there was no <c>[Authorize]</c> interop at all:
/// <c>UseSpark()</c> registers a bare <c>AddAuthorization()</c> with no policies, so
/// <c>[Authorize(Policy = "Administrators")]</c> threw at request time, and
/// <c>[Authorize(Roles = …)]</c> worked only when the group happened to be stored as an Identity
/// role — which the identity provider, the E2E fixtures and module certificates do not do.
/// </summary>
public class SparkAuthorizeAttributeTests
{
    private const string TestScheme = "TestScheme";

    private sealed class AlwaysSignedInHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester")], TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }

    /// <summary>Grants exactly the rights it was handed, so a test states its own model in one line.</summary>
    private sealed class StubAccessControl(params string[] granted) : IAccessControl
    {
        public Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default)
            => Task.FromResult(granted.Contains(resource, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class StubGroups(params string[] groups) : IGroupMembershipProvider
    {
        public Task<IEnumerable<string>> GetCurrentUserGroupsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<string>>(groups);
    }

    private static async Task<IHost> StartAsync(IAccessControl accessControl, IGroupMembershipProvider? groups = null)
        => await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication(TestScheme)
                        .AddScheme<AuthenticationSchemeOptions, AlwaysSignedInHandler>(TestScheme, null);
                    services.AddAuthorization();
                    services.AddSingleton<IAuthorizationHandler, SparkAuthorizeHandler>();
                    services.AddScoped(_ => accessControl);
                    if (groups is not null)
                        services.AddScoped(_ => groups);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/read-person", () => Results.Ok())
                            .RequireAuthorization(new SparkAuthorizeAttribute("Read", "Person"));

                        endpoints.MapGet("/administrators", () => Results.Ok())
                            .RequireAuthorization(new SparkAuthorizeAttribute { Group = "Administrators" });

                        endpoints.MapGet("/anonymous", () => Results.Ok())
                            .RequireAuthorization(new SparkAuthorizeAttribute("Read", "Person"))
                            .AllowAnonymous();
                    });
                }))
            .StartAsync();

    [Fact]
    public async Task A_granted_right_authorizes_the_endpoint()
    {
        using var host = await StartAsync(new StubAccessControl("Read/Person"));

        var response = await host.GetTestClient().GetAsync("/read-person");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_denied_right_refuses_the_endpoint()
    {
        using var host = await StartAsync(new StubAccessControl("Read/Company"));

        var response = await host.GetTestClient().GetAsync("/read-person");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task It_demands_the_same_string_the_pipeline_checks()
    {
        // The reason the right form is primary: a controller and its equivalent persistent-object
        // endpoint provably ask the same question, rather than agreeing by convention.
        var asked = new List<string>();
        var accessControl = new RecordingAccessControl(asked);

        using var host = await StartAsync(accessControl);
        await host.GetTestClient().GetAsync("/read-person");

        asked.Should().ContainSingle().Which.Should().Be("Read/Person");
    }

    [Fact]
    public async Task The_group_form_resolves_through_the_membership_provider()
    {
        using var host = await StartAsync(new StubAccessControl(), new StubGroups("Administrators"));

        var response = await host.GetTestClient().GetAsync("/administrators");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_group_form_refuses_a_caller_outside_the_group()
    {
        using var host = await StartAsync(new StubAccessControl(), new StubGroups("Readers"));

        var response = await host.GetTestClient().GetAsync("/administrators");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_group_form_refuses_when_no_membership_provider_is_registered()
    {
        // Fail closed. An app that names a group but wired no provider has expressed an intent that
        // cannot be evaluated, and silently succeeding would grant everyone.
        using var host = await StartAsync(new StubAccessControl());

        var response = await host.GetTestClient().GetAsync("/administrators");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AllowAnonymous_overrides_it()
    {
        using var host = await StartAsync(new StubAccessControl());

        var response = await host.GetTestClient().GetAsync("/anonymous");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class RecordingAccessControl(List<string> asked) : IAccessControl
    {
        public Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default)
        {
            asked.Add(resource);
            return Task.FromResult(true);
        }
    }
}
