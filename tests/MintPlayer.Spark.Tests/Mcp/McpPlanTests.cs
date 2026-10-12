using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Mcp.Plans;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Mcp._Infrastructure;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using static MintPlayer.Spark.Tests.Mcp._Infrastructure.McpWire;

namespace MintPlayer.Spark.Tests.Mcp;

public class McpWtCar
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string? Note { get; set; }
    public string? Driver { get; set; }
}

public class McpWtDriver
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
}

public class McpWtCarActions(IEntityMapper mapper)
    : DefaultPersistentObjectActions<McpWtCar>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: every row is visible; the subject is MCP plans.";
}

public class McpWtDriverActions(IEntityMapper mapper)
    : DefaultPersistentObjectActions<McpWtDriver>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: every row is visible; the subject is MCP plans.";
}

public class McpWtContext : SparkContext
{
    public IRavenQueryable<McpWtCar> Cars => Session.Query<McpWtCar>();
    public IRavenQueryable<McpWtDriver> Drivers => Session.Query<McpWtDriver>();
}

/// <summary>
/// MCP PRD D8 (plan M4): <c>propose_changes</c> → <c>apply_changes</c>. The plan is bound to the user, the agent and
/// the grant; the echoed diff must match; it runs once, expires, re-checks every right at apply time, and commits all
/// of its operations or none. Fixtures: <c>McpWtCar</c> (agents may read and write it) references <c>McpWtDriver</c>,
/// which no caller may read, so <c>Driver</c> is a hidden reference (D19).
/// </summary>
/// <remarks>
/// Tokens: <c>Bearer plan:{user}:{client}:{grant}:{rw|r}[:car-r]</c> — the last segment narrows the grant's type choice
/// for <c>McpWtCar</c> to Read.
/// </remarks>
public class McpPlanTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private const string Scheme = "PlanBearer";
    private const string CarsQuery = "mcpwtcars";

    private readonly HashSet<string> denied = new(StringComparer.OrdinalIgnoreCase);
    private readonly McpWtRace race = new();
    private readonly McpWtActionLog actionLog = new();
    private readonly McpLogRecorder logs = new();

    private string CarId(string suffix) => $"mcpwtcars/{Scope}-{suffix}";

    private static IEnumerable<EntityTypeFile> Models(EAgentPermission carPermissions) =>
    [
        new()
        {
            PersistentObject = new EntityTypeDefinition
            {
                Id = Guid.Parse("4c9d0000-00b1-4000-8000-0000000000b1"),
                Name = "McpWtCar",
                ClrType = typeof(McpWtCar).FullName!,
                Breadcrumb = "{Name}",
                AgentPermissions = carPermissions,
                Attributes =
                [
                    new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string", IsRequired = true, Order = 1 },
                    new() { Id = Guid.NewGuid(), Name = "Note", DataType = "string", Order = 2 },
                    new() { Id = Guid.NewGuid(), Name = "Driver", DataType = "Reference", ReferenceType = typeof(McpWtDriver).FullName, Order = 3 },
                ],
            },
            // The list a selection is ticked in: a custom action on a selection names it (#467 D12).
            Queries = [new SparkQuery { Id = Guid.Parse("4c9d0000-00b3-4000-8000-0000000000b3"), Name = "AllMcpWtCars", Alias = CarsQuery, Source = "Database.Cars", EntityType = "McpWtCar" }],
        },
        new()
        {
            PersistentObject = new EntityTypeDefinition
            {
                Id = Guid.Parse("4c9d0000-00b2-4000-8000-0000000000b2"),
                Name = "McpWtDriver",
                ClrType = typeof(McpWtDriver).FullName!,
                Breadcrumb = "{Name}",
                AgentPermissions = EAgentPermission.Read | EAgentPermission.Write,
                Attributes = [new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string" }],
            },
        },
    ];

    private async Task<SparkEndpointFactory<McpWtContext>> StartAsync(EAgentPermission carPermissions = EAgentPermission.Read | EAgentPermission.Write)
    {
        await SeedAsync(async session =>
        {
            await session.StoreAsync(new McpWtDriver { Name = "Dora" }, $"mcpwtdrivers/{Scope}-1");
            await session.StoreAsync(new McpWtCar { Name = "Alpha", Note = "first", Driver = $"mcpwtdrivers/{Scope}-1" }, CarId("1"));
            await session.StoreAsync(new McpWtCar { Name = "Bravo", Note = "second" }, CarId("2"));
            await session.StoreAsync(new McpWtCar { Name = "Charlie" }, CarId("3"));
        });

        // security.json allows everything except reading drivers, and whatever a test revokes on the way.
        var access = SparkTestAccessControl.Matching(resource =>
            !resource.EndsWith("/McpWtDriver", StringComparison.OrdinalIgnoreCase) && !denied.Contains(resource));

        return new SparkEndpointFactory<McpWtContext>(
            Store,
            Models(carPermissions),
            configureServices: services =>
            {
                services.AddScoped<McpWtCarActions>();
                services.AddScoped<McpWtDriverActions>();
                services.AddSingleton(race);
                services.AddSingleton(actionLog);
                services.AddScoped<McpWtStampAction>();
                services.AddScoped<McpWtStampSelectionAction>();
                services.AddSingleton(TestActions.LoaderWithCustom(McpWtStampAction.Name, McpWtStampSelectionAction.Name));
                services.AddScoped<ICustomActionResolver>(sp => new McpWtActionResolver(
                    sp.GetRequiredService<McpWtStampAction>(), sp.GetRequiredService<McpWtStampSelectionAction>()));
                services.AddSingleton<ILoggerProvider>(logs);
                services.UseSparkTestAccessControl(access);
            },
            configureSpark: spark =>
            {
                spark.AddCredentialScheme<AuthenticationSchemeOptions, McpTestHost.StubCookieHandler>(McpTestHost.CookieScheme, isAmbient: true);
                spark.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, PlanBearerHandler>(Scheme, _ => { });
                spark.Services.AddSingleton<IAgentCredentialScheme, PlanAgentScheme>();
                spark.AddInterceptor<McpWtRaceInterceptor>();
                spark.AddMcp(mcp =>
                {
                    mcp.ServerName = "Acme Test";
                    mcp.ToolPrefix = "acme";
                    mcp.CanonicalUrl = McpTestHost.CanonicalUrl;
                    mcp.AgentPermissions = _ => EAgentPermission.Read | EAgentPermission.Write;
                });
            },
            security: SparkTestSecurity.Permissive);
    }

    private static Action<HttpRequestMessage> As(string user, string client = "agent-1", string grant = "AgentGrants/g1", string permissions = "rw", string? types = null)
        => r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"plan:{user}:{client}:{grant}:{permissions}" + (types is null ? "" : ":" + types));

    private static object Update(string id, object values) => new { kind = "update", type = "McpWtCar", id, values };

    private static async Task<(string PlanId, JsonArray Changes, Reply Reply)> ProposeAsync(HttpClient client, Action<HttpRequestMessage> caller, params object[] operations)
    {
        var reply = await CallAsync(client, "acme_propose_changes", new { summary = "test plan", operations }, caller);
        reply.IsToolError.Should().BeFalse(reply.ToolText + reply.ErrorMessage);
        var body = Unfence(reply.ToolText);
        return (body["planId"]!.GetValue<string>(), body["changes"]!.AsArray(), reply);
    }

    private static Task<Reply> ApplyAsync(HttpClient client, Action<HttpRequestMessage> caller, string planId, JsonArray changes)
        => CallAsync(client, "acme_apply_changes", new { planId, changes }, caller);

    private async Task<McpWtCar?> LoadCarAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<McpWtCar>(id);
    }

    private async Task<McpPlan> LoadPlanAsync(string planId)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.LoadAsync<McpPlan>(planId))!;
    }

    [Fact]
    public async Task Propose_changes_nothing_and_apply_commits_every_operation_with_its_record()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var alice = As("alice");

        var (planId, changes, _) = await ProposeAsync(client, alice,
            Update(CarId("1"), new { Name = "Alpha 2" }),
            new { kind = "create", type = "McpWtCar", values = new { Name = "Delta", Note = "new" } },
            new { kind = "delete", type = "McpWtCar", id = CarId("3") });

        changes.Select(c => c!.GetValue<string>()).Should().Equal(
            $"1. update McpWtCar 'Alpha' ({CarId("1")})\n   Name: \"Alpha\" -> \"Alpha 2\"",
            "2. create McpWtCar\n   Name = \"Delta\"\n   Note = \"new\"",
            $"3. delete McpWtCar 'Charlie' ({CarId("3")})");
        (await LoadCarAsync(CarId("1")))!.Name.Should().Be("Alpha", "proposing changes nothing");
        (await LoadCarAsync(CarId("3"))).Should().NotBeNull();
        var proposed = await LoadPlanAsync(planId);
        proposed.Status.Should().Be(McpPlanStatus.Proposed);
        proposed.UserId.Should().Be("users/alice");
        proposed.ClientId.Should().Be("agent-1");
        proposed.GrantId.Should().Be("AgentGrants/g1");

        var applied = await ApplyAsync(client, alice, planId, changes);

        applied.IsToolError.Should().BeFalse(applied.ToolText);
        (await LoadCarAsync(CarId("1")))!.Name.Should().Be("Alpha 2");
        (await LoadCarAsync(CarId("1")))!.Driver.Should().Be($"mcpwtdrivers/{Scope}-1",
            "the plan's save keeps the stored value of a reference the caller cannot see (D19; §6.2 'kept on save')");
        (await LoadCarAsync(CarId("3"))).Should().BeNull();
        var ids = Unfence(applied.ToolText)["ids"]!.AsArray();
        var createdId = ids[1]!.GetValue<string>();
        (await LoadCarAsync(createdId))!.Name.Should().Be("Delta");

        using var session = Store.OpenAsyncSession();
        var plan = await session.LoadAsync<McpPlan>(planId);
        plan!.Status.Should().Be(McpPlanStatus.Applied);
        var expires = DateTime.Parse(session.Advanced.GetMetadataFor(plan)[Constants.Documents.Metadata.Expires].ToString()!).ToUniversalTime();
        expires.Should().BeAfter(DateTime.UtcNow.AddDays(300), "an applied plan is kept PlanRetention (a year) as the audit record");
    }

    public static TheoryData<string, string, string> Strangers => new()
    {
        { "bob", "agent-1", "AgentGrants/g1" },     // another user
        { "alice", "agent-2", "AgentGrants/g1" },   // another agent
        { "alice", "agent-1", "AgentGrants/g2" },   // another grant
    };

    [Theory]
    [MemberData(nameof(Strangers))]
    public async Task A_plan_is_bound_to_the_user_agent_and_grant_that_proposed_it(string user, string agent, string grant)
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var (planId, changes, _) = await ProposeAsync(client, As("alice"), Update(CarId("1"), new { Name = "Stolen" }));

        var stranger = await ApplyAsync(client, As(user, agent, grant), planId, changes);

        stranger.IsToolError.Should().BeTrue();
        stranger.ToolText.Should().Be(SparkMcpResult.NotFoundMessage, "another caller's plan answers like an unknown one");
        (await LoadCarAsync(CarId("1")))!.Name.Should().Be("Alpha");
        (await LoadPlanAsync(planId)).Status.Should().Be(McpPlanStatus.Proposed, "a stranger cannot even use the plan up");

        var owner = await ApplyAsync(client, As("alice"), planId, changes);
        owner.IsToolError.Should().BeFalse(owner.ToolText);
    }

    [Fact]
    public async Task An_echo_that_differs_from_the_plan_is_refused()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var alice = As("alice");
        var (planId, changes, _) = await ProposeAsync(client, alice, Update(CarId("1"), new { Name = "Shown" }));

        var tampered = new JsonArray(changes[0]!.GetValue<string>().Replace("Shown", "Hidden"));
        var reply = await ApplyAsync(client, alice, planId, tampered);
        var missing = await ApplyAsync(client, alice, planId, []);

        reply.IsToolError.Should().BeTrue();
        reply.ToolText.Should().Contain("does not match the plan");
        missing.IsToolError.Should().BeTrue();
        (await LoadCarAsync(CarId("1")))!.Name.Should().Be("Alpha");
        (await ApplyAsync(client, alice, planId, changes)).IsToolError.Should().BeFalse("a mistyped echo does not use the plan up");
    }

    [Fact]
    public async Task A_plan_runs_once()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var alice = As("alice");
        var (planId, changes, _) = await ProposeAsync(client, alice, new { kind = "create", type = "McpWtCar", values = new { Name = "Once" } });

        var first = await ApplyAsync(client, alice, planId, changes);
        var replay = await ApplyAsync(client, alice, planId, changes);

        first.IsToolError.Should().BeFalse(first.ToolText);
        replay.IsToolError.Should().BeTrue();
        replay.ToolText.Should().Contain("already used");
        using var session = Store.OpenAsyncSession();
        (await session.Query<McpWtCar>().Customize(c => c.WaitForNonStaleResults()).CountAsync(c => c.Name == "Once")).Should().Be(1);
    }

    [Fact]
    public async Task An_expired_plan_is_refused()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var alice = As("alice");
        var (planId, changes, _) = await ProposeAsync(client, alice, Update(CarId("1"), new { Name = "Late" }));

        // The expiration sweep may run long after; the plan's own expiry decides.
        using (var session = Store.OpenAsyncSession())
        {
            var plan = await session.LoadAsync<McpPlan>(planId);
            plan!.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
            await session.SaveChangesAsync();
        }

        var reply = await ApplyAsync(client, alice, planId, changes);

        reply.IsToolError.Should().BeTrue();
        reply.ToolText.Should().Contain("expired");
        (await LoadCarAsync(CarId("1")))!.Name.Should().Be("Alpha");
    }

    [Fact]
    public async Task A_proposed_plan_expires_ten_minutes_after_it_was_proposed()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var (planId, _, _) = await ProposeAsync(client, As("alice"), Update(CarId("1"), new { Name = "Soon" }));

        using var session = Store.OpenAsyncSession();
        var plan = await session.LoadAsync<McpPlan>(planId);
        var expires = DateTime.Parse(session.Advanced.GetMetadataFor(plan)[Constants.Documents.Metadata.Expires].ToString()!).ToUniversalTime();
        expires.Should().BeCloseTo(plan!.CreatedAt + McpPlanner.ProposalLifetime, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Rights_revoked_in_security_json_between_propose_and_apply_refuse_the_plan()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var alice = As("alice");
        var (planId, changes, _) = await ProposeAsync(client, alice,
            new { kind = "create", type = "McpWtCar", values = new { Name = "Echo" } },
            Update(CarId("1"), new { Name = "Revoked" }));

        denied.Add("Edit/McpWtCar");
        var reply = await ApplyAsync(client, alice, planId, changes);

        reply.IsToolError.Should().BeTrue();
        reply.ToolText.Should().Contain("Operation 2");
        (await LoadCarAsync(CarId("1")))!.Name.Should().Be("Alpha");
        using var session = Store.OpenAsyncSession();
        (await session.Query<McpWtCar>().Customize(c => c.WaitForNonStaleResults()).CountAsync(c => c.Name == "Echo"))
            .Should().Be(0, "the plan's other operation is not saved either");
        (await session.LoadAsync<McpPlan>(planId))!.Status.Should().Be(McpPlanStatus.Failed);
    }

    [Fact]
    public async Task A_grant_narrowed_between_propose_and_apply_refuses_the_plan()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var (planId, changes, _) = await ProposeAsync(client, As("alice"), Update(CarId("1"), new { Name = "Narrowed" }));

        // The same grant, edited by the user to Read for this type: read per call (D12).
        var reply = await ApplyAsync(client, As("alice", types: "car-r"), planId, changes);

        reply.IsToolError.Should().BeTrue();
        (await LoadCarAsync(CarId("1")))!.Name.Should().Be("Alpha");
    }

    [Fact]
    public async Task An_object_changed_since_propose_conflicts_and_nothing_is_saved()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var alice = As("alice");
        var (planId, changes, _) = await ProposeAsync(client, alice,
            Update(CarId("2"), new { Note = "planned" }),
            Update(CarId("1"), new { Name = "Planned" }));

        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<McpWtCar>(CarId("1")))!.Note = "someone else";
            await session.SaveChangesAsync();
        }

        var reply = await ApplyAsync(client, alice, planId, changes);

        reply.IsToolError.Should().BeTrue();
        reply.ToolText.Should().StartWith("Conflict (409)");
        (await LoadCarAsync(CarId("2")))!.Note.Should().Be("second", "the plan's first operation is not saved either");
        (await LoadCarAsync(CarId("1")))!.Name.Should().Be("Alpha");
    }

    [Fact]
    public async Task A_write_racing_the_apply_fails_the_whole_commit()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var alice = As("alice");
        var (planId, changes, _) = await ProposeAsync(client, alice,
            Update(CarId("1"), new { Name = "First" }),
            Update(CarId("2"), new { Name = "Second" }));

        // While the second operation saves — after the first passed every check and sits in the batch — someone else
        // writes the first object. Only RavenDB's change-vector check at the commit can see it.
        race.Arm(trigger: CarId("2"), target: CarId("1"), Store);
        var reply = await ApplyAsync(client, alice, planId, changes);

        race.Fired.Should().BeTrue("the race must actually have happened for this test to say anything");
        reply.IsToolError.Should().BeTrue();
        reply.ToolText.Should().StartWith("Conflict (409)");
        var first = await LoadCarAsync(CarId("1"));
        first!.Name.Should().Be("Alpha");
        first.Note.Should().Be("raced");
        (await LoadCarAsync(CarId("2")))!.Name.Should().Be("Bravo", "one commit: the second operation is not saved either");
        (await LoadPlanAsync(planId)).Status.Should().Be(McpPlanStatus.Failed, "the plan's applied record was in the same commit");
    }

    [Fact]
    public async Task More_than_fifty_operations_are_refused()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var operations = Enumerable.Range(0, McpPlanner.MaxOperations + 1)
            .Select(i => (object)new { kind = "create", type = "McpWtCar", values = new { Name = $"Bulk {i}" } }).ToArray();

        var reply = await CallAsync(client, "acme_propose_changes", new { summary = "too much", operations }, As("alice"));

        reply.IsToolError.Should().BeTrue();
        reply.ToolText.Should().Contain("at most 50 operations");
    }

    [Fact]
    public async Task Anonymous_callers_can_neither_see_nor_call_the_write_tools()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();

        var tools = await ListToolsAsync(client);
        var reply = await CallAsync(client, "acme_propose_changes",
            new { summary = "anonymous", operations = new[] { Update(CarId("1"), new { Name = "Anon" }) } });

        tools.ToolNames.Should().NotContain("acme_propose_changes");
        tools.ToolNames.Should().NotContain("acme_apply_changes");
        (reply.IsToolError || reply.ErrorMessage is not null).Should().BeTrue("an anonymous propose is refused (D8, D17)");
        reply.ToolText.Should().NotContain("planId");
        (await LoadCarAsync(CarId("1")))!.Name.Should().Be("Alpha");

        // Nor can an anonymous caller run a plan it was handed (§6.2 "anonymous write refused").
        var (planId, changes, _) = await ProposeAsync(client, As("alice"), Update(CarId("1"), new { Name = "Alice's" }));
        var anonymousApply = await ApplyAsync(client, _ => { }, planId, changes);

        anonymousApply.ErrorMessage.Should().Be("Unknown tool: 'acme_apply_changes'", "anonymous calls are read-only (D17)");
        (await LoadCarAsync(CarId("1")))!.Name.Should().Be("Alpha");
        (await LoadPlanAsync(planId)).Status.Should().Be(McpPlanStatus.Proposed, "the refused call did not use the plan up");
    }

    [Fact]
    public async Task A_hidden_reference_cannot_be_written_through_a_plan()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var alice = As("alice");

        var hidden = await CallAsync(client, "acme_propose_changes",
            new { summary = "swap driver", operations = new[] { Update(CarId("2"), new { Driver = $"mcpwtdrivers/{Scope}-1" }) } }, alice);
        var unknown = await CallAsync(client, "acme_propose_changes",
            new { summary = "nonsense", operations = new[] { Update(CarId("2"), new { Driver2 = "x" }) } }, alice);
        var create = await CallAsync(client, "acme_propose_changes",
            new { summary = "new with driver", operations = new[] { new { kind = "create", type = "McpWtCar", values = new { Name = "Echo", Driver = $"mcpwtdrivers/{Scope}-1" } } } }, alice);

        hidden.IsToolError.Should().BeTrue();
        hidden.ToolText.Should().Be("Operation 1: 'Driver' is not an attribute you can set on McpWtCar.");
        unknown.ToolText.Should().Be("Operation 1: 'Driver2' is not an attribute you can set on McpWtCar.", "a hidden reference answers like a name that does not exist");
        create.IsToolError.Should().BeTrue();
        (await LoadCarAsync(CarId("2")))!.Driver.Should().BeNull();
    }

    [Fact]
    public async Task A_caller_who_may_not_create_a_type_learns_nothing_about_its_attributes()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        denied.Add("New/McpWtCar");

        var existing = await CallAsync(client, "acme_propose_changes",
            new { summary = "create", operations = new[] { new { kind = "create", type = "McpWtCar", values = new { Name = "No" } } } }, As("alice"));
        var bogus = await CallAsync(client, "acme_propose_changes",
            new { summary = "create", operations = new[] { new { kind = "create", type = "McpWtCar", values = new { Bogus = "No" } } } }, As("alice"));

        existing.ToolText.Should().Be($"Operation 1: {SparkMcpResult.NotFoundMessage}");
        bogus.ToolText.Should().Be($"Operation 1: {SparkMcpResult.NotFoundMessage}", "the type-level right is asked before any attribute is looked at");
    }

    [Fact]
    public async Task A_type_without_Write_for_agents_cannot_be_changed()
    {
        await using var factory = await StartAsync(carPermissions: EAgentPermission.Read);
        var client = factory.CreateClient();

        var reply = await CallAsync(client, "acme_propose_changes",
            new { summary = "edit", operations = new[] { Update(CarId("1"), new { Name = "No" }) } }, As("alice"));

        reply.IsToolError.Should().BeTrue();
        reply.ToolText.Should().Be($"Operation 1: {SparkMcpResult.NotFoundMessage}");
    }

    [Fact]
    public async Task A_grant_without_Write_cannot_propose()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();

        var readOnly = await CallAsync(client, "acme_propose_changes",
            new { summary = "edit", operations = new[] { Update(CarId("1"), new { Name = "No" }) } }, As("alice", permissions: "r"));
        var typeReadOnly = await CallAsync(client, "acme_propose_changes",
            new { summary = "edit", operations = new[] { Update(CarId("1"), new { Name = "No" }) } }, As("alice", types: "car-r"));

        readOnly.ErrorMessage.Should().Contain("Unknown tool", "a grant without changes hides the write tools");
        typeReadOnly.IsToolError.Should().BeTrue();
        typeReadOnly.ToolText.Should().Be($"Operation 1: {SparkMcpResult.NotFoundMessage}");
        (await LoadCarAsync(CarId("1")))!.Name.Should().Be("Alpha");
    }

    [Fact]
    public async Task Propose_and_apply_are_audited_with_hashed_arguments()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var alice = As("alice");
        var (planId, changes, _) = await ProposeAsync(client, alice, Update(CarId("1"), new { Name = "Secret value" }));
        await ApplyAsync(client, alice, planId, changes);

        var events = logs.Messages.Where(m => m.Category == "MintPlayer.Spark.Mcp.Audit").Select(m => m.Text).ToList();
        events.Should().Contain(e => e.StartsWith("MCP call acme_propose_changes by user users/alice, client agent-1"));
        events.Should().Contain(e => e.StartsWith("MCP call acme_apply_changes by user users/alice, client agent-1"));
        events.Should().NotContain(e => e.Contains("Secret value") || e.Contains(planId), "arguments are hashed, never logged");
        events.Should().Contain(e => e.Contains("grant AgentGrants/g1, arguments ") && !e.Contains("arguments -"));
    }

    [Fact]
    public async Task A_custom_action_is_checked_at_propose_and_runs_once_at_apply_in_a_plan_of_its_own()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var alice = As("alice");
        var stamp = new { kind = "action", type = "McpWtCar", action = McpWtStampAction.Name, id = CarId("2") };

        var mixed = await CallAsync(client, "acme_propose_changes",
            new { summary = "mixed", operations = new object[] { stamp, Update(CarId("1"), new { Name = "Mixed" }) } }, alice);
        var (planId, changes, _) = await ProposeAsync(client, alice, stamp);

        mixed.IsToolError.Should().BeTrue();
        mixed.ToolText.Should().Contain("a plan of its own");
        changes.Select(c => c!.GetValue<string>()).Should().Equal($"1. action {McpWtStampAction.Name} on McpWtCar 'Bravo' ({CarId("2")})");
        actionLog.Runs.Should().Be(0, "proposing runs every gate but never the action");

        var applied = await ApplyAsync(client, alice, planId, changes);
        var again = await ApplyAsync(client, alice, planId, changes);

        applied.IsToolError.Should().BeFalse(applied.ToolText);
        again.IsToolError.Should().BeTrue();
        actionLog.Runs.Should().Be(1);
        (await LoadCarAsync(CarId("2")))!.Note.Should().Be("stamped");
        (await LoadPlanAsync(planId)).Status.Should().Be(McpPlanStatus.Applied);
    }

    [Fact]
    public async Task A_custom_action_the_caller_may_not_run_cannot_be_proposed()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        denied.Add($"{McpWtStampAction.Name}/McpWtCar");

        var reply = await CallAsync(client, "acme_propose_changes",
            new { summary = "stamp", operations = new[] { new { kind = "action", type = "McpWtCar", action = McpWtStampAction.Name, id = CarId("2") } } }, As("alice"));

        reply.IsToolError.Should().BeTrue();
        reply.ToolText.Should().Be($"Operation 1: {SparkMcpResult.NotFoundMessage}");
        actionLog.Runs.Should().Be(0);
    }

    private static object Selection(string? query, params string[] ids)
        => new { kind = "action", type = "McpWtCar", action = McpWtStampSelectionAction.Name, query, ids };

    /// <summary>
    /// A custom action on a selection (the query the rows were ticked in and their ids, as the web's grid posts it;
    /// plan M7, "missing tests named by M4"): checked at propose, where the rows are re-read through the query and
    /// nothing runs, and run once at apply on the rows re-read again.
    /// </summary>
    [Fact]
    public async Task A_custom_action_on_a_selection_is_checked_at_propose_and_runs_once_at_apply_on_the_selected_rows()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var alice = As("alice");

        var (planId, changes, _) = await ProposeAsync(client, alice, Selection(CarsQuery, CarId("1"), CarId("2")));

        changes.Select(c => c!.GetValue<string>()).Should().Equal(
            $"1. action {McpWtStampSelectionAction.Name} on McpWtCar selection [{CarId("1")}, {CarId("2")}] of query {CarsQuery}");
        actionLog.Runs.Should().Be(0, "proposing re-reads the selection and runs every gate, never the action");

        var applied = await ApplyAsync(client, alice, planId, changes);
        var again = await ApplyAsync(client, alice, planId, changes);

        applied.IsToolError.Should().BeFalse(applied.ToolText);
        again.IsToolError.Should().BeTrue("a plan runs once");
        actionLog.Runs.Should().Be(1);
        actionLog.SelectedRows.OrderBy(r => r, StringComparer.Ordinal).Should().Equal(CarId("1"), CarId("2"));
        (await LoadCarAsync(CarId("1")))!.Note.Should().Be("selected");
        (await LoadCarAsync(CarId("2")))!.Note.Should().Be("selected");
        (await LoadCarAsync(CarId("3")))!.Note.Should().BeNull("a row outside the selection is untouched");
        (await LoadPlanAsync(planId)).Status.Should().Be(McpPlanStatus.Applied);
    }

    /// <summary>
    /// A selection is all or nothing, as on the web: a row the caller cannot see (here: no such row) refuses the whole
    /// proposal with the uniform not-found, a selection must name the query it was ticked in, and a selected row gone by
    /// the time of apply refuses the plan before the action runs. Selected rows carry no etag (only the object an action
    /// runs on does); they are re-read at apply, so a smaller selection never runs in their place.
    /// </summary>
    [Fact]
    public async Task A_selection_is_all_or_nothing_at_propose_and_again_at_apply()
    {
        await using var factory = await StartAsync();
        var client = factory.CreateClient();
        var alice = As("alice");

        var unseen = await CallAsync(client, "acme_propose_changes",
            new { summary = "stamp", operations = new[] { Selection(CarsQuery, CarId("1"), CarId("missing")) } }, alice);
        var noQuery = await CallAsync(client, "acme_propose_changes",
            new { summary = "stamp", operations = new[] { Selection(null, CarId("1")) } }, alice);

        unseen.IsToolError.Should().BeTrue();
        unseen.ToolText.Should().Be($"Operation 1: {SparkMcpResult.NotFoundMessage}", "a row the caller cannot see and a missing one answer alike");
        noQuery.IsToolError.Should().BeTrue();
        noQuery.ToolText.Should().Contain("must name the query its rows were selected in");

        var (planId, changes, _) = await ProposeAsync(client, alice, Selection(CarsQuery, CarId("1"), CarId("2")));
        using (var session = Store.OpenAsyncSession())
        {
            session.Delete(CarId("2"));
            await session.SaveChangesAsync();
        }

        var applied = await ApplyAsync(client, alice, planId, changes);

        applied.IsToolError.Should().BeTrue("the selection is re-read at apply, and one of its rows is gone");
        applied.ToolText.Should().Be($"Operation 1: {SparkMcpResult.NotFoundMessage}");
        actionLog.Runs.Should().Be(0, "nothing ran: not at propose, not at the refused apply");
        (await LoadCarAsync(CarId("1")))!.Note.Should().Be("first", "the row still there is not acted on alone");
        (await LoadPlanAsync(planId)).Status.Should().Be(McpPlanStatus.Failed);
    }

    internal static JsonNode Unfence(string text)
    {
        var start = text.IndexOf("<data-", StringComparison.Ordinal) + 1;
        var tag = text[start..text.IndexOf('>', start)];
        var open = text.LastIndexOf($"<{tag}>", StringComparison.Ordinal) + tag.Length + 2;
        var close = text.LastIndexOf($"</{tag}>", StringComparison.Ordinal);
        return JsonNode.Parse(text[open..close])!;
    }

    /// <summary>The agent token: <c>plan:{user}:{client}:{grant}:{rw|r}[:car-r]</c>.</summary>
    public sealed class PlanBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer plan:", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());

            var parts = header["Bearer plan:".Length..].Split(':');
            var identity = new ClaimsIdentity(
            [
                new Claim("sub", "users/" + parts[0]),
                new Claim("client", parts[1]),
                new Claim("grant", parts[2]),
                new Claim("perm", parts[3]),
                new Claim("types", parts.Length > 4 ? parts[4] : ""),
            ], McpPlanTests.Scheme, "sub", "role");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), McpPlanTests.Scheme)));
        }
    }

    /// <summary>The token's registration: setup 1, local subject; client, grant and permissions from the token.</summary>
    public sealed class PlanAgentScheme : IAgentCredentialScheme
    {
        public EAgentAuthentication Setup => EAgentAuthentication.PersonalAccessToken;
        public string Scheme => McpPlanTests.Scheme;
        public bool SubjectIsLocalUserId => true;

        public ValueTask<AgentCredential?> DescribeAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            var permissions = principal.FindFirst("perm")?.Value == "rw" ? EAgentPermission.Read | EAgentPermission.Write : EAgentPermission.Read;
            IReadOnlyDictionary<string, EAgentPermission>? types = principal.FindFirst("types")?.Value == "car-r"
                ? new Dictionary<string, EAgentPermission> { ["McpWtCar"] = EAgentPermission.Read, ["McpWtDriver"] = EAgentPermission.Read }
                : null;
            var grant = principal.FindFirst("grant")!.Value;
            return ValueTask.FromResult<AgentCredential?>(new(
                new AgentInformation(EAgentKind.PersonalAccessToken, principal.FindFirst("client")!.Value, null, null, principal) { GrantId = grant },
                new AgentGrantScope(grant, permissions, types)));
        }
    }
}

