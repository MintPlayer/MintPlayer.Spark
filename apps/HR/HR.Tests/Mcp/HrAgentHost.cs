using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents;

namespace HR.Tests.Mcp;

/// <summary>
/// HR's real <c>Program</c> (its model, its actions, its <c>AddMcp</c> wiring with personal access tokens) for the MCP
/// tests: no background services, no Angular dev server, and a content root that is a copy of HR's own with one
/// change, the security file. It is HR's <c>security.json</c> plus <see cref="RestrictedGroup"/>, a group that may read
/// and edit people but neither read companies (so <c>Person.Company</c> is a hidden reference, MCP PRD D19) nor any
/// person's <c>Email</c> (an attribute-level denial): the two kinds of redaction HR's own groups never produce.
/// </summary>
internal sealed class HrAgentHost(IDocumentStore store) : WebApplicationFactory<HRContext>
{
    public const string Password = "Correct-horse-battery-1";
    public const string CanonicalUrl = "https://hr.example/mcp";

    /// <summary>HR's own groups (<c>App_Data/security.json</c>).</summary>
    public const string Managers = "HR managers";
    public const string Viewers = "Viewers";

    /// <summary>The test's group, added to a copy of HR's security file.</summary>
    public const string RestrictedGroup = "Parity restricted";
    private const string RestrictedGroupId = "d0000005-0000-0000-0000-000000000001";

    private readonly string contentRoot = CopyContentRoot();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: that would start the Angular dev server (UseAngularCliServer).
        builder.UseEnvironment("HRAgentTest");
        builder.UseContentRoot(contentRoot);

