using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Mcp;

/// <summary>
/// The two ways a user connects an AI agent to a Spark application, end to end against Fleet's real process (MCP PRD
/// D4, D5, D11, D14, D17; plan M7): an agent that finds the authorization server from the 401, registers itself and signs
/// the user in through OAuth in a real browser; and a personal access token made, used and revoked on the Connected
/// applications page. The agent is scripted HTTP, as Claude Code, Codex and Gemini CLI speak it; the pages are driven by
/// Playwright.
/// </summary>
[Collection(FleetMcpE2ECollection.Name)]
public sealed class McpAgentJourneyTests(FleetMcpE2EFixture fixture) : IAsyncLifetime
{
    private readonly List<IBrowserContext> contexts = [];

    private FleetTestHost Host => fixture.Host;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var context in contexts)
            await context.CloseAsync();
    }

    [Fact]
    public async Task An_agent_signs_the_user_in_through_OAuth_in_the_browser_and_calls_tools_as_that_user()
    {
        var plate = await SeedCarAsync("OA");
        using var agent = AgentClient();

        // 1. The first protected call carries no token: an HTTP 401 whose challenge names the protected-resource metadata.
        var refused = await McpAsync(agent, "tools/call", new { name = "fleet_query", arguments = new { query = "cars" } }, token: null);
        refused.Status.Should().Be(HttpStatusCode.Unauthorized, refused.Body);
        var metadataUrl = Regex.Match(refused.Challenge, "resource_metadata=\"([^\"]+)\"").Groups[1].Value;
        metadataUrl.Should().Be(Host.FleetUrl + "/.well-known/oauth-protected-resource/mcp", "the RFC 9728 challenge: " + refused.Challenge);

        // 2. RFC 9728, then RFC 8414: where to sign in, and how.
        var resource = (await agent.GetFromJsonAsync<JsonObject>(metadataUrl))!;
        var canonical = resource["resource"]!.GetValue<string>();
        canonical.Should().Be(Host.FleetUrl + "/mcp");
        var issuer = resource["authorization_servers"]!.AsArray().Single()!.GetValue<string>();
        issuer.Should().Be(Host.FleetUrl, "Fleet is its agents' authorization server");
        var server = (await agent.GetFromJsonAsync<JsonObject>(issuer + "/.well-known/oauth-authorization-server"))!;
        server["code_challenge_methods_supported"]!.AsArray().Select(m => m!.GetValue<string>()).Should().Contain("S256");

        // 3. Anonymous registration with a loopback redirect, as Gemini CLI does (and Claude Code without CIMD): the agent
        //    listens on a port of its own, and the code arrives there.
        await using var loopback = new LoopbackRedirect();
        var redirect = loopback.RedirectUri;
        using var registration = await agent.PostAsJsonAsync(server["registration_endpoint"]!.GetValue<string>(), new
        {
            client_name = "E2E agent",
            redirect_uris = new[] { redirect },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        });
        registration.StatusCode.Should().Be(HttpStatusCode.Created, await registration.Content.ReadAsStringAsync());
        var clientId = (await registration.Content.ReadFromJsonAsync<JsonObject>())!["client_id"]!.GetValue<string>();

        // 4. The browser: Fleet's sign-in page, then the consent page; the code arrives at the agent's loopback redirect.
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var authorize = server["authorization_endpoint"]!.GetValue<string>() + "?" + string.Join('&', new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["scope"] = "mcp:read offline_access",
            ["resource"] = canonical,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        var page = await NewPageAsync();
        await page.GotoAsync(authorize);
        await page.Locator("#identifier").FillAsync(Host.AdminEmailAddress);
        await page.Locator("#password").FillAsync(Host.AdminPass);
        await page.Locator("#password").PressAsync("Enter"); // the form posts its antiforgery token with it
        var allow = page.Locator("button[name=decision][value=allow]");
        await allow.WaitForAsync();
        (await page.ContentAsync()).Should().Contain("data-agent-trust=\"unverified\"", "a dynamically registered agent is labelled unverified (D5)");
        await allow.ClickAsync();
        var callback = await loopback.CallbackAsync.WaitAsync(TimeSpan.FromSeconds(30));
        await page.WaitForURLAsync(url => url.StartsWith(redirect, StringComparison.Ordinal));

        var answer = HttpUtility.ParseQueryString(new Uri(callback).Query);
        answer["state"].Should().Be(state);
        answer["iss"].Should().Be(issuer, "RFC 9207");

        // 5. Code + PKCE, for this resource only.
        using var tokenResponse = await agent.PostAsync(server["token_endpoint"]!.GetValue<string>(), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = answer["code"]!,
            ["redirect_uri"] = redirect,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
            ["resource"] = canonical,
        }));
        var tokens = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync())!;
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK, tokens.ToJsonString());
        var access = tokens["access_token"]!.GetValue<string>();
        tokens["refresh_token"].Should().NotBeNull("agents get refresh tokens");

        // 6. tools/list and one tools/call, as the user who signed in.
        var tools = await McpAsync(agent, "tools/list", new { }, access);
        tools.Status.Should().Be(HttpStatusCode.OK, tools.Body);
        var names = tools.Json!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        names.Should().Contain("fleet_query").And.Contain("fleet_discover");
        names.Should().NotContain("fleet_propose_changes", "Fleet lets agents read only");

        var cars = await McpAsync(agent, "tools/call", new { name = "fleet_query", arguments = new { query = "cars", search = plate } }, access);
        cars.Status.Should().Be(HttpStatusCode.OK, cars.Body);
        cars.ToolText.Should().Contain(plate, "the agent reads the cars its user may read");
    }

    [Fact]
    public async Task A_personal_access_token_made_on_the_connected_applications_page_works_until_it_is_revoked_there()
    {
        var plate = await SeedCarAsync("PT");
        var page = await NewPageAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync(); // "revoke this token?"
        await BrowserSignIn.SignInAsync(page, Host.FleetUrl, Host.AdminEmailAddress, Host.AdminPass);

        await page.GotoAsync("/account/agents");
        await page.Locator(".spark-agent-token-new").ClickAsync();
        await page.Locator("#sparkAgentTokenName").FillAsync("E2E token");
        await page.Locator(".spark-agent-token-create").ClickAsync();
        var token = (await page.Locator(".spark-agent-token").TextContentAsync())?.Trim();
        token.Should().StartWith("spat_", "the page shows the new token once");

        using var agent = AgentClient();
        var before = await McpAsync(agent, "tools/call", new { name = "fleet_query", arguments = new { query = "cars", search = plate } }, token);
        before.Status.Should().Be(HttpStatusCode.OK, before.Body);
        before.ToolText.Should().Contain(plate);

        var grant = page.Locator(".spark-agent-grant", new() { HasText = "E2E token" });
        await grant.Locator(".spark-agent-revoke").ClickAsync();
        await grant.WaitForAsync(new() { State = WaitForSelectorState.Detached });

        var after = await McpAsync(agent, "tools/call", new { name = "fleet_query", arguments = new { query = "cars", search = plate } }, token);
        after.Status.Should().Be(HttpStatusCode.Unauthorized, "the token was revoked on the page: " + after.Body);
    }

    private async Task<IPage> NewPageAsync()
    {
        var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true, BaseURL = Host.FleetUrl });
        contexts.Add(context);
        return await context.NewPageAsync();
    }

    /// <summary>A car the seeded administrator creates, so the administrator's agent has one to find.</summary>
    private async Task<string> SeedCarAsync(string prefix)
    {
        var plate = CarFixture.RandomLicensePlate(prefix);
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(Host);
        await client.CreatePersistentObjectAsync(CarFixture.New(plate));
        await Host.WaitForIndexingAsync();
        return plate;
    }

    /// <summary>The agent's HTTP: no cookies, no redirects followed, the host's self-signed certificate accepted.</summary>
    private HttpClient AgentClient()
        => new(new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        }) { BaseAddress = new Uri(Host.FleetUrl) };

    private sealed record McpReply(HttpStatusCode Status, string Challenge, string Body, JsonNode? Json)
    {
        public string ToolText => Json?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? Body;
    }

    /// <summary>One JSON-RPC request over the Streamable HTTP transport (stateless: no initialize needed).</summary>
    private static async Task<McpReply> McpAsync(HttpClient client, string method, object @params, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var json = body.TrimStart().StartsWith('{')
            ? body
            : body.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))?["data:".Length..];
        return new McpReply(response.StatusCode, response.Headers.WwwAuthenticate.ToString(), body, json is null ? null : JsonNode.Parse(json));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The agent's loopback redirect (RFC 8252 §7.3), as a native agent runs one: a listener on 127.0.0.1 and a port of
    /// its own that answers the browser's request for the redirect URI and hands the test the URL it was called with. Each
    /// connection is served on its own, because a browser may open one it never sends a request on.
    /// </summary>
    private sealed class LoopbackRedirect : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<string> callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource stop = new();
        private readonly Task accepting;
        private readonly int port;

        public LoopbackRedirect()
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            accepting = AcceptAsync();
        }

        public string RedirectUri => $"http://127.0.0.1:{port}/callback";

        /// <summary>The full URL the redirect was called with: code, state and iss in its query.</summary>
        public Task<string> CallbackAsync => callback.Task;

        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var connection = await listener.AcceptTcpClientAsync(stop.Token);
                    _ = ServeAsync(connection);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
            {
                // Stopped.
            }
        }

        private async Task ServeAsync(System.Net.Sockets.TcpClient connection)
        {
            using var _ = connection;
            try
            {
                var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(stop.Token) ?? string.Empty;
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token)))
                {
                    // The headers: nothing in them matters here.
                }
                var target = requestLine.Split(' ') is [_, var path, ..] ? path : "/";
                var found = target.StartsWith("/callback", StringComparison.Ordinal);
                var body = found ? "Signed in. You can close this window." : "Not found.";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), stop.Token);
                if (found)
                    callback.TrySetResult($"http://127.0.0.1:{port}{target}");
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // A connection the browser dropped, or the test ended.
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try { await accepting; } catch (OperationCanceledException) { }
            stop.Dispose();
        }
    }
}
