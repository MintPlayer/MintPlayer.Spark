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
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Services;

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

    /// <summary>
    /// A security.json whose <c>wellKnown</c> block names one group as <c>authenticated</c>.
    /// </summary>
    private sealed class StubSecurityLoader(string groupId, string displayName) : ISecurityConfigurationLoader
    {
        public SecurityConfiguration GetConfiguration() => new()
        {
            Groups = { [groupId] = TranslatedString.Create(displayName) },
            WellKnown = new Dictionary<string, string> { [SparkWellKnownGroups.Authenticated] = groupId },
        };

        public RightsDecision GetResolvedRights(IReadOnlySet<Guid> groupIds) => RightsDecision.None;
        public void InvalidateCache() { }
    }

    private const string WellKnownGroupId = "a1b2c3d4-0000-0000-0000-00000000000f";
    private const string WellKnownGroupName = "Signed-in users";

    private static async Task<IHost> StartAsync(
        IAccessControl accessControl,
        IGroupMembershipProvider? groups = null,
        ISecurityConfigurationLoader? securityLoader = null)
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
                    if (securityLoader is not null)
                        services.AddScoped(_ => securityLoader);
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

                        // The two ways an author would name a well-known group: by the id
                        // security.json declares, and by the display name that id resolves to.
                        endpoints.MapGet("/wellknown-by-id", () => Results.Ok())
                            .RequireAuthorization(new SparkAuthorizeAttribute { Group = WellKnownGroupId });

                        endpoints.MapGet("/wellknown-by-name", () => Results.Ok())
                            .RequireAuthorization(new SparkAuthorizeAttribute { Group = WellKnownGroupName });
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

    // ----------------------------------------------------------------------------------
    // #327 §9.4 — a well-known group cannot be demanded through the group form
    // ----------------------------------------------------------------------------------

    [Fact]
    public async Task Naming_the_authenticated_group_by_id_throws_rather_than_denying_forever()
    {
        // Well-known ids are decided from authentication state and deliberately excluded from
        // claim-derived membership, so this requirement could never be satisfied. Left as a
        // refusal it produced a 403 indistinguishable from an ordinary one, on an attribute that
        // reads as "signed-in users may do this" — a permanent silent lockout.
        using var host = await StartAsync(
            new StubAccessControl(),
            new StubGroups("Administrators"),
            new StubSecurityLoader(WellKnownGroupId, WellKnownGroupName));

        var act = () => host.GetTestClient().GetAsync("/wellknown-by-id");

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("authenticated").And.Contain("[SparkAuthorize(");
    }

    [Fact]
    public async Task Naming_the_authenticated_group_by_its_display_name_throws_too()
    {
        // An author is far likelier to write the readable name than the GUID, so matching only the
        // id would leave the common spelling of the mistake undetected.
        using var host = await StartAsync(
            new StubAccessControl(),
            new StubGroups("Administrators"),
            new StubSecurityLoader(WellKnownGroupId, WellKnownGroupName));

        var act = () => host.GetTestClient().GetAsync("/wellknown-by-name");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task An_ordinary_group_is_unaffected_by_the_well_known_check()
    {
        // The guard must not fire on the normal case: /administrators names a group that is not
        // declared well-known, and still authorizes exactly as before.
        using var host = await StartAsync(
            new StubAccessControl(),
            new StubGroups("Administrators"),
            new StubSecurityLoader(WellKnownGroupId, WellKnownGroupName));

        var response = await host.GetTestClient().GetAsync("/administrators");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task With_no_security_configuration_resolvable_the_group_form_still_works()
    {
        // The check resolves ISecurityConfigurationLoader optionally: outside a Spark-configured
        // host there is no wellKnown block to contradict, and the attribute must not start
        // throwing merely because the loader is absent.
        using var host = await StartAsync(new StubAccessControl(), new StubGroups("Administrators"));

        var response = await host.GetTestClient().GetAsync("/administrators");

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
