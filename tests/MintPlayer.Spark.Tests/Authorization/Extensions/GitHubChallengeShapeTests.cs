using System.Net;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// Issue #296 — the shape of the authorization redirect Spark sends a user to.
/// </summary>
/// <remarks>
/// Nothing in the suite asserted this before, which is how <c>AddGitHub</c> came to request no scope
/// at all while the provisioning gate required an issuer-attested email that only the
/// <c>user:email</c> scope can obtain. The two were mutually unsatisfiable, and every existing test
/// stayed green because they all short-circuit to Identity's <c>ExternalScheme</c> cookie with a
/// ready-made claim rather than forming a real challenge.
/// <para>
/// <c>CallbackPath</c> in particular is a bare string literal that no other test reads, yet it must
/// match what is registered on the GitHub App — a mismatch is invisible here and fatal in production.
/// </para>
/// <para>
/// What this deliberately cannot cover: that the deployed App's registered callback URL and granted
/// permissions actually match. Those live on github.com.
/// </para>
/// </remarks>
public class GitHubChallengeShapeTests : SparkTestDriver
{
    private const string ClientId = "test-client-id";

    private async Task<IHost> StartAsync()
    {
        return await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    new IdentityBuilder(typeof(SparkUser), services).AddGitHub(options =>
                    {
                        options.ClientId = ClientId;
                        options.ClientSecret = "test-client-secret";
                    });
                    services.AddAuthorization();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapSparkIdentityApi<SparkUser>(SparkLocalCredentials.Disabled));
                }))
            .StartAsync();
    }

    /// <summary>A client that does not follow the redirect, so the Location header survives.</summary>
    private static HttpClient NonFollowing(IHost host) =>
        host.GetTestServer().CreateClient();

    private async Task<Uri> ChallengeLocationAsync(IHost host)
    {
        var response = await NonFollowing(host).GetAsync("/spark/auth/external-login?provider=GitHub&returnUrl=/dashboard");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location.Should().NotBeNull();
        return response.Headers.Location!;
    }

    [Fact]
    public async Task The_github_challenge_redirects_to_the_authorization_endpoint()
    {
        using var host = await StartAsync();

        var location = await ChallengeLocationAsync(host);

        location.Host.Should().Be("github.com");
        location.AbsolutePath.Should().Be("/login/oauth/authorize");
    }

    [Fact]
    public async Task The_github_challenge_carries_the_configured_client_id_and_a_state()
    {
        using var host = await StartAsync();

        var query = System.Web.HttpUtility.ParseQueryString((await ChallengeLocationAsync(host)).Query);

        query["client_id"].Should().Be(ClientId);
        query["response_type"].Should().Be("code");
        query["state"].Should().NotBeNullOrEmpty("the correlation/state round trip depends on it");
    }

    /// <summary>
    /// The one value the provider must be configured to match. It is derived by ASP.NET from the
    /// request's scheme+host plus <c>CallbackPath</c>, never composed by Spark — so this is the only
    /// place the literal is pinned.
    /// </summary>
    [Fact]
    public async Task The_github_challenge_requests_the_registered_callback_path()
    {
        using var host = await StartAsync();

        var query = System.Web.HttpUtility.ParseQueryString((await ChallengeLocationAsync(host)).Query);

        query["redirect_uri"].Should().NotBeNull();
        new Uri(query["redirect_uri"]!).AbsolutePath.Should().Be("/signin-github");
    }

    /// <summary>
    /// RED before #296: no scope was requested at all, so an OAuth App's token could not read
    /// /user/emails, no verified-email claim was issued, and first-time provisioning was impossible.
    /// </summary>
    [Fact]
    public async Task The_github_challenge_requests_the_user_email_scope()
    {
        using var host = await StartAsync();

        var query = System.Web.HttpUtility.ParseQueryString((await ChallengeLocationAsync(host)).Query);

        query["scope"].Should().NotBeNullOrEmpty(
            "auto-provisioning needs an issuer-attested email, which requires user:email");
        query["scope"].Should().Contain("user:email");
    }

    /// <summary>
    /// A consumer must still be able to override the default — the scope is added before the
    /// caller's configure callback runs.
    /// </summary>
    [Fact]
    public async Task A_consumer_can_override_the_default_scope()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();
                    new IdentityBuilder(typeof(SparkUser), services).AddGitHub(options =>
                    {
                        options.ClientId = ClientId;
                        options.ClientSecret = "test-client-secret";
                        options.Scope.Clear();
                        options.Scope.Add("read:org");
                    });
                    services.AddAuthorization();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapSparkIdentityApi<SparkUser>(SparkLocalCredentials.Disabled));
                }))
            .StartAsync();

        var query = System.Web.HttpUtility.ParseQueryString((await ChallengeLocationAsync(host)).Query);

        query["scope"].Should().Be("read:org");
    }
}
