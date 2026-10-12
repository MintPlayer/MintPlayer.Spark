using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Mcp.Authentication;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MintPlayer.Spark.Tests.Mcp._Infrastructure;

/// <summary>
/// A Spark host with the MCP server, two stub credentials and the guarded fixtures:
/// <list type="bullet">
/// <item><c>X-Stub-Cookie: alice</c> — an AMBIENT Spark credential, standing in for the sign-in cookie. <c>/mcp</c> must ignore it.</item>
/// <item><c>Authorization: Bearer stub-alice</c> — the agent token, wired as a personal-access-token setup whose
/// principal carries only <c>sub</c> (as a resource server's does). <c>Bearer bad</c> is refused.</item>
/// </list>
/// <c>GuardedDoc</c> is exposed to agents (<c>Read</c>); <c>FilteredDoc</c> is not (<c>None</c>).
/// </summary>
internal static class McpTestHost
{
    public const string CookieScheme = "StubCookie";
    public const string CookieHeader = "X-Stub-Cookie";
    public const string BearerScheme = "StubBearer";
    public const string CanonicalUrl = "https://acme.example/mcp";

    public static readonly Guid GuardedDocTypeId = Guid.Parse("4c9d0000-0001-4000-8000-000000000001");
    public static readonly Guid FilteredDocTypeId = Guid.Parse("4c9d0000-0002-4000-8000-000000000002");
    public static readonly Guid AllDocsQueryId = Guid.Parse("4c9d0000-0003-4000-8000-000000000003");
    public static readonly Guid AllFilteredQueryId = Guid.Parse("4c9d0000-0004-4000-8000-000000000004");

    public static IEnumerable<EntityTypeFile> Models()
    {
        var guarded = GuardedDocModel.For(GuardedDocTypeId);
        guarded.PersistentObject.AgentPermissions = EAgentPermission.Read;
        guarded.Queries = [new SparkQuery { Id = AllDocsQueryId, Name = "AllDocs", Alias = "alldocs", Source = "Database.Docs", EntityType = "GuardedDoc" }];

        var filtered = FilteredDocModel.For(FilteredDocTypeId);
        filtered.Queries = [new SparkQuery { Id = AllFilteredQueryId, Name = "AllFiltered", Alias = "allfiltered", Source = "Database.FilteredDocs", EntityType = "FilteredDoc" }];

        return [guarded, filtered];
    }

    public static SparkEndpointFactory<GuardedContext> Create(
        Raven.Client.Documents.IDocumentStore store,
        SparkTestSecurity security,
        Action<SparkMcpOptions>? configure = null,
        Action<ISparkBuilder>? configureSpark = null)
        => Create<GuardedContext>(store, Models(), security, configure, configureSpark);

    /// <summary>The same host over other fixtures.</summary>
    public static SparkEndpointFactory<TContext> Create<TContext>(
        Raven.Client.Documents.IDocumentStore store,
        IEnumerable<EntityTypeFile> models,
        SparkTestSecurity security,
        Action<SparkMcpOptions>? configure = null,
        Action<ISparkBuilder>? configureSpark = null,
        Action<IServiceCollection>? configureServices = null)
        where TContext : SparkContext
        => new(
            store,
            models,
            configureServices: configureServices,
            configureSpark: spark =>
            {
                spark.AddCredentialScheme<AuthenticationSchemeOptions, StubCookieHandler>(CookieScheme, isAmbient: true);
                spark.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, StubBearerHandler>(BearerScheme, _ => { });
                spark.AddMcp(mcp =>
                {
                    mcp.ServerName = "Acme Test";
                    mcp.ToolPrefix = "acme";
                    mcp.CanonicalUrl = CanonicalUrl;
                    mcp.AgentPermissions = _ => EAgentPermission.Read | EAgentPermission.Write;
                    mcp.AddTools<AcmeTestTools>();
                    mcp.AuthenticationDetectors.Add(new McpAuthenticationDetector(
                        EAgentAuthentication.PersonalAccessToken,
                        "the test bearer scheme",
                        _ => new McpAuthenticationSetup(
                            EAgentAuthentication.PersonalAccessToken,
                            BearerScheme,
                            principal => new AgentInformation(EAgentKind.PersonalAccessToken, "stub-client", null, null, principal),
                            SubjectIsLocalUserId: true,
                            Challenge: null)));
                    configure?.Invoke(mcp);
                });
                configureSpark?.Invoke(spark);
            },
            security: security);

