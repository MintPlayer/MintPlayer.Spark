using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Settings;
using MintPlayer.Spark.Settings.Quotas;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Mcp._Infrastructure;
using MintPlayer.Spark.Tests.Organizations;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Settings;

/// <summary>
/// X3 (<c>docs/seo_analyzer_PRD.md</c>): per-user quotas on custom actions, evaluated after authentication and
/// every other gate, with the limit and window read from settings (X9), counted in a sliding window.
/// </summary>
public class SparkQuotaTests : SparkTestDriver
{
    private static readonly Guid NoteTypeId = Guid.Parse("0a3c0000-0000-4000-8000-000000000001");

    public static class QpQuotas
    {
        public static readonly SparkQuota Probes = new("probes",
            new SparkSetting<int>("Test:Quotas:Probes:Limit", 5, "Probes per window."),
            new SparkSetting<TimeSpan>("Test:Quotas:Probes:Window", TimeSpan.FromDays(1), "The probes window."));
    }

    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly QpRuns runs = new();
    private SparkEndpointFactory<QpContext>? factory;

    public override async Task DisposeAsync()
    {
        if (factory is not null)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private SparkEndpointFactory<QpContext> Factory => factory ??= new SparkEndpointFactory<QpContext>(
        Store,
        [new EntityTypeFile
        {
            PersistentObject = new EntityTypeDefinition
            {
                Id = NoteTypeId,
                Name = "QpNote",
                ClrType = typeof(QpNote).FullName!,
                Attributes = [new() { Id = Guid.NewGuid(), Name = "Title", DataType = "string" }],
            },
        }],
        configureServices: services =>
        {
            services.AddSingleton<IStartupFilter, OgPrincipalStartupFilter>();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(runs);
            services.AddScoped<QpProbeAction>();
            services.AddSingleton(TestActions.LoaderWithCustom("QpProbe"));
            services.AddScoped<ICustomActionResolver>(sp => new QpResolver(sp.GetRequiredService<QpProbeAction>()));
        },
        configureSpark: spark => spark.AddQuotas(typeof(QpQuotas)),
        security: SparkTestSecurity.Permissive);

    [Fact]
    public async Task The_sixth_run_in_a_day_is_a_429_with_Retry_After_and_does_not_run()
    {
        for (var i = 0; i < 5; i++)
        {
            (await ProbeAsync("SparkUsers/ann")).Status.Should().Be(HttpStatusCode.OK);
            clock.Advance(TimeSpan.FromHours(1));
        }

        var (status, retryAfter, body) = await ProbeAsync("SparkUsers/ann");

        status.Should().Be(HttpStatusCode.TooManyRequests);
        retryAfter.Should().Be(TimeSpan.FromHours(19), "the first use (5 h ago) leaves the 24 h window in 19 h");
        body.Should().Contain("probes");
        runs.Count.Should().Be(5, "a refused run never reaches the action");
        (await ProbeAsync("SparkUsers/bob")).Status.Should().Be(HttpStatusCode.OK, "quotas are per user");
    }

    [Fact]
    public async Task The_window_slides_a_use_leaves_it_exactly_one_window_later()
    {
        for (var i = 0; i < 5; i++)
            (await ProbeAsync("SparkUsers/ann")).Status.Should().Be(HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromHours(23));
        (await ProbeAsync("SparkUsers/ann")).Status.Should().Be(HttpStatusCode.TooManyRequests);
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        (await ProbeAsync("SparkUsers/ann")).Status.Should().Be(HttpStatusCode.OK);
        (await ProbeAsync("SparkUsers/ann")).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_limit_and_window_are_settings_an_administrator_changes_at_runtime()
    {
        await SetAsync("Test:Quotas:Probes:Limit", "1");
        (await ProbeAsync("SparkUsers/ann")).Status.Should().Be(HttpStatusCode.OK);
        (await ProbeAsync("SparkUsers/ann")).Status.Should().Be(HttpStatusCode.TooManyRequests);

        await SetAsync("Test:Quotas:Probes:Limit", "-1");
        for (var i = 0; i < 8; i++)
            (await ProbeAsync("SparkUsers/ann")).Status.Should().Be(HttpStatusCode.OK, "a negative limit is unlimited");

        await SetAsync("Test:Quotas:Probes:Limit", "0");
        (await ProbeAsync("SparkUsers/bob")).Status.Should().Be(HttpStatusCode.TooManyRequests, "0 refuses every use");

        await SetAsync("Test:Quotas:Probes:Limit", "2");
        await SetAsync("Test:Quotas:Probes:Window", "00:10:00");
        (await ProbeAsync("SparkUsers/cy")).Status.Should().Be(HttpStatusCode.OK);
        (await ProbeAsync("SparkUsers/cy")).Status.Should().Be(HttpStatusCode.OK);
        var (status, retryAfter, _) = await ProbeAsync("SparkUsers/cy");
        status.Should().Be(HttpStatusCode.TooManyRequests);
        retryAfter.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Concurrent_uses_never_overspend_the_limit()
    {
        var quotas = Factory.CreateScope().ServiceProvider.GetRequiredService<ISparkQuotas>();
        var decisions = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => quotas.TryConsumeAsync(QpQuotas.Probes, "SparkUsers/race")));

        decisions.Count(d => d.Allowed).Should().Be(5);
        (await quotas.GetUsedAsync(QpQuotas.Probes, "SparkUsers/race")).Should().Be(5);
        using var session = Store.OpenAsyncSession();
        var usage = await session.LoadAsync<SparkQuotaUsage>("SparkQuotaUsages/probes/SparkUsers/race");
        usage.ExpiresAtUtc.Should().Be(clock.GetUtcNow().UtcDateTime.AddDays(1), "the usage document expires with its last use (X1)");
    }

    [Fact]
    public async Task An_anonymous_caller_is_counted_by_address()
    {
        for (var i = 0; i < 5; i++)
            (await ProbeAsync(null)).Status.Should().Be(HttpStatusCode.OK);
        (await ProbeAsync(null)).Status.Should().Be(HttpStatusCode.TooManyRequests);
        (await ProbeAsync("SparkUsers/ann")).Status.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// MCP PRD D4: an AI agent spends its user's quota, never one of its own per address. A real personal access token
    /// through <c>/mcp</c>: its principal carries the local user id as <c>NameIdentifier</c> (every agent setup does,
    /// <c>McpAgentPrincipalTests</c>), which the key reads as it reads the sign-in cookie's. Red with the claim off the
    /// token's principal: <c>ip:…</c>.
    /// </summary>
    [Fact]
    public async Task An_agents_bearer_caller_spends_its_users_quota_not_one_per_address()
    {
        await using var host = McpAccountHost.Create(Store, mcp => mcp.AddTools<QuotaKeyProbeTools>());
        var user = await McpAccountHost.CreateUserAsync(host, "quota-agent");
        var browser = await McpBrowser.SignInAsync(host.CreateClient(), "quota-agent");
        var (token, _) = await browser.CreateTokenAsync("quota-probe");

        var reply = await McpWire.CallAsync(host.CreateClient(), "acme_quota_key", new { }, McpAgentClient.Bearer(token));

        reply.ToolText.Should().Be("key=" + user.Id);
    }

    /// <summary>The quota key of the caller, as a custom action's quota gate computes it.</summary>
    [ModelContextProtocol.Server.McpServerToolType]
    public sealed class QuotaKeyProbeTools(Microsoft.AspNetCore.Http.IHttpContextAccessor accessor)
    {
        [ModelContextProtocol.Server.McpServerTool(Name = "quota_key", ReadOnly = true)]
        public ModelContextProtocol.Protocol.CallToolResult QuotaKey()
            => MintPlayer.Spark.Mcp.SparkMcpResult.Text("key=" + ISparkQuotas.KeyFor(accessor.HttpContext!));
    }

    [Fact]
    public void Startup_refuses_an_action_naming_an_undeclared_quota()
    {
        var empty = new SparkQuotaCatalog();
        var act = () => SparkQuotaStartup.VerifyAttributes(empty);
        act.Should().Throw<InvalidOperationException>().WithMessage("*QpProbeAction*probes*");

        var declared = new SparkQuotaCatalog();
        declared.AddFrom(typeof(QpQuotas));
        var ok = () => SparkQuotaStartup.VerifyAttributes(declared);
        ok.Should().NotThrow();
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private async Task SetAsync(string key, string value)
    {
        _ = Factory;
        using (var session = Store.OpenAsyncSession())
        {
            var setting = await session.LoadAsync<MintPlayer.Spark.Settings.Models.Setting>("Settings/" + key)
                ?? throw new InvalidOperationException("not seeded: " + key);
            setting.Value = value;
            await session.SaveChangesAsync();
        }
        Factory.GetService<ISparkSettings>().Invalidate();
    }

    private async Task<(HttpStatusCode Status, TimeSpan? RetryAfter, string Body)> ProbeAsync(string? userId)
    {
        using var client = Factory.CreateClient();
        // The antiforgery token is bound to the signed-in user, so it is minted as that user.
        var (cookie, xsrf) = await OrganizationsTestHost.MintAsync(client, userId);
        var request = new HttpRequestMessage(HttpMethod.Post, "/spark/actions/execute") { Content = JsonContent.Create(Wire.Action(NoteTypeId, "QpProbe")) };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-XSRF-TOKEN", xsrf);
        if (userId is not null)
            request.Headers.Add(OgPrincipalStartupFilter.Header, userId);
        var response = await client.SendAsync(request);
        return (response.StatusCode, response.Headers.RetryAfter?.Delta, await response.Content.ReadAsStringAsync());
    }

    public sealed class QpRuns
    {
        private int count;
        public int Count => count;
        public void Add() => Interlocked.Increment(ref count);
    }

    [SparkQuota("probes")]
    public sealed class QpProbeAction(QpRuns runs) : ICustomAction
    {
        public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        {
            runs.Add();
            return Task.CompletedTask;
        }
    }

    private sealed class QpResolver(ICustomAction action) : ICustomActionResolver
    {
        public ICustomAction? Resolve(string name) => string.Equals(name, "QpProbe", StringComparison.OrdinalIgnoreCase) ? action : null;
        public IReadOnlyList<string> GetRegisteredActionNames() => ["QpProbe"];
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}

public class QpNote
{
    public string? Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

public class QpContext : SparkContext
{
    public IRavenQueryable<QpNote> Notes => Session.Query<QpNote>();
}
