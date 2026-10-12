using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Mcp;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Choosing the provider's flows and endpoints (<c>Flows</c>, <c>Endpoints</c>; MCP PRD decision log, "IdP surface for
/// MCP-only apps"): the default keeps every flow and endpoint, a chosen set is mapped and advertised and nothing else is,
/// and a set that cannot work refuses startup, naming the dependency.
/// </summary>
public class OidcSurfaceTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private const string Issuer = "https://idp.test";

    private static IEnumerable<string> Strings(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<string>());

    [Fact]
    public async Task The_default_provider_keeps_every_flow_and_endpoint()
    {
        await using var factory = OidcTestHost.CreateFactory(Store);
        var client = factory.CreateClient();

        var discovery = (await client.GetFromJsonAsync<JsonObject>("/.well-known/openid-configuration"))!;
        Strings(discovery["grant_types_supported"]).Should().Equal(
            "authorization_code", "refresh_token", "client_credentials",
            "urn:ietf:params:oauth:grant-type:device_code", "urn:ietf:params:oauth:grant-type:token-exchange");
        var endpoints = new Dictionary<string, string>
        {
            ["authorization_endpoint"] = "/connect/authorize",
            ["token_endpoint"] = "/connect/token",
            ["userinfo_endpoint"] = "/connect/userinfo",
            ["end_session_endpoint"] = "/connect/logout",
            ["introspection_endpoint"] = "/connect/introspect",
            ["revocation_endpoint"] = "/connect/revoke",
            ["pushed_authorization_request_endpoint"] = "/connect/par",
            ["device_authorization_endpoint"] = "/connect/device_authorization",
            ["registration_endpoint"] = "/connect/register",
            ["jwks_uri"] = "/.well-known/jwks",
        };
        foreach (var (key, path) in endpoints)
            (discovery[key]?.GetValue<string>()).Should().Be(Issuer + path, key);
        discovery["backchannel_logout_supported"]!.GetValue<bool>().Should().BeTrue();
        (await McpAgentsOnlyProviderTests.UnmappedAdvertisedAsync(client, discovery, Issuer)).Should().BeEmpty();
        (await McpAgentsOnlyProviderTests.MappedAsync(client, McpAgentsOnlyProviderTests.NotForAgents)).Should().HaveCount(
            McpAgentsOnlyProviderTests.NotForAgents.Length, "a full provider maps every one of them");
    }

    [Fact]
    public async Task A_chosen_set_is_mapped_and_advertised_and_nothing_else()
    {
        await using var factory = OidcTestHost.CreateFactory(Store, idp =>
        {
            idp.Flows = SparkIdentityProviderFlows.AuthorizationCode | SparkIdentityProviderFlows.ClientCredentials;
            idp.Endpoints = SparkIdentityProviderEndpoints.Introspection | SparkIdentityProviderEndpoints.UserInfo;
        });
        var client = factory.CreateClient();

        var discovery = (await client.GetFromJsonAsync<JsonObject>("/.well-known/openid-configuration"))!;
        Strings(discovery["grant_types_supported"]).Should().Equal("authorization_code", "client_credentials");
        var keys = discovery.Select(p => p.Key).ToList();
        foreach (var key in new[] { "authorization_endpoint", "token_endpoint", "userinfo_endpoint", "introspection_endpoint", "jwks_uri" })
            keys.Should().Contain(key);
        keys.Should().NotIntersectWith([
            "revocation_endpoint", "end_session_endpoint", "pushed_authorization_request_endpoint", "device_authorization_endpoint",
            "registration_endpoint", "revocation_endpoint_auth_methods_supported", "backchannel_logout_supported",
        ]);
        (await McpAgentsOnlyProviderTests.UnmappedAdvertisedAsync(client, discovery, Issuer)).Should().BeEmpty();

        (await McpAgentsOnlyProviderTests.MappedAsync(client,
        [
            ("POST", "/connect/revoke"), ("GET", "/connect/logout"), ("POST", "/connect/par"), ("POST", "/connect/device_authorization"),
            ("GET", "/connect/device"), ("POST", "/connect/register"), ("GET", "/connect/applications"),
            ("GET", "/spark/identity-provider/developer"), ("GET", "/spark/identity-provider/admin/keys"),
        ])).Should().BeEmpty("only the chosen endpoints are mapped");

        foreach (var grant in new[] { "refresh_token", "urn:ietf:params:oauth:grant-type:device_code" })
        {
            var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = grant, ["client_id"] = "any-client", ["refresh_token"] = "any-token", ["device_code"] = "any-code",
            }));
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            JsonNode.Parse(body)!["error"]!.GetValue<string>().Should().Be("unsupported_grant_type", grant);
        }
    }

    [Theory]
    [InlineData(SparkIdentityProviderFlows.None, SparkIdentityProviderEndpoints.None, "Flows is None")]
    [InlineData(SparkIdentityProviderFlows.RefreshToken | SparkIdentityProviderFlows.ClientCredentials, SparkIdentityProviderEndpoints.None, "Flows.RefreshToken needs Flows.AuthorizationCode or Flows.DeviceCode")]
    [InlineData(SparkIdentityProviderFlows.ClientCredentials, SparkIdentityProviderEndpoints.PushedAuthorization, "Endpoints.PushedAuthorization needs Flows.AuthorizationCode")]
    [InlineData(SparkIdentityProviderFlows.ClientCredentials, SparkIdentityProviderEndpoints.UserInfo, "Endpoints.UserInfo needs Flows.AuthorizationCode or Flows.DeviceCode")]
    [InlineData(SparkIdentityProviderFlows.ClientCredentials, SparkIdentityProviderEndpoints.EndSession, "Endpoints.EndSession needs Flows.AuthorizationCode or Flows.DeviceCode")]
    [InlineData(SparkIdentityProviderFlows.ClientCredentials, SparkIdentityProviderEndpoints.ConnectedApplications, "Endpoints.ConnectedApplications needs Flows.AuthorizationCode or Flows.DeviceCode")]
    [InlineData(SparkIdentityProviderFlows.All, SparkIdentityProviderEndpoints.DeveloperRegistration, "Endpoints.DeveloperRegistration needs Endpoints.DeveloperPortal")]
    public void A_combination_that_cannot_work_refuses_startup(SparkIdentityProviderFlows flows, SparkIdentityProviderEndpoints endpoints, string expected)
    {
        var act = () => OidcTestHost.CreateFactory(Store, idp =>
        {
            idp.Flows = flows;
            idp.Endpoints = endpoints;
        }).CreateClient();

        act.Should().Throw<Exception>().Which.ToString().Should().Contain(expected);
    }

    [Fact]
    public void Flows_and_endpoints_bind_from_configuration_as_comma_separated_names()
    {
        var options = new SparkIdentityProviderOptions();
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Flows"] = "AuthorizationCode, RefreshToken",
                ["Endpoints"] = "Revocation, UserInfo",
            })
            .Build()
            .Bind(options);

        options.Flows.Should().Be(SparkIdentityProviderFlows.AuthorizationCode | SparkIdentityProviderFlows.RefreshToken);
        options.Endpoints.Should().Be(SparkIdentityProviderEndpoints.Revocation | SparkIdentityProviderEndpoints.UserInfo);
        new SparkIdentityProviderOptions().Flows.Should().Be(SparkIdentityProviderFlows.All, "nothing changes for a provider that chooses nothing");
        new SparkIdentityProviderOptions().Endpoints.Should().Be(SparkIdentityProviderEndpoints.All);
    }
}
