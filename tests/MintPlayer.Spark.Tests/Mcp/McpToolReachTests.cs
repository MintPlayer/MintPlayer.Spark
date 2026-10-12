using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Mcp._Infrastructure;
using ModelContextProtocol.Server;
using static MintPlayer.Spark.Tests.Mcp._Infrastructure.McpWire;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>
/// What an application's own tool may reach beyond the type layer (decision log "M5 implementation"; MCP PRD §4.7: SeoTester's
/// <c>PageResult</c> stays <c>None</c> and is "reached through the tools", and its one write runs <c>StartAudit</c> on a type
/// agents may only read). <see cref="McpReadsThroughAttribute"/> lends a type the read layers of another, for that tool only;
/// <see cref="McpRunsActionAttribute"/> lets one named action need only <c>Read</c> from the type and the grant's choice of
/// types. Neither widens a write, another action, another tool, or the generic tools; <c>security.json</c> and row security
/// still decide. <c>GuardedDoc</c> is exposed <c>Read</c>; <c>FilteredDoc</c> is <c>None</c> (McpTestHost).
/// </summary>
public class McpToolReachTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private const string FilteredId = "FilteredDocs/reach-alice";
    private const string GuardedId = "GuardedDocs/reach-visible";

    private readonly ReachCounter counter = new();

    private async Task SeedAsync()
    {
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(new FilteredDoc { Name = "beta", Owner = "alice" }, FilteredId);
        await session.StoreAsync(new GuardedDoc { Name = "alpha", IsVisible = true }, GuardedId);
        await session.SaveChangesAsync();
    }

    private SparkEndpointFactory<GuardedContext> Host(Action<SparkMcpOptions>? configure = null)
        => McpTestHost.Create<GuardedContext>(
            Store,
            McpTestHost.Models(),
            SparkTestSecurity.Permissive,
            mcp =>
            {
                mcp.AddTools<ReachTools>();
                configure?.Invoke(mcp);
            },
            configureServices: services =>
            {
                services.AddSingleton(counter);
                services.AddScoped<ReachPingAction>();
                services.AddSingleton(TestActions.LoaderWithCustom("Ping", "Pong"));
                services.AddScoped<ICustomActionResolver>(sp => new ReachResolver(sp.GetRequiredService<ReachPingAction>()));
            });

    [Fact]
    public async Task A_tool_reads_an_unexposed_type_only_through_the_type_it_declares()
    {
        await SeedAsync();
        await using var factory = Host();
        var client = factory.CreateClient();

        var direct = await CallAsync(client, "acme_filtered_direct", new { id = FilteredId }, Bearer("alice"));
        var through = await CallAsync(client, "acme_filtered_through", new { id = FilteredId }, Bearer("alice"));
        var generic = await CallAsync(client, "acme_get", new { type = "FilteredDoc", id = FilteredId }, Bearer("alice"));

        direct.ToolText.Should().Be("refused", "FilteredDoc's agentPermissions is None: a tool that declares nothing cannot read it");
        through.ToolText.Should().Be("loaded beta", "read as part of GuardedDoc, which agents may read");
        generic.ToolText.Should().Be(SparkMcpResult.NotFoundMessage, "the generic tools keep the type layer");
    }

    [Fact]
    public async Task Reading_through_another_type_never_lets_the_tool_write_it()
    {
        await SeedAsync();
        await using var factory = Host();

        var save = await CallAsync(factory.CreateClient(), "acme_filtered_save", new { id = FilteredId }, Bearer("alice"));

        save.ToolText.Should().Be("loaded, save refused", "McpReadsThrough lends the read layers only");
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<FilteredDoc>(FilteredId)).Name.Should().Be("beta");
    }

    [Fact]
    public async Task A_declared_action_runs_on_a_read_only_type_and_no_other_action_or_tool_does()
    {
        await SeedAsync();
        await using var factory = Host();
        var client = factory.CreateClient();

        var declared = await CallAsync(client, "acme_ping", new { id = GuardedId }, Bearer("alice"));
        var undeclared = await CallAsync(client, "acme_ping_undeclared", new { id = GuardedId }, Bearer("alice"));
        var otherAction = await CallAsync(client, "acme_pong_from_ping_tool", new { id = GuardedId }, Bearer("alice"));

        declared.ToolText.Should().Be("Executed");
        undeclared.ToolText.Should().Be("Refused", "GuardedDoc is Read for agents: an action needs Write unless the tool declares it");
        otherAction.ToolText.Should().Be("Refused", "the declaration names one action");
        counter.Count.Should().Be(1, "only the declared action ran");
    }

    [Fact]
    public async Task A_declared_action_still_needs_changes_from_the_application_layer()
    {
        await SeedAsync();
        await using var factory = Host(mcp => mcp.AgentPermissions = _ => EAgentPermission.Read);
        var client = factory.CreateClient();

        var tools = await ListToolsAsync(client, Bearer("alice"));
        var call = await CallAsync(client, "acme_ping", new { id = GuardedId }, Bearer("alice"));

        tools.ToolNames.Should().NotContain("acme_ping").And.Contain("acme_filtered_through");
        call.ErrorMessage.Should().Be("Unknown tool: 'acme_ping'");
        counter.Count.Should().Be(0);
    }

    [Fact]
    public void A_read_only_tool_that_runs_an_action_refuses_startup()
    {
        var act = () => McpTestHost.Create<GuardedContext>(Store, McpTestHost.Models(), SparkTestSecurity.Permissive, mcp => mcp.AddTools<MisdeclaredTools>()).CreateClient();

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("[McpRunsAction]");
    }

    // ── the tools ──────────────────────────────────────────────────────────────────────────────────

    [McpServerToolType]
    public sealed class ReachTools(IDatabaseAccess database, IModelLoader models, ISparkMcpActions actions)
    {
        [McpServerTool(Name = "filtered_direct", ReadOnly = true)]
        public Task<string> FilteredDirectAsync(string id) => LoadAsync(id);

        [McpServerTool(Name = "filtered_through", ReadOnly = true)]
        [McpReadsThrough("FilteredDoc", "GuardedDoc")]
        public Task<string> FilteredThroughAsync(string id) => LoadAsync(id);

        [McpServerTool(Name = "filtered_save")]
        [McpReadsThrough("FilteredDoc", "GuardedDoc")]
        public async Task<string> FilteredSaveAsync(string id)
        {
            var po = await database.GetPersistentObjectAsync(models.GetEntityTypeByName("FilteredDoc")!.Id, id);
            if (po is null)
                return "refused";
            po["Name"].Value = "changed by an agent";
            po["Name"].IsValueChanged = true;
            try
            {
                await database.SavePersistentObjectAsync(po);
                return "loaded, saved";
            }
            catch (SparkAccessDeniedException)
            {
                return "loaded, save refused";
            }
        }

        [McpServerTool(Name = "ping")]
        [McpRunsAction("GuardedDoc", "Ping")]
        public async Task<string> PingAsync(string id) => (await actions.RunAsync("GuardedDoc", "Ping", id)).Status.ToString();

        [McpServerTool(Name = "ping_undeclared")]
        public async Task<string> PingUndeclaredAsync(string id) => (await actions.RunAsync("GuardedDoc", "Ping", id)).Status.ToString();

        [McpServerTool(Name = "pong_from_ping_tool")]
        [McpRunsAction("GuardedDoc", "Ping")]
        public async Task<string> PongAsync(string id) => (await actions.RunAsync("GuardedDoc", "Pong", id)).Status.ToString();

        private async Task<string> LoadAsync(string id)
        {
            try
            {
                return await database.GetPersistentObjectAsync(models.GetEntityTypeByName("FilteredDoc")!.Id, id) is { } po
                    ? "loaded " + po["Name"].Value
                    : "refused";
            }
            catch (SparkAccessDeniedException)
            {
                return "refused";
            }
        }
    }

    [McpServerToolType]
    public sealed class MisdeclaredTools
    {
        [McpServerTool(Name = "sneaky", ReadOnly = true)]
        [McpRunsAction("GuardedDoc", "Ping")]
        public string Sneaky() => "ran";
    }

    public sealed class ReachCounter
    {
        private int count;
        public int Count => count;
        public void Add() => Interlocked.Increment(ref count);
    }

    public sealed class ReachPingAction(ReachCounter counter) : ICustomAction
    {
        public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        {
            counter.Add();
            args.SetResult(new { pinged = args.Parent?.Id });
            return Task.CompletedTask;
        }
    }

    private sealed class ReachResolver(ICustomAction action) : ICustomActionResolver
    {
        public ICustomAction? Resolve(string name) => name is "Ping" or "Pong" ? action : null;
        public IReadOnlyList<string> GetRegisteredActionNames() => ["Ping", "Pong"];
    }
}
