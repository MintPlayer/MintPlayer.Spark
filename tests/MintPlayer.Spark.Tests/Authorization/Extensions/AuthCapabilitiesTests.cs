using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
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
/// Pins <c>GET /spark/auth/capabilities</c> — the channel that stops the server's auth
/// configuration and the client's from silently disagreeing.
/// </summary>
public class AuthCapabilitiesTests : SparkTestDriver
{
    private async Task<IHost> StartAsync(SparkLocalCredentials mode)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IDocumentStore>(Store);
                    services.AddSparkAuthentication<SparkUser>();

                    // Two providers a human can click, plus one machine-only scheme that must not
                    // be offered as a sign-in button.
                    services.AddAuthentication()
                        .AddCookie("GitHub", "GitHub", _ => { })
                        .AddCookie("Google", "Google", _ => { })
                        .AddCookie("ApiToken", displayName: null, _ => { });

                    services.AddAuthorization();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapSparkIdentityApi<SparkUser>(mode));
                }))
            .StartAsync();
    }

    private static async Task<JsonElement> GetCapabilitiesAsync(IHost host)
    {
        using var client = host.GetTestServer().CreateClient();
        var response = await client.GetAsync("/spark/auth/capabilities");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Theory]
    [InlineData(SparkLocalCredentials.Full, "Full")]
    [InlineData(SparkLocalCredentials.SignInOnly, "SignInOnly")]
    [InlineData(SparkLocalCredentials.Disabled, "Disabled")]
    public async Task Capabilities_reports_the_configured_mode(SparkLocalCredentials mode, string expected)
    {
        using var host = await StartAsync(mode);

        var body = await GetCapabilitiesAsync(host);

        body.GetProperty("localCredentials").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task Capabilities_reports_the_registered_external_providers()
    {
        using var host = await StartAsync(SparkLocalCredentials.Disabled);

        var body = await GetCapabilitiesAsync(host);
        var schemes = body.GetProperty("externalProviders")
            .EnumerateArray()
            .Select(provider => provider.GetProperty("scheme").GetString())
            .ToArray();

        schemes.Should().BeEquivalentTo(["GitHub", "Google"]);
    }

    [Fact]
    public async Task Capabilities_omits_non_interactive_schemes()
    {
        // The scheme table mixes interactive providers with machine-caller credential schemes and
        // Identity's own internal cookies. Only the first belongs on a sign-in page — and offering
        // a bearer or certificate scheme as a button would be a dead end for the user.
        using var host = await StartAsync(SparkLocalCredentials.Disabled);

        var body = await GetCapabilitiesAsync(host);
        var schemes = body.GetProperty("externalProviders")
            .EnumerateArray()
            .Select(provider => provider.GetProperty("scheme").GetString())
            .ToArray();

        schemes.Should().NotContain("ApiToken");
        schemes.Should().NotContain(IdentityConstants.ApplicationScheme);
        schemes.Should().NotContain(IdentityConstants.ExternalScheme);
        schemes.Should().NotContain(IdentityConstants.BearerScheme);
    }

    [Fact]
    public async Task Capabilities_is_reachable_anonymously()
    {
        // It is the page an unauthenticated visitor lands on. Requiring auth to discover how to
        // authenticate would be circular.
        using var host = await StartAsync(SparkLocalCredentials.Disabled);
        using var client = host.GetTestServer().CreateClient();

        var response = await client.GetAsync("/spark/auth/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
