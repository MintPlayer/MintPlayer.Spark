using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace SeoTester.Tests._Infrastructure;

/// <summary>
/// Boots SeoTester's real <c>Program</c> in-process against the embedded RavenDB: the app's own App_Data (model,
/// security.json, model hashes) as content root, placeholder credentials for the four providers, the pickup folder
/// for mail, and a principal taken from test headers (<see cref="SeoPrincipalStartupFilter"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Not Development</b>: Program calls <c>UseAngularCliServer</c> there, which spawns <c>npm start</c>. Not
/// Production either: that environment's settings file points the mailed links at the real host.</item>
/// <item><b>One host per test collection</b> (<see cref="SeoTesterHostCollection"/>): Spark's registries are
/// process-wide, so two hosts must never boot at once.</item>
/// <item>Settings go through <c>UseSetting</c> (host configuration), because Program reads
/// <c>builder.Configuration</c> while it registers services (<c>AddExternalProviders</c>).</item>
/// </list>
/// </remarks>
public sealed class SeoTesterWebAppFactory(IDocumentStore store, IReadOnlyDictionary<string, string?>? settings = null, FakeDns? dns = null, FakeWeb? web = null, FakeTls? tls = null) : WebApplicationFactory<Program>
{
    public const string EnvironmentName = "IntegrationTest";

    /// <summary>The four providers of PRD §4.1, with obviously fake ids: no test reaches a provider.</summary>
    public static readonly string[] ProviderSections = ["MicrosoftAccount", "GitHub", "Twitter", "Google"];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.UseContentRoot(SeoTesterPaths.AppDirectory);

        builder.UseSetting("Spark:RavenDb:Urls:0", store.Urls[0]);
        builder.UseSetting("Spark:RavenDb:Database", store.Database);
        builder.UseSetting("Spark:DataProtection:Storage", "RavenDb");
        builder.UseSetting("Spark:HttpsRedirection", "false");
        builder.UseSetting("Spark:Auth:PublicBaseUrl", "http://localhost");
        builder.UseSetting("Spark:Mail:PickupFolder", Path.Combine(Path.GetTempPath(), $"seotester-tests-mail-{Environment.ProcessId}"));
        foreach (var provider in ProviderSections)
        {
            builder.UseSetting($"Spark:Auth:Providers:{provider}:ClientId", $"integration-test-{provider.ToLowerInvariant()}");
            builder.UseSetting($"Spark:Auth:Providers:{provider}:ClientSecret", "integration-test-secret");
        }
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            builder.UseSetting(key, value);

        // The pipeline's redeliveries and delayed batches are woken by the sweeper: every second, not every 30.
        builder.UseSetting("Spark:Messaging:FallbackPollInterval", "00:00:01");

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, SeoPrincipalStartupFilter>();
            // Nothing a test runs leaves the machine: DNS, the web (verification, the crawler, the registries) and TLS
            // answer from tables. FakeWeb replaces only the primary handlers, so the SSRF-safe handlers still run.
            services.AddSingleton<SeoTester.Dns.ISeoDns>(dns ?? new FakeDns());
            var fakeWeb = web ?? new FakeWeb();
            foreach (var client in new[] { SeoTester.Verification.SiteVerification.HttpClientName, SeoTester.Crawler.SeoHttp.Crawler, SeoTester.Crawler.SeoHttp.Lookups, SeoTester.Crawler.SeoHttp.Browser, SeoTester.Crawler.SeoHttp.PageSpeed })
                services.AddHttpClient(client).ConfigurePrimaryHttpMessageHandler(fakeWeb.CreateHandler);
            services.AddSingleton<SeoTester.Crawler.ITlsProber>(tls ?? new FakeTls());
        });
    }
}

/// <summary>Where the app lives, found by walking up to the repository root (<c>nx.json</c>).</summary>
internal static class SeoTesterPaths
{
    public static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nx.json")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException($"No repository root (nx.json) above {AppContext.BaseDirectory}.");
        }
    }

    public static string AppDirectory
    {
        get
        {
            var app = Path.Combine(RepositoryRoot, "apps", "SeoTester", "SeoTester");
            if (!File.Exists(Path.Combine(app, "App_Data", "security.json")))
                throw new InvalidOperationException($"'{app}' has no App_Data/security.json; Spark reads its model and security from the content root.");
            return app;
        }
    }
}

/// <summary>
/// A signed-in principal from <c>X-Seo-User</c>, with ASP.NET Identity role claims from <c>X-Seo-Roles</c>: the
/// shape a cookie sign-in gives a <c>SparkUser</c> whose <c>Roles</c> hold them, which is how a group such as
/// <c>Administrators</c> is granted (Spark reads groups from role claims).
/// </summary>
internal sealed class SeoPrincipalStartupFilter : IStartupFilter
{
    public const string UserHeader = "X-Seo-User";
    public const string RolesHeader = "X-Seo-Roles";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(UserHeader, out var user) && user.ToString() is { Length: > 0 } id)
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id), new(ClaimTypes.Name, id) };
                foreach (var role in context.Request.Headers[RolesHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    claims.Add(new Claim(ClaimTypes.Role, role));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "SeoTest"));
            }
            await nextMiddleware();
        });
        next(app);
    };
}

