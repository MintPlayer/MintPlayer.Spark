using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeCoverage.ApiTokens;
using CodeCoverage.Services;
using CodeCoverage.Tests._Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Mcp._Infrastructure;

/// <summary>
/// The MCP tests' own collection: one host, booted only when these tests run.
/// </summary>
/// <remarks>
/// Non-parallel like <see cref="CoverageWebHostCollection"/>, so the two collections run one after the other and never
/// start two composition roots at once (Spark's registry is process-wide). A separate collection, rather than a third
/// fixture on that one, because every collection fixture boots whenever any class of its collection runs: the MCP host
/// would otherwise cost every HTTP test run a boot.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CoverageMcpHostCollection : ICollectionFixture<CoverageMcpHostFixture>
{
    public const string Name = "Coverage MCP host";
}

/// <summary>
/// The real application (<c>Program</c>: Spark, the identity provider with <c>ForMcp()</c>, the MCP server) with GitHub
/// replaced <b>only where it would leave the process</b>: the user-token store and GitHub's HTTP API. Everything above
/// (<c>ForgeIntegrationResolver</c>, <c>GitHubAccessService</c>, the resolver, the scope) is the app's own registration,
/// so a request takes the production path, as <see cref="StubbedGitHubWebHostFixture"/> does for the installation side.
/// </summary>
/// <remarks>
/// Also trusts a test key for the <c>GitHubOidc</c> scheme, so a workflow token the app itself accepts can be shown to be
/// refused on <c>/mcp</c> (D23). The MCP call limits are off: these tests make many anonymous calls from one address.
/// </remarks>
public sealed class CoverageMcpHostFixture : CoverageRavenTest, IAsyncLifetime
{
    public IDocumentStore Store { get; private set; } = null!;
    public CoverageWebAppFactory Factory { get; private set; } = null!;

    /// <summary>The key a test GitHub Actions token is signed with; the host's <c>GitHubOidc</c> scheme trusts it.</summary>
    public RsaSecurityKey WorkflowKey { get; } = new(RSA.Create(2048)) { KeyId = "test-workflow-key" };

    /// <summary>The GitHub API as these tests script it, shared by every handler instance the factory creates.</summary>
    internal StubGitHubApi.Script GitHubApi { get; } = new();

    /// <summary>The seeded users' personal access tokens and acme's upload token.</summary>
    internal CoverageMcpSeed.Credentials Credentials { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Store = GetDocumentStore();
        Factory = new CoverageWebAppFactory(Store, services =>
            {
                services.RemoveAll<IGitHubUserTokenService>();
                services.AddSingleton<IGitHubUserTokenService>(new StubGitHubUserTokens());

                // GitHub's REST API (the user's owners, comparisons): a new handler per pipeline, over one script, because
                // the factory disposes the handlers it rotates.
                services.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName)
                    .ConfigurePrimaryHttpMessageHandler(() => new StubGitHubApi(GitHubApi));

                services.PostConfigure<JwtBearerOptions>(GitHubOidc.SchemeName, options =>
                {
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration { Issuer = GitHubOidc.Issuer, SigningKeys = { WorkflowKey } });
                    options.TokenValidationParameters.IssuerSigningKey = WorkflowKey;
                });
            },
            settings: new Dictionary<string, string?>
            {
                ["Spark:Mcp:Quota:AnonymousPerMinute"] = "0",
                ["Spark:Mcp:Quota:SignedInPerMinute"] = "0",
            });

        // Boot here, so a startup failure is attributed to the fixture (see CoverageWebHostFixture).
        _ = Factory.CreateClient();
        Credentials = CoverageMcpSeed.SeedAsync(Store, GitHubApi).GetAwaiter().GetResult();
        WaitForIndexing(Store);
        return Task.CompletedTask;
    }

    /// <summary>The same three independent, non-throwing teardown steps as <see cref="CoverageWebHostFixture"/>.</summary>
    public async Task DisposeAsync()
    {
        try { await Factory.DisposeAsync(); }
        catch (Exception ex) { Console.WriteLine($"[CoverageMcpHostFixture] host disposal faulted: {ex}"); }

        try { Store?.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[CoverageMcpHostFixture] store disposal faulted: {ex}"); }

        try { base.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[CoverageMcpHostFixture] RavenDB test-driver disposal faulted: {ex}"); }
    }

    /// <summary>A GitHub Actions workflow token for <c>acme/open</c>, signed with <see cref="WorkflowKey"/>.</summary>
    public string WorkflowToken()
    {
        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = GitHubOidc.Issuer,
            Audience = "http://localhost",
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "repo:acme/open:ref:refs/heads/main",
                ["repository"] = "acme/open",
                ["repository_visibility"] = "public",
            },
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(WorkflowKey, SecurityAlgorithms.RsaSha256),
        });
    }
}