        var settings = new Dictionary<string, string?>
        {
            ["Spark:RavenDb:Urls:0"] = store.Urls[0],
            ["Spark:RavenDb:Database"] = store.Database,
            ["Spark:DataProtection:KeysPath"] = Path.Combine(contentRoot, "dataprotection"),
            ["Spark:HttpsRedirection"] = "false",
            ["Spark:Replication:ModuleUrl"] = "http://localhost",
            ["Spark:Replication:SparkModulesUrls:0"] = store.Urls[0],
            ["Spark:Replication:SparkModulesDatabase"] = store.Database + "-modules",
            // The web's limiter is per address and every TestServer request shares one; the agents' own quotas stay.
            ["Spark:RateLimiter:PermitLimit"] = "100000",
            ["Spark:Mcp:CanonicalUrl"] = CanonicalUrl,
        };
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name != "GenericWebHostService").ToList())
                services.Remove(descriptor);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(contentRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>A user with a password, in <paramref name="group"/> (a role) unless null: a plain signed-in user.</summary>
    public async Task<string> CreateUserAsync(string email, string? group)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<SparkUser>>();
        var user = new SparkUser { UserName = email[..email.IndexOf('@')], Email = email, EmailConfirmed = true };
        var created = await users.CreateAsync(user, Password);
        created.Succeeded.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));
        if (group is not null)
            (await users.AddToRoleAsync(user, group)).Succeeded.Should().BeTrue();
        return user.Id!;
    }

    /// <summary>The user's browser, signed in with the cookie the web's own sign-in issues.</summary>
    public async Task<HrBrowser> SignInAsync(string email)
    {
        var browser = new HrBrowser(CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false }));
        using (await browser.SendAsync(HttpMethod.Get, "/spark")) { }
        using (var login = await browser.SendAsync(HttpMethod.Post, "/spark/auth/login?useCookies=true", new { email, password = Password }))
            login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using (await browser.SendAsync(HttpMethod.Get, "/spark")) { } // mints the XSRF token for the signed-in identity
        return browser;
    }

    /// <summary>A copy of HR's content root (settings and <c>App_Data</c>) whose security file has <see cref="RestrictedGroup"/>.</summary>
    private static string CopyContentRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nx.json")))
            dir = dir.Parent;
        var app = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("No nx.json above the test output."), "apps", "HR", "HR");

        var root = Path.Combine(Path.GetTempPath(), $"hr-agent-tests-{Environment.ProcessId}-{Guid.NewGuid():N}");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(app, "App_Data"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(app, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        File.Copy(Path.Combine(app, "appsettings.json"), Path.Combine(root, "appsettings.json"));

        var securityPath = Path.Combine(root, "App_Data", "security.json");
        var security = JsonNode.Parse(File.ReadAllText(securityPath))!.AsObject();
        security["groups"]!.AsObject()[RestrictedGroupId] = RestrictedGroup;
        var rights = security["rights"]!.AsArray();
        rights.Add(Right("d0000005-0000-0000-0000-000000000011", "QueryReadEdit/Person", denied: false));
        rights.Add(Right("d0000005-0000-0000-0000-000000000012", "QueryRead/CarreerJob", denied: false));
        // A denial beats the signed-in group's QueryRead/Company (guide-authorization.md, Precedence).
        rights.Add(Right("d0000005-0000-0000-0000-000000000013", "QueryRead/Company", denied: true));
        rights.Add(Right("d0000005-0000-0000-0000-000000000014", "QueryRead/Person/Email", denied: true));
        File.WriteAllText(securityPath, security.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return root;

        static JsonObject Right(string key, string resource, bool denied)
            => new() { ["key"] = key, ["resource"] = resource, ["groupId"] = RestrictedGroupId, ["isDenied"] = denied };
    }
}

/// <summary>
/// A browser: the sign-in cookie and the XSRF token threaded through <see cref="HttpClient"/> by hand, for the web
/// endpoints and the account page's endpoints (antiforgery on every cookie-authenticated POST).
/// </summary>
internal sealed class HrBrowser(HttpClient client)
{
    private readonly Dictionary<string, string> cookies = new(StringComparer.Ordinal);

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

    /// <summary>The answer's status and its body as JSON (null when it has none).</summary>
    public async Task<(HttpStatusCode Status, JsonNode? Body)> JsonAsync(HttpMethod method, string url, object? body = null)
    {
        using var response = await SendAsync(method, url, body);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>A personal access token for everything HR exposes, with changes (Connected applications).</summary>
    public async Task<string> CreateTokenAsync(string permissions = "Read, Write")
    {
        var (status, body) = await JsonAsync(HttpMethod.Post, "/spark/auth/manage/agents/tokens", new { name = "parity", permissions });
        status.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
        return body!["token"]!.GetValue<string>();
    }
}

/// <summary>JSON-RPC over the Streamable HTTP transport, as an agent sends it.</summary>
internal static class HrMcp
{
    public sealed record Reply(HttpStatusCode Status, bool IsToolError, string Text, string? Error);

    public static async Task<Reply> CallAsync(HttpClient client, string tool, object arguments, string? token)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } });
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        // A JSON body, or the last data: event of an SSE body.
        var json = text.TrimStart().StartsWith('{')
            ? text
            : text.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal))?["data:".Length..];
        var node = json is null ? null : JsonNode.Parse(json);
        var result = node?["result"];
        return new Reply(
            response.StatusCode,
            result?["isError"]?.GetValue<bool>() == true,
            result?["content"]?[0]?["text"]?.GetValue<string>() ?? text,
            node?["error"]?["message"]?.GetValue<string>());
    }

    /// <summary>The JSON a tool fenced as data (MCP PRD D10).</summary>
    public static JsonNode Unfence(string text)
    {
        var start = text.IndexOf("<data-", StringComparison.Ordinal) + 1;
        start.Should().BeGreaterThan(0, "the answer carries fenced data: " + text);
        var tag = text[start..text.IndexOf('>', start)];
        var open = text.LastIndexOf($"<{tag}>", StringComparison.Ordinal) + tag.Length + 2;
        var close = text.LastIndexOf($"</{tag}>", StringComparison.Ordinal);
        return JsonNode.Parse(text[open..close])!;
    }
}