/// <summary>Requests as a given user, with Spark's antiforgery pair minted for that identity.</summary>
internal static class SeoRequests
{
    public static Task<(System.Net.HttpStatusCode Status, JsonElement Body, string Raw)> PostAsync(
        HttpClient client, string url, object payload, string? user, params string[] roles)
        => PostAsync(client, url, payload, user, language: null, roles);

    /// <summary>As above, with an <c>Accept-Language</c> (the server's messages follow it).</summary>
    public static async Task<(System.Net.HttpStatusCode Status, JsonElement Body, string Raw)> PostAsync(
        HttpClient client, string url, object payload, string? user, string? language, params string[] roles)
    {
        var (cookie, xsrf) = await MintAsync(client, user, roles);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        if (language is not null)
            request.Headers.Add("Accept-Language", language);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        AddIdentity(request, user, roles);
        using var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(raw) ? default : JsonDocument.Parse(raw).RootElement.Clone(), raw);
    }

    public static void AddIdentity(HttpRequestMessage request, string? user, string[] roles)
    {
        if (user is not null)
            request.Headers.Add(SeoPrincipalStartupFilter.UserHeader, user);
        if (roles.Length > 0)
            request.Headers.Add(SeoPrincipalStartupFilter.RolesHeader, string.Join(',', roles));
    }

    /// <summary>An antiforgery pair minted for this identity: the token is bound to the user name.</summary>
    private static async Task<(string Cookie, string Xsrf)> MintAsync(HttpClient client, string? user, string[] roles)
    {
        using var warmup = new HttpRequestMessage(HttpMethod.Get, "/spark");
        AddIdentity(warmup, user, roles);
        using var response = await client.SendAsync(warmup);
        string? antiforgery = null, xsrf = null;
        foreach (var raw in response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
        {
            var nameValue = raw.Split(';', 2)[0];
            var eq = nameValue.IndexOf('=');
            if (eq < 0) continue;
            if (nameValue.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal))
                antiforgery = nameValue;
            else if (nameValue[..eq] == "XSRF-TOKEN")
                xsrf = Uri.UnescapeDataString(nameValue[(eq + 1)..]);
        }
        if (antiforgery is null || xsrf is null)
            throw new InvalidOperationException($"GET /spark minted no antiforgery pair ({(int)response.StatusCode}; {string.Join(" | ", response.Headers.Select(h => h.Key))}).");
        return (antiforgery + "; XSRF-TOKEN=" + Uri.EscapeDataString(xsrf), xsrf);
    }
}

/// <summary>The one SeoTester host the tests share (see <see cref="SeoTesterWebAppFactory"/>'s remarks).</summary>
public class SeoTesterHostFixture : SparkTestDriver
{
    /// <summary>Host settings of this fixture (a subclass tunes the crawl for its collection).</summary>
    protected virtual IReadOnlyDictionary<string, string?>? HostSettings => null;

    public IDocumentStore Database => Store;

    public SeoTesterWebAppFactory Factory { get; private set; } = null!;

    /// <summary>The DNS the host's verification sees.</summary>
    public FakeDns Dns { get; } = new();

    /// <summary>The web the host's verification client, crawler and lookups see.</summary>
    public FakeWeb Web { get; } = new();

    /// <summary>The TLS the host's probe sees.</summary>
    public FakeTls Tls { get; } = new();

    /// <summary>Ann owns Acme (Max Member, Vic Viewer); Bob owns Globex; Ann and Max have personal organizations.</summary>
    public Task SeedOrganizationsAsync() => SeedFromJsonAsync("Data/organizations.json");

    /// <summary>A client without a cookie jar: each request carries exactly the cookies the test gives it.</summary>
    public HttpClient CreateClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await SeedOrganizationsAsync();
        Factory = new SeoTesterWebAppFactory(Store, HostSettings, dns: Dns, web: Web, tls: Tls);
        // Boot here, so a startup failure is reported by the fixture and not by whichever test runs first.
        _ = Factory.CreateClient();
    }

    /// <summary>Stops the host and starts a new one on the same database: a deploy or a crash in the middle of a run.</summary>
    public async Task RestartHostAsync()
    {
        await Factory.DisposeAsync();
        Factory = new SeoTesterWebAppFactory(Store, HostSettings, dns: Dns, web: Web, tls: Tls);
        _ = Factory.CreateClient();
    }

    public override async Task DisposeAsync()
    {
        if (Factory is not null)
            await Factory.DisposeAsync();
        await base.DisposeAsync();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SeoTesterHostCollection : ICollectionFixture<SeoTesterHostFixture>
{
    public const string Name = "SeoTester host";
}