    /// <summary>The ambient stand-in for the sign-in cookie: <c>X-Stub-Cookie: alice</c>.</summary>
    public sealed class StubCookieHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(CookieHeader, out var user))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "users/cookie-" + user), new Claim(ClaimTypes.Name, user.ToString())], CookieScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), CookieScheme)));
        }
    }

    /// <summary>The agent token: <c>Bearer stub-{user}</c> authenticates with <c>sub = users/{user}</c> only.</summary>
    public sealed class StubBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());

            var token = header["Bearer ".Length..];
            if (!token.StartsWith("stub-", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.Fail("invalid token"));

            var identity = new ClaimsIdentity([new Claim("sub", "users/" + token["stub-".Length..])], BearerScheme, "sub", "role");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), BearerScheme)));
        }
    }
}

/// <summary>An application's own tools, written unprefixed: one read, one write.</summary>
[McpServerToolType]
public sealed class AcmeTestTools(ISparkCurrentUser currentUser, IHttpContextAccessor accessor)
{
    [McpServerTool(Name = "whoami", ReadOnly = true)]
    public CallToolResult WhoAmI()
        => SparkMcpResult.Text(
            $"authenticated={currentUser.IsAuthenticated};id={currentUser.Id};scheme={accessor.HttpContext?.Features.Get<ISparkAuthenticatedSchemeFeature>()?.Scheme.Name}");

    [McpServerTool(Name = "touch")]
    public string Touch() => "touched";
}

/// <summary>Raw JSON-RPC over the Streamable HTTP transport, so status codes and headers stay observable.</summary>
internal static class McpWire
{
    private static int nextId;

    public sealed record Reply(HttpStatusCode Status, JsonNode? Body, HttpResponseMessage Response)
    {
        public JsonNode? Result => Body?["result"];
        public string? ErrorMessage => Body?["error"]?["message"]?.GetValue<string>();
        public bool IsToolError => Result?["isError"]?.GetValue<bool>() == true;
        public string ToolText => Result?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
        public IReadOnlyList<string> ToolNames => Result?["tools"]?.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList() ?? [];
    }

    public static Task<Reply> ListToolsAsync(HttpClient client, Action<HttpRequestMessage>? configure = null)
        => SendAsync(client, "tools/list", new { }, configure);

    public static Task<Reply> CallAsync(HttpClient client, string tool, object arguments, Action<HttpRequestMessage>? configure = null)
        => SendAsync(client, "tools/call", new { name = tool, arguments }, configure);

    public static Task<Reply> InitializeAsync(HttpClient client, Action<HttpRequestMessage>? configure = null)
        => SendAsync(client, "initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "spark-tests", version = "1" },
        }, configure);

    public static async Task<Reply> SendAsync(HttpClient client, string method, object @params, Action<HttpRequestMessage>? configure = null)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = Interlocked.Increment(ref nextId), method, @params });
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        configure?.Invoke(request);

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new Reply(response.StatusCode, Parse(text), response);
    }

    public static Action<HttpRequestMessage> Bearer(string user) => r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "stub-" + user);
    public static Action<HttpRequestMessage> Cookie(string user) => r => r.Headers.Add(McpTestHost.CookieHeader, user);
    public static Action<HttpRequestMessage> Origin(string origin) => r => r.Headers.Add("Origin", origin);
    public static Action<HttpRequestMessage> Both(params Action<HttpRequestMessage>[] all) => r => { foreach (var a in all) a(r); };

    /// <summary>A JSON body, or the last <c>data:</c> event of an SSE body.</summary>
    private static JsonNode? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{'))
            return JsonNode.Parse(trimmed);

        var data = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("data:", StringComparison.Ordinal)).LastOrDefault();
        return data is null ? null : JsonNode.Parse(data["data:".Length..].Trim());
    }
}
