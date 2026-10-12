using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;
using Fleet.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Shared;
using Raven.Client.Documents;

namespace Fleet.Tests;

/// <summary>
/// Fleet's own authorization server for AI agents (setup 3, <c>idp.ForMcp()</c> in <c>Program.cs</c>), beside its SparkId
/// sign-in and its SparkId resource server: Fleet's real <c>Program</c> with the Development wiring (an issuer, the MCP
/// canonical URL, the SparkId provider), an agent that registers through a Client ID Metadata Document or anonymous
/// loopback registration, signs the user in at Fleet's <c>/connect</c> pages, and reads cars on <c>/mcp</c> as that user,
/// row security included. The scripted agent follows the M3b tests (<c>McpEmbeddedAuthorizationServerTests</c>), whose
/// helpers live in the library's test project; one application per test process (see <c>SparkAppRouteHost</c>).
/// </summary>
public class McpEmbeddedServerTests : SparkTestDriver
{
    private const string Issuer = "https://fleet.example";
    private const string CanonicalUrl = "https://fleet.example/mcp";
    private const string Password = "Correct-horse-battery-1";
    private const string Redirect = "http://127.0.0.1:53682/callback";
    private const string Administrators = "Administrators";
    private const string CarsQueryId = "a20e8400-e29b-41d4-a716-446655440001";

    [Fact]
    public async Task A_metadata_document_agent_signs_in_at_Fleet_and_reads_only_the_users_cars()
    {
        const string clientId = "https://agent.example/fleet/claude.json";
        await using var host = new FleetAgentHost(Store, new DocumentServer().Serve(clientId, Document(clientId)));
        var userId = await CreateViewerAsync(host, "viewer@fleet.test");
        await SeedCarsAsync(userId);
        var browser = host.Browser();

        // Fleet's provider sign-in page offers the SparkId sign-in beside the password form: the two coexist.
        var loginPage = await browser.GetStringAsync("/connect/login?returnUrl=%2F");
        loginPage.Should().Contain("provider=SparkId", "Fleet's provider sign-in page offers the SparkId sign-in beside the password form");

        var agent = new Agent(browser, host.CreateClient(), clientId);
        await agent.SignInAsync("viewer@fleet.test", Password);
        var (consentPage, requestId) = await agent.ConsentPageAsync();
        consentPage.Should().Contain("data-agent-trust=\"verified\"").And.Contain("value=\"Car\"");
        consentPage.Should().NotContain("value=\"write\"", "Fleet's AgentPermissions is Read");
        var tokens = await agent.RedeemAsync(await agent.CodeAsync(consentPage, requestId));

        var access = tokens["access_token"]!.GetValue<string>();
        var payload = JsonNode.Parse(Base64Url(access.Split('.')[1]))!;
        var audiences = payload["aud"] is JsonArray array ? array.Select(a => a!.GetValue<string>()).ToList() : [payload["aud"]!.GetValue<string>()];
        audiences.Should().Equal([CanonicalUrl], "Fleet's /mcp and nothing else");
        payload["sub"]!.GetValue<string>().Should().Be(userId);
        tokens["refresh_token"].Should().NotBeNull();

        var cars = await Mcp.CallAsync(host.CreateClient(), "fleet_query", new { query = "cars" }, access);
        cars.Status.Should().Be(HttpStatusCode.OK);
        cars.Text.Should().Contain("1-AGT-001");
        cars.Text.Should().NotContain("1-AGT-002", "another user's car: Fleet's row filter applies to the agent as to the user");
    }