/// <summary>A GitHub user token per stored user, without a refresh: <c>ghu_test_{user id}</c>.</summary>
internal sealed class StubGitHubUserTokens : IGitHubUserTokenService
{
    public Task<GitHubUserToken> GetAccessTokenAsync(SparkUser user, bool forceRefresh = false, CancellationToken cancellationToken = default)
        => Task.FromResult(new GitHubUserToken(StubGitHubApi.TokenFor(user.Id!), GitHubTokenState.Ok));
}

/// <summary>
/// GitHub's REST API for the requests these tests make: <c>GET /user</c> and <c>GET /user/installations</c> answer per
/// user token; <c>GET /repos/{owner}/{name}/compare/{base}...{head}</c> answers what a test scripted. Anything else is a
/// 503, which every GitHub client in the app treats as "GitHub unavailable".
/// </summary>
internal sealed class StubGitHubApi(StubGitHubApi.Script script) : HttpMessageHandler
{
    public sealed class Script
    {
        /// <summary>Per user token: the account GET /user returns and the organisations the user's installations reach.</summary>
        public Dictionary<string, (long Id, string Login, (long Id, string Login)[] Organisations)> Users { get; } = new(StringComparer.Ordinal);

        /// <summary>Per <c>{owner}/{name}/compare/{base}...{head}</c> path: GitHub's comparison JSON.</summary>
        public Dictionary<string, string> Comparisons { get; } = new(StringComparer.Ordinal);
    }

    public static string TokenFor(string userId) => "ghu_test_" + userId.Replace('/', '_');

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var token = request.Headers.Authorization?.Parameter;

        if (path is "/user" or "/user/installations")
        {
            if (token is null || !script.Users.TryGetValue(token, out var user))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

            var body = path == "/user"
                ? new JsonObject { ["id"] = user.Id, ["login"] = user.Login, ["type"] = "User" }
                : new JsonObject
                {
                    ["total_count"] = user.Organisations.Length,
                    ["installations"] = new JsonArray([.. user.Organisations.Select((o, i) => (JsonNode)new JsonObject
                    {
                        ["id"] = 50_000 + o.Id,
                        ["target_type"] = "Organization",
                        ["suspended_at"] = null,
                        ["account"] = new JsonObject { ["login"] = o.Login, ["id"] = o.Id, ["type"] = "Organization" },
                    })]),
                };
            return Task.FromResult(Json(body.ToJsonString()));
        }

        const string repos = "/repos/";
        if (path.StartsWith(repos, StringComparison.Ordinal)
            && script.Comparisons.TryGetValue(Uri.UnescapeDataString(path[repos.Length..]), out var comparison))
        {
            return Task.FromResult(Json(comparison));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>Raw JSON-RPC over the Streamable HTTP transport, so status codes and headers stay observable.</summary>
internal static class McpWire
{
    private static int nextId;

    public sealed record Reply(HttpStatusCode Status, string Text, JsonNode? Body, HttpResponseMessage Response)
    {
        public JsonNode? Result => Body?["result"];
        public bool IsToolError => Result?["isError"]?.GetValue<bool>() == true;
        public string ToolText => Result?["content"]?[0]?["text"]?.GetValue<string>() ?? string.Empty;
        public string? ErrorMessage => Body?["error"]?["message"]?.GetValue<string>();

        /// <summary>The fenced JSON of a <c>SparkMcpResult.Json</c> answer.</summary>
        public JsonNode Data
        {
            get
            {
                var text = ToolText;
                var first = text.IndexOf("<data-", StringComparison.Ordinal);
                first.Should().BeGreaterThan(-1, $"a data answer is fenced: {text}");
                var tag = text[(first + 1)..text.IndexOf('>', first)];

                // The last pair: the note in front of the data names the tag too.
                var start = text.LastIndexOf($"<{tag}>", StringComparison.Ordinal) + tag.Length + 2;
                var end = text.LastIndexOf($"</{tag}>", StringComparison.Ordinal);
                return JsonNode.Parse(text[start..end])!;
            }
        }
    }

    public static Task<Reply> CallAsync(HttpClient client, string tool, object arguments, Action<HttpRequestMessage>? configure = null)
        => SendAsync(client, "tools/call", new { name = tool, arguments }, configure);

    public static async Task<Reply> SendAsync(HttpClient client, string method, object @params, Action<HttpRequestMessage>? configure = null, int? id = null)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id ?? Interlocked.Increment(ref nextId), method, @params });
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        configure?.Invoke(request);

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new Reply(response.StatusCode, text, Parse(text), response);
    }

    public static Action<HttpRequestMessage> Bearer(string token) => r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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

/// <summary>A personal access token minted the way the Authorization package does (D11), straight into the store.</summary>
internal static class TestPersonalAccessTokens
{
    public static string Create(string userId, out string tokenId)
    {
        tokenId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return $"spat_{WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(userId))}.{tokenId}.{secret}";
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
