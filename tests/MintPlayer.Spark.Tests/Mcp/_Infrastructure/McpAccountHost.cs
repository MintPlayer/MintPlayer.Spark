using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Mcp._Infrastructure;

/// <summary>
/// A Spark host with real Identity (the Authorization package, so setup 1 is wired for real) and the MCP server.
/// <c>GuardedDoc</c> and <c>FilteredDoc</c> are both exposed to agents (<c>Read</c>); only the group
/// <see cref="Developers"/> may query and read them, so a caller's groups decide, and they reach an agent token
/// only through the store-backed provider (PRD §4.6).
/// </summary>
internal static class McpAccountHost
{
    public const string Developers = "Developers";
    public const string Password = "Correct-horse-1";
    private static readonly Guid DevelopersGroupId = Guid.Parse("4c9d0000-00d0-4000-8000-0000000000d0");

    public static IEnumerable<EntityTypeFile> Models()
    {
        var models = McpTestHost.Models().ToList();
        foreach (var model in models)
            model.PersistentObject.AgentPermissions = EAgentPermission.Read | EAgentPermission.Write;
        return models;
    }

    public static SparkEndpointFactory<GuardedContext> Create(IDocumentStore store, Action<SparkMcpOptions>? configure = null)
        => new(
            store,
            Models(),
            configureSpark: spark =>
            {
                spark.AddAuthentication<SparkUser>(auth =>
                {
                    auth.LocalCredentials = SparkLocalCredentials.Full;
                    auth.AllowUnconfirmedRegistration = true;
                });
                spark.AddMcp(mcp =>
                {
                    mcp.ServerName = "Acme Test";
                    mcp.ToolPrefix = "acme";
                    mcp.CanonicalUrl = McpTestHost.CanonicalUrl;
                    mcp.AgentPermissions = _ => EAgentPermission.Read | EAgentPermission.Write;
                    mcp.AddTools<AcmeTestTools>();
                    configure?.Invoke(mcp);
                });
            },
            security: SparkTestSecurity.FromJson(Security()));

    private static string Security() => JsonSerializer.Serialize(new
    {
        wellKnown = new Dictionary<string, string>
        {
            [SparkWellKnownGroups.Anonymous] = SparkTestSecurity.AnonymousGroupId.ToString(),
            [SparkWellKnownGroups.Authenticated] = SparkTestSecurity.AuthenticatedGroupId.ToString(),
        },
        groups = new Dictionary<string, string>
        {
            [SparkTestSecurity.AnonymousGroupId.ToString()] = "Anonymous visitors",
            [SparkTestSecurity.AuthenticatedGroupId.ToString()] = "Signed-in users",
            [DevelopersGroupId.ToString()] = Developers,
        },
        rights = new[]
        {
            new { key = "dev-guarded", resource = "QueryRead/GuardedDoc", groupId = DevelopersGroupId.ToString() },
            new { key = "dev-filtered", resource = "QueryRead/FilteredDoc", groupId = DevelopersGroupId.ToString() },
        },
        libraries = SparkTestSecurity.LibraryRightsOff(),
    });

    /// <summary>A user with a password, optionally in <see cref="Developers"/>. Names must be unique per database.</summary>
    public static async Task<SparkUser> CreateUserAsync(SparkEndpointFactory<GuardedContext> factory, string userName, bool developer = true)
    {
        using var scope = factory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>();
        var user = new SparkUser { UserName = userName, Email = userName + "@test.local", EmailConfirmed = true };
        var created = await users.CreateAsync(user, Password);
        created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));
        if (developer)
            (await users.AddToRoleAsync(user, Developers)).Succeeded.Should().BeTrue();
        return user;
    }

    /// <summary>Runs <paramref name="action"/> on the user, through <see cref="UserManager{TUser}"/> as the application would.</summary>
    public static async Task ChangeUserAsync(SparkEndpointFactory<GuardedContext> factory, string userId, Func<UserManager<SparkUser>, SparkUser, Task<IdentityResult>> action)
    {
        using var scope = factory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>();
        var user = await users.FindByIdAsync(userId);
        (await action(users, user!)).Succeeded.Should().BeTrue();
    }
}

/// <summary>
/// A browser: the sign-in cookie and the XSRF token, threaded through <see cref="HttpClient"/> (TestServer's client
/// keeps no cookies), for the account endpoints, which demand antiforgery like every cookie-authenticated POST.
/// </summary>
internal sealed class McpBrowser(HttpClient client)
{
    private readonly Dictionary<string, string> cookies = new(StringComparer.Ordinal);

    public HttpClient Client => client;

    public static async Task<McpBrowser> SignInAsync(HttpClient client, string userName)
    {
        var browser = new McpBrowser(client);
        await browser.SendAsync(HttpMethod.Get, "/spark");
        using var login = await browser.SendAsync(HttpMethod.Post, "/spark/auth/login?useCookies=true",
            new { email = userName + "@test.local", password = McpAccountHost.Password });
        login.EnsureSuccessStatusCode();
        await browser.SendAsync(HttpMethod.Get, "/spark"); // mint the XSRF token for the signed-in identity
        return browser;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (cookies.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}")));
        if (cookies.TryGetValue("XSRF-TOKEN", out var xsrf))
            request.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(xsrf));
        if (body is not null)
            request.Content = JsonContent.Create(body);

        var response = await client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var pair in setCookies.Select(c => c.Split(';', 2)[0]))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0)
                    cookies[pair[..eq]] = pair[(eq + 1)..];
            }
        }
        return response;
    }

    public async Task<JsonObject> AgentsAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "/spark/auth/manage/agents");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    /// <summary>Creates a personal access token; answers the raw response.</summary>
    public Task<HttpResponseMessage> PostTokenAsync(object request)
        => SendAsync(HttpMethod.Post, "/spark/auth/manage/agents/tokens", request);

    /// <summary>Creates a personal access token and answers it and its grant key.</summary>
    public async Task<(string Token, string Key)> CreateTokenAsync(string name, string permissions = "Read", string[]? types = null)
    {
        using var response = await PostTokenAsync(new
        {
            name,
            permissions,
            types = types?.Select(t => new { name = t, permissions = "Read" }).ToArray(),
        });
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(text);
        var body = JsonNode.Parse(text)!;
        return (body["token"]!.GetValue<string>(), body["key"]!.GetValue<string>());
    }

    public static Action<HttpRequestMessage> Token(string token) => r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
}