/// <summary>Writes <see cref="Target"/> from another session while <see cref="Trigger"/> is being saved, once.</summary>
public sealed class McpWtRace
{
    private string? trigger;
    private IDocumentStore? store;

    public string? Target { get; private set; }
    public bool Fired { get; private set; }

    public void Arm(string trigger, string target, IDocumentStore store)
    {
        this.trigger = trigger;
        Target = target;
        this.store = store;
    }

    public async Task FireIfArmedAsync(string? savingId)
    {
        if (trigger is null || !string.Equals(savingId, trigger, StringComparison.OrdinalIgnoreCase))
            return;
        trigger = null;
        using var session = store!.OpenAsyncSession();
        (await session.LoadAsync<McpWtCar>(Target))!.Note = "raced";
        await session.SaveChangesAsync();
        Fired = true;
    }
}

/// <summary>How often the custom actions ran, and the rows a selection action was handed.</summary>
public sealed class McpWtActionLog
{
    private int runs;
    public int Runs => runs;
    public ConcurrentQueue<string> SelectedRows { get; } = new();
    public void Ran() => Interlocked.Increment(ref runs);

    public void Ran(IEnumerable<string> selectedRows)
    {
        Ran();
        foreach (var row in selectedRows)
            SelectedRows.Enqueue(row);
    }
}