    [Fact]
    public async Task A_dynamically_registered_agent_signs_in_at_Fleet_and_an_anonymous_one_sees_only_public_types()
    {
        await using var host = new FleetAgentHost(Store, new DocumentServer());
        await CreateViewerAsync(host, "dcr@fleet.test");
        var client = host.CreateClient();

        var anonymous = await Mcp.CallAsync(client, "fleet_discover", new { }, token: null);
        anonymous.Status.Should().Be(HttpStatusCode.OK);
        anonymous.Text.Should().Contain("\"Company\"", "anonymous visitors may read companies");
        anonymous.Text.Should().NotContain("\"Car\"");

        var registered = await client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Gemini CLI",
            redirect_uris = new[] { "http://localhost:7777/oauth/callback" },
            token_endpoint_auth_method = "none",
        });
        registered.StatusCode.Should().Be(HttpStatusCode.Created);
        var clientId = (await registered.Content.ReadFromJsonAsync<JsonObject>())!["client_id"]!.GetValue<string>();

        var agent = new Agent(host.Browser(), host.CreateClient(), clientId);
        await agent.SignInAsync("dcr@fleet.test", Password);
        var (consentPage, requestId) = await agent.ConsentPageAsync();
        consentPage.Should().Contain("data-agent-trust=\"unverified\"");
        var tokens = await agent.RedeemAsync(await agent.CodeAsync(consentPage, requestId));

        var discover = await Mcp.CallAsync(client, "fleet_discover", new { }, tokens["access_token"]!.GetValue<string>());
        discover.Status.Should().Be(HttpStatusCode.OK);
        discover.Text.Should().Contain("\"Car\"", "a Viewer's groups, read from the store for the agent's token");
    }

    /// <summary>
    /// An administrator's agent sees the cars the administrator's cookie sees (<c>CarActions</c> tests
    /// <c>User.IsInRole("Administrators")</c>), and losing the role narrows the agent's very next call to the cars the user
    /// created: an agent's roles come from the store on every call (S4), like its groups. Red before the agent principal
    /// carried the cookie's role claims: the agent saw only the administrator's own car.
    /// </summary>
    [Fact]
    public async Task An_administrators_agent_sees_the_cars_the_cookie_sees_until_the_role_is_removed()
    {
        const string clientId = "https://agent.example/fleet/admin.json";
        await using var host = new FleetAgentHost(Store, new DocumentServer().Serve(clientId, Document(clientId)));
        var userId = await CreateViewerAsync(host, "admin@fleet.test");
        await AddRoleToDocumentAsync(userId, Administrators);
        await SeedCarsAsync(userId);
        var browser = host.Browser();
        var agent = new Agent(browser, host.CreateClient(), clientId);
        await agent.SignInAsync("admin@fleet.test", Password);

        using var web = await browser.PostAsJsonAsync("/spark/queries/execute", new { queryId = CarsQueryId });
        var webCars = await web.Content.ReadAsStringAsync();
        web.StatusCode.Should().Be(HttpStatusCode.OK, webCars);
        webCars.Should().Contain("1-AGT-001").And.Contain("1-AGT-002", "an administrator's cookie sees every car");

        var (consentPage, requestId) = await agent.ConsentPageAsync();
        var access = (await agent.RedeemAsync(await agent.CodeAsync(consentPage, requestId)))["access_token"]!.GetValue<string>();

        var cars = await Mcp.CallAsync(host.CreateClient(), "fleet_query", new { query = "cars" }, access);
        cars.Status.Should().Be(HttpStatusCode.OK);
        cars.Text.Should().Contain("1-AGT-001").And.Contain("1-AGT-002", "the agent carries the cookie's roles, so it sees the cookie's cars");

        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>();
            (await users.RemoveFromRoleAsync((await users.FindByIdAsync(userId))!, Administrators)).Succeeded.Should().BeTrue();
        }

        var narrowed = await Mcp.CallAsync(host.CreateClient(), "fleet_query", new { query = "cars" }, access);
        narrowed.Status.Should().Be(HttpStatusCode.OK, "a role change is no security-stamp change: the agent stays signed in, as a Viewer");
        narrowed.Text.Should().Contain("1-AGT-001");
        narrowed.Text.Should().NotContain("1-AGT-002", "the next call reads the roles from the store (S4)");
    }

    /// <summary>
    /// The role as Fleet's administrators hold it: the group's name written on the user document, as the E2E seed writes
    /// it. <c>CarActions</c> compares <c>IsInRole("Administrators")</c> exactly, and <c>UserManager.AddToRoleAsync</c>
    /// stores the normalized name.
    /// </summary>
    private async Task AddRoleToDocumentAsync(string userId, string role)
    {
        using var session = Store.OpenAsyncSession();
        var user = await session.LoadAsync<SparkUser>(userId);
        user.Roles.Add(role);
        await session.SaveChangesAsync();
    }

    private static async Task<string> CreateViewerAsync(FleetAgentHost host, string email)
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>();
        var user = new SparkUser { UserName = email[..email.IndexOf('@')], Email = email, EmailConfirmed = true };
        var created = await users.CreateAsync(user, Password);
        created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));
        (await users.AddToRoleAsync(user, "Viewers")).Succeeded.Should().BeTrue();
        return user.Id!;
    }

    private async Task SeedCarsAsync(string userId)
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Car { LicensePlate = "1-AGT-001", Brand = "Volvo", Model = "XC40", CreatedBy = userId });
            await session.StoreAsync(new Car { LicensePlate = "1-AGT-002", Brand = "Volvo", Model = "XC60", CreatedBy = "SparkUsers/someone-else" });
            await session.SaveChangesAsync();
        }
        await Store.WaitForIndexingAsync();
    }

    private static string Document(string clientId) => new JsonObject
    {
        ["client_id"] = clientId,
        ["client_name"] = "Claude Code",
        ["redirect_uris"] = new JsonArray("http://localhost/callback", "http://127.0.0.1/callback"),
        ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
        ["response_types"] = new JsonArray("code"),
        ["token_endpoint_auth_method"] = "none",
    }.ToJsonString();

    private static string Base64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    /// <summary>Fleet's real <c>Program</c>, its Development wiring by settings, no background services, metadata documents from a stub.</summary>
    private sealed class FleetAgentHost(IDocumentStore store, DocumentServer documents) : WebApplicationFactory<FleetContext>
    {
        public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Not Development: that would start the Angular dev server (UseAngularCliServer).
            builder.UseEnvironment("FleetAgentTest");
            builder.UseContentRoot(AppDirectory());

            var settings = new Dictionary<string, string?>
            {
                ["Spark:RavenDb:Urls:0"] = store.Urls[0],
                ["Spark:RavenDb:Database"] = store.Database,
                ["Spark:DataProtection:KeysPath"] = Path.Combine(Path.GetTempPath(), $"fleet-agent-tests-dataprotection-{Environment.ProcessId}"),
                ["Spark:HttpsRedirection"] = "false",
                ["Spark:Replication:ModuleUrl"] = "http://localhost",
                ["Spark:Replication:SparkModulesUrls:0"] = store.Urls[0],
                ["Spark:Replication:SparkModulesDatabase"] = store.Database + "-modules",
                // What appsettings.Development.json sets: the SparkId sign-in and resource server, and the agents' server.
                ["Spark:Auth:Providers:OpenIdConnect:SparkId:DisplayName"] = "Spark Identity",
                ["Spark:Auth:Providers:OpenIdConnect:SparkId:Authority"] = "https://sparkid.example",
                ["Spark:Auth:Providers:OpenIdConnect:SparkId:ClientId"] = "fleet",
                ["Spark:Auth:Providers:OpenIdConnect:SparkId:ClientSecret"] = "test-only",
                ["Spark:JwtBearer:Authority"] = "https://sparkid.example",
                ["Spark:JwtBearer:Audience"] = "fleet",
                ["Spark:IdentityProvider:Issuer"] = Issuer,
                ["Spark:IdentityProvider:SigningKeyPath"] = SparkAppSigningKey.NewFile(),
                ["Spark:Mcp:CanonicalUrl"] = CanonicalUrl,
            };
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);

            builder.ConfigureTestServices(services =>
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name != "GenericWebHostService").ToList())
                    services.Remove(descriptor);
                // The metadata-document fetch (the SSRF-safe client) answers from the stub.
                services.AddHttpClient("Spark.IdentityProvider.ClientMetadataDocument").ConfigurePrimaryHttpMessageHandler(() => documents);
            });
        }

        private static string AppDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nx.json")))
                dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("No nx.json above the test output."), "apps", "Fleet", "Fleet");
        }
    }

    /// <summary>The stub metadata-document server: the registered URLs, 404 for anything else.</summary>
    private sealed class DocumentServer : HttpMessageHandler
    {
        private readonly Dictionary<string, string> documents = new(StringComparer.Ordinal);

        public DocumentServer Serve(string url, string json)
        {
            documents[url] = json;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(documents.TryGetValue(request.RequestUri!.ToString(), out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>
    /// A public MCP client: the browser (cookies, no redirects followed) for sign-in and consent, and the agent itself
    /// (no cookies, as a real agent calls the token endpoint) for code + PKCE (S256) with <c>resource</c>.
    /// </summary>
    private sealed class Agent(HttpClient browser, HttpClient client, string clientId)
    {
        private readonly string verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private string Challenge => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public async Task SignInAsync(string email, string password)
        {
            var page = await browser.GetStringAsync("/connect/login?returnUrl=%2F");
            var response = await browser.PostAsync("/connect/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = email, ["password"] = password, ["returnUrl"] = "/", ["__RequestVerificationToken"] = Antiforgery(page),
            }));
            response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the password sign-in succeeds");
        }

        public async Task<(string Html, string RequestId)> ConsentPageAsync()
        {
            var authorize = await browser.GetAsync("/connect/authorize?response_type=code"
                + $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(Redirect)}"
                + $"&scope={Uri.EscapeDataString("mcp:read mcp:write offline_access")}&resource={Uri.EscapeDataString(CanonicalUrl)}"
                + $"&code_challenge={Challenge}&code_challenge_method=S256&state=s1");
            authorize.StatusCode.Should().Be(HttpStatusCode.Redirect, await authorize.Content.ReadAsStringAsync());
            var location = authorize.Headers.Location!.OriginalString;
            location.Should().StartWith("/connect/consent?request_id=", "an agent sign-in always asks the user");
            return (await browser.GetStringAsync(location), HttpUtility.ParseQueryString(new Uri("https://x" + location).Query)["request_id"]!);
        }

        public async Task<string> CodeAsync(string html, string requestId)
        {
            var consent = await browser.PostAsync("/connect/consent", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["request_id"] = requestId, ["decision"] = "allow", ["types_mode"] = "all", ["permission"] = "read",
                ["__RequestVerificationToken"] = Antiforgery(html),
            }));
            consent.StatusCode.Should().Be(HttpStatusCode.Redirect, await consent.Content.ReadAsStringAsync());
            var query = HttpUtility.ParseQueryString(consent.Headers.Location!.Query);
            query["iss"].Should().Be(Issuer, "RFC 9207 iss on the response");
            return query["code"]!;
        }

        public async Task<JsonNode> RedeemAsync(string code)
        {
            var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = Redirect, ["client_id"] = clientId,
                ["code_verifier"] = verifier, ["resource"] = CanonicalUrl,
            }));
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, "the token endpoint answered {0}", body);
            return JsonNode.Parse(body)!;
        }

        private static string Antiforgery(string html)
        {
            var match = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
            match.Success.Should().BeTrue("the page carries an antiforgery token");
            return WebUtility.HtmlDecode(match.Groups[1].Value);
        }
    }

    /// <summary>JSON-RPC over the Streamable HTTP transport.</summary>
    private static class Mcp
    {
        public sealed record Reply(HttpStatusCode Status, string Text);

        public static async Task<Reply> CallAsync(HttpClient client, string tool, object arguments, string? token)
        {
            var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } });
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
            if (token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            // A JSON body, or the last data: event of an SSE body; the tool's text content.
            var json = text.TrimStart().StartsWith('{')
                ? text
                : text.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))?["data:".Length..];
            var content = json is null ? null : JsonNode.Parse(json)?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
            return new Reply(response.StatusCode, content ?? text);
        }
    }
}