/// <summary>A custom action that writes the object it runs on through the gated write path (it commits by itself).</summary>
public sealed class McpWtStampAction(IDatabaseAccess databaseAccess, McpWtActionLog log) : ICustomAction
{
    public const string Name = "McpWtStamp";

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        log.Ran();
        var po = await databaseAccess.GetPersistentObjectAsync(Guid.Parse("4c9d0000-00b1-4000-8000-0000000000b1"), args.Parent!.Id!);
        po!["Note"].SetValue("stamped");
        await databaseAccess.SavePersistentObjectAsync(po);
    }
}

/// <summary>
/// A custom action on a selection: writes every row it is handed, which the framework re-reads through the query the
/// selection was ticked in (<c>CustomActionArgs.SelectedItems</c>), never the ids as typed.
/// </summary>
public sealed class McpWtStampSelectionAction(IDatabaseAccess databaseAccess, McpWtActionLog log) : ICustomAction
{
    public const string Name = "McpWtStampSelection";

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        log.Ran(args.SelectedItems.Select(i => i.Id!));
        foreach (var item in args.SelectedItems)
        {
            var po = await databaseAccess.GetPersistentObjectAsync(Guid.Parse("4c9d0000-00b1-4000-8000-0000000000b1"), item.Id!);
            po!["Note"].SetValue("selected");
            await databaseAccess.SavePersistentObjectAsync(po);
        }
    }
}

internal sealed class McpWtActionResolver(McpWtStampAction stamp, McpWtStampSelectionAction selection) : ICustomActionResolver
{
    public ICustomAction? Resolve(string name)
        => string.Equals(name, McpWtStampAction.Name, StringComparison.OrdinalIgnoreCase) ? stamp
            : string.Equals(name, McpWtStampSelectionAction.Name, StringComparison.OrdinalIgnoreCase) ? selection
            : null;

    public IReadOnlyList<string> GetRegisteredActionNames() => [McpWtStampAction.Name, McpWtStampSelectionAction.Name];
}

public sealed class McpWtRaceInterceptor(McpWtRace race) : IBeforeSave
{
    public bool AppliesTo(Type entityType) => entityType == typeof(McpWtCar);

    public async ValueTask OnBeforeSaveAsync(SaveContext context) => await race.FireIfArmedAsync(context.PersistentObject.Id);
}

/// <summary>Every log line, for the audit assertions.</summary>
internal sealed class McpLogRecorder : ILoggerProvider
{
    public ConcurrentQueue<(string Category, string Text)> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);

    public void Dispose() { }

    private sealed class Recorder(McpLogRecorder owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Messages.Enqueue((category, formatter(state, exception)));
    }
}
