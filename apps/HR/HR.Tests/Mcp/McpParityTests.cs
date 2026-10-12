using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace HR.Tests.Mcp;

/// <summary>
/// Spike S5 (MCP PRD §6.1, D9; plan M7): authorization parity on HR's real application. Three users with different rights
/// (HR's own "HR managers" and "Viewers", and <see cref="HrAgentHost.RestrictedGroup"/>: people without their company or
/// e-mail) and an anonymous caller ask each generic tool and the web endpoint it mirrors the same question, with the
/// same personal access token's user and the same sign-in cookie's user.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two kinds of assertion.</b> Each answer is compared with the web's (the parity, D9), and each also states what that
/// user should see (the expectations). Both are needed: the shared services serve both surfaces, so a check removed from
/// one of them changes both answers alike and only an expectation notices (the mutation run, spikes S5).
/// </para>
/// <para>
/// <b>What may differ, by design.</b> The agent layers narrow (D6): a type HR does not expose to agents
/// (<c>"agentPermissions"</c> absent: <c>Address</c>, <c>Car</c>) is not there for the agent as a type (discover,
/// describe, query, get); its embedded values inside an exposed object (<c>Person.Address</c>) are the object's, shown as
/// the web and the query rows show them. Anonymous refusals are the sign-in text where the web answers 401 (D17, personal
/// tokens only: no OAuth challenge); a write the save would drop silently is refused by name (M4). Detail rows are counted
/// by <c>get</c> unless asked for (<c>include</c>), as the comparison asks for a person's jobs.
/// </para>
/// </remarks>
public class McpParityTests : SparkTestDriver
{
    private const string Prefix = "hr_";

    /// <summary>The types HR's model exposes to agents (<c>"agentPermissions"</c>), and their access.</summary>
    private static readonly HashSet<string> Exposed = ["Person", "CarreerJob", "Company", "Profession"];

    private static readonly (string Name, string? Group)[] Users =
    [
        ("manager", HrAgentHost.Managers),
        ("viewer", HrAgentHost.Viewers),
        ("restricted", HrAgentHost.RestrictedGroup),
    ];

    private sealed record Caller(string Name, HrBrowser Web, string? Token)
    {
        public bool IsAnonymous => Token is null;
    }

    private async Task<(HrAgentHost Host, IReadOnlyList<Caller> Callers)> StartAsync()
    {
        var host = new HrAgentHost(Store);
        _ = host.Services; // boots HR: its indexes, its migrations
        await SeedFromJsonAsync("Data/parity.json");

        var callers = new List<Caller>();
        foreach (var (name, group) in Users)
        {
            await host.CreateUserAsync($"{name}@hr.test", group);
            var web = await host.SignInAsync($"{name}@hr.test");
            callers.Add(new Caller(name, web, await web.CreateTokenAsync()));
        }
        callers.Add(new Caller("anonymous", new HrBrowser(host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false })), null));

        // Every list once, so the auto indexes exist and are current before two answers are compared. A query is
        // reached by its alias ("GetPeople" → "people"), as discover names it; its name is not an address.
        foreach (var query in new[] { "people", "companies", "professions" })
        {
            var (status, body) = await callers[0].Web.JsonAsync(HttpMethod.Post, "/spark/queries/execute", new { queryId = query, take = 100 });
            status.Should().Be(HttpStatusCode.OK, $"warming {query}: {body?.ToJsonString()}");
        }
        await Store.WaitForIndexingAsync();
        return (host, callers);
    }

    private static Task<HrMcp.Reply> AgentAsync(HrAgentHost host, Caller caller, string tool, object arguments)
        => HrMcp.CallAsync(host.CreateClient(), Prefix + tool, arguments, caller.Token);

    // ── discover ↔ GET /spark/types + GET /spark/queries ─────────────────────────────────────────────

    [Fact]
    public async Task Discover_lists_what_the_web_lists_narrowed_to_the_types_HR_exposes()
    {
        var (host, callers) = await StartAsync();
        await using var _ = host;
        var differences = new List<string>();

        foreach (var caller in callers)
        {
            var mcp = await AgentAsync(host, caller, "discover", new { take = 100 });
            mcp.IsToolError.Should().BeFalse($"{caller.Name}: {mcp.Text}");
            var agentTypes = HrMcp.Unfence(mcp.Text)["types"]!.AsArray().ToDictionary(
                t => t!["name"]!.GetValue<string>(),
                t => t!["queries"]!.AsArray().Select(q => q!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList());

            var (typesStatus, webTypes) = await caller.Web.JsonAsync(HttpMethod.Get, "/spark/types");
            var (queriesStatus, webQueries) = await caller.Web.JsonAsync(HttpMethod.Get, "/spark/queries");
            typesStatus.Should().Be(HttpStatusCode.OK);
            queriesStatus.Should().Be(HttpStatusCode.OK);
            var webNames = webTypes!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();

            Compare(differences, $"{caller.Name} types", agentTypes.Keys.Order(StringComparer.Ordinal), webNames.Where(Exposed.Contains).Order(StringComparer.Ordinal));
            foreach (var type in agentTypes.Keys)
            {
                var webTypeQueries = webQueries!.AsArray()
                    .Where(q => q!["entityType"]?.GetValue<string>() == type && q["parentReference"] is null)
                    .Select(q => (q!["alias"] ?? q["name"])!.GetValue<string>())
                    .Order(StringComparer.Ordinal);
                Compare(differences, $"{caller.Name} queries of {type}", agentTypes[type], webTypeQueries);
            }

            // What each caller should see.
            var expected = caller.Name switch
            {
                "manager" or "viewer" => (Has: new[] { "Person", "CarreerJob", "Company", "Profession" }, Lacks: Array.Empty<string>()),
                "restricted" => (Has: new[] { "Person", "CarreerJob", "Profession" }, Lacks: new[] { "Company" }),
                _ => (Has: new[] { "Company", "Profession" }, Lacks: new[] { "Person", "CarreerJob" }),
            };
            expected.Has.Except(agentTypes.Keys).Should().BeEmpty($"{caller.Name}'s rights");
            agentTypes.Keys.Intersect(expected.Lacks).Should().BeEmpty($"{caller.Name}'s rights");
            agentTypes.Keys.Should().NotContain("Address", "HR does not expose addresses to agents (no agentPermissions)");
        }

        differences.Should().BeEmpty();
    }

    // ── describe ↔ GET /spark/types/{type} ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Describe_answers_the_attributes_the_web_definition_has()
    {
        var (host, callers) = await StartAsync();
        await using var _ = host;
        var differences = new List<string>();

        foreach (var caller in callers)
        {
            foreach (var type in new[] { "Person", "CarreerJob", "Company", "Profession", "Address", "NoSuchType" })
            {
                var mcp = await AgentAsync(host, caller, "describe", new { type });
                var (status, web) = await caller.Web.JsonAsync(HttpMethod.Get, $"/spark/types/{type}");
                var what = $"{caller.Name} describe {type}";

                if (status != HttpStatusCode.OK || !Exposed.Contains(type))
                {
                    ExpectRefusal(differences, what, caller, mcp, status, agentNarrowed: status == HttpStatusCode.OK);
                    continue;
                }

                if (mcp.IsToolError)
                {
                    differences.Add($"{what}: the web answered the definition, the agent '{mcp.Text}'");
                    continue;
                }
                Compare(differences, what,
                    HrMcp.Unfence(mcp.Text)["attributes"]!.AsArray().Select(a => Shape(a!)).Order(StringComparer.Ordinal),
                    web!["attributes"]!.AsArray().Select(a => Shape(a!)).Order(StringComparer.Ordinal));
            }

            // What each caller should see of a person.
            var person = await AgentAsync(host, caller, "describe", new { type = "Person" });
            if (caller.Name == "restricted")
            {
                var names = HrMcp.Unfence(person.Text)["attributes"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()).ToList();
                names.Should().Contain("FirstName");
                names.Should().NotContain("Company", "a reference to a type the caller may not read is hidden (D19)");
                names.Should().NotContain("Email", "an attribute the caller may not read is absent");
            }
            else if (caller.Name is "manager" or "viewer")
            {
                new[] { "Company", "Email", "Jobs" }.Except(HrMcp.Unfence(person.Text)["attributes"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()))
                    .Should().BeEmpty($"{caller.Name} reads companies and e-mail addresses");
            }
        }

        differences.Should().BeEmpty();

        static string Shape(JsonNode attribute)
            => string.Join('|', attribute["name"], attribute["dataType"], attribute["referenceType"], attribute["asDetailType"],
                attribute["isArray"], attribute["isRequired"], attribute["isReadOnly"]);
    }

    // ── query ↔ POST /spark/queries/execute ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Query_returns_the_rows_columns_and_values_the_web_returns()
    {
        var (host, callers) = await StartAsync();
        await using var _ = host;
        var differences = new List<string>();
        var peopleChecked = 0;

        // The web's answer each caller must get, so a query that silently answers nobody (a misspelt address
        // answers 404 to all, alike) cannot pass for parity: the queries by their aliases, as discover lists them.
        var expected = new Dictionary<string, Dictionary<string, HttpStatusCode>>
        {
            ["manager"] = new() { ["people"] = HttpStatusCode.OK, ["companies"] = HttpStatusCode.OK, ["professions"] = HttpStatusCode.OK, ["cars"] = HttpStatusCode.OK },
            ["viewer"] = new() { ["people"] = HttpStatusCode.OK, ["companies"] = HttpStatusCode.OK, ["professions"] = HttpStatusCode.OK, ["cars"] = HttpStatusCode.OK },
            ["restricted"] = new() { ["people"] = HttpStatusCode.OK, ["companies"] = HttpStatusCode.NotFound, ["professions"] = HttpStatusCode.OK, ["cars"] = HttpStatusCode.NotFound },
            ["anonymous"] = new() { ["people"] = HttpStatusCode.Unauthorized, ["companies"] = HttpStatusCode.OK, ["professions"] = HttpStatusCode.OK, ["cars"] = HttpStatusCode.Unauthorized },
        };

        foreach (var caller in callers)
        {
            foreach (var query in new[] { "people", "companies", "professions", "cars", "company-people", "no-such-query" })
            {
                var mcp = await AgentAsync(host, caller, "query", new { query, take = 100 });
                var (status, web) = await caller.Web.JsonAsync(HttpMethod.Post, "/spark/queries/execute", new { queryId = query, take = 100 });
                var what = $"{caller.Name} query {query}";
                if (expected[caller.Name].TryGetValue(query, out var webStatus))
                    status.Should().Be(webStatus, $"{what} on the web");
                if (query == "company-people" && !caller.IsAnonymous)
                    status.Should().Be(HttpStatusCode.BadRequest, $"{caller.Name}: a sub-query without its parent is an incomplete request, not a server fault");

                // cars: HR's replicated cars are not exposed to agents (no agentPermissions).
                if (status != HttpStatusCode.OK || query == "cars")
                {
                    if (status == HttpStatusCode.BadRequest)
                    {
                        // Refused for the arguments, after authorization (a sub-query without its parent): the same words
                        // for both. HR's company-people answered the web with a 500 before (EnsureParent's exception).
                        var error = web?["error"]?.GetValue<string>();
                        if (!mcp.IsToolError || mcp.Text != error)
                            differences.Add($"{what}: the web answered 400 '{error}', the agent '{mcp.Text}'");
                        if (query == "company-people")
                            error.Should().Contain("requires a parent object", caller.Name);
                        continue;
                    }
                    ExpectRefusal(differences, what, caller, mcp, status, agentNarrowed: status == HttpStatusCode.OK);
                    continue;
                }

                if (mcp.IsToolError)
                {
                    differences.Add($"{what}: the web answered rows, the agent '{mcp.Text}'");
                    continue;
                }

                var page = HrMcp.Unfence(mcp.Text);
                Compare(differences, $"{what} totalItems", [page["totalItems"]!.ToJsonString()], [web!["totalItems"]!.ToJsonString()]);
                Compare(differences, $"{what} columns",
                    page["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()),
                    web["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
                var agentRows = page["rows"]!.AsArray().ToList();
                var webRows = web["items"]!.AsArray().ToList();
                Compare(differences, $"{what} row ids", agentRows.Select(r => r!["id"]!.GetValue<string>()), webRows.Select(r => r!["id"]!.GetValue<string>()));
                foreach (var (agentRow, webRow) in agentRows.Zip(webRows))
                {
                    Compare(differences, $"{what} row {webRow!["id"]} breadcrumb", [agentRow!["breadcrumb"]?.ToJsonString() ?? "null"], [webRow["breadcrumb"]?.ToJsonString() ?? "null"]);
                    var agentValues = agentRow["values"]!.AsObject();
                    var webValues = webRow["values"]!.AsArray().ToDictionary(v => v!["key"]!.GetValue<string>(), v => AsAgentValue(v!));
                    Compare(differences, $"{what} row {webRow["id"]} keys", agentValues.Select(v => v.Key).Order(StringComparer.Ordinal), webValues.Keys.Order(StringComparer.Ordinal));
                    foreach (var (key, value) in agentValues)
                    {
                        if (webValues.TryGetValue(key, out var webValue) && !JsonNode.DeepEquals(value, webValue))
                            differences.Add($"{what} row {webRow["id"]} {key}: agent {value?.ToJsonString()} web {webValue?.ToJsonString()}");
                    }
                }

                // What each caller should see of people.
                if (query == "people")
                {
                    peopleChecked++;
                    var avery = agentRows.Single(r => r!["id"]!.GetValue<string>() == "People/parity-1")!["values"]!.AsObject();
                    if (caller.Name == "restricted")
                    {
                        avery.Select(v => v.Key).Should().NotContain("Company", "a hidden reference is absent from rows (D19)");
                        avery.Select(v => v.Key).Should().NotContain("Email");
                        mcp.Text.Should().NotContain("Companies/parity-north", "nor does the hidden reference's target id leak");
                        mcp.Text.Should().NotContain("avery@example.test");
                    }
                    else
                    {
                        avery["Company"]!["id"]!.GetValue<string>().Should().Be("Companies/parity-north", caller.Name);
                    }
                }
            }
        }

        differences.Should().BeEmpty();
        peopleChecked.Should().Be(3, "the three users' people were compared row by row, not refused alike");
    }

    // ── get ↔ POST /spark/po/load ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_returns_the_object_the_web_loads()
    {
        var (host, callers) = await StartAsync();
        await using var _ = host;
        var differences = new List<string>();
        var objects = new[]
        {
            ("Person", "People/parity-1"), ("Person", "People/parity-2"), ("Person", "People/parity-3"),
            ("Company", "Companies/parity-north"), ("Profession", "Professions/parity-dev"),
            ("Person", "People/parity-missing"), ("Company", "People/parity-1"),
        };

        foreach (var caller in callers)
        {
            foreach (var (type, id) in objects)
            {
                var mcp = await AgentAsync(host, caller, "get", new { type, id, include = new[] { "Jobs" } });
                var (status, web) = await caller.Web.JsonAsync(HttpMethod.Post, "/spark/po/load", new { objectTypeId = type, id });
                var what = $"{caller.Name} get {type} {id}";

                if (status != HttpStatusCode.OK)
                {
                    ExpectRefusal(differences, what, caller, mcp, status, agentNarrowed: false);
                    continue;
                }
                if (mcp.IsToolError)
                {
                    differences.Add($"{what}: the web loaded it, the agent '{mcp.Text}'");
                    continue;
                }

                var agent = HrMcp.Unfence(mcp.Text);
                foreach (var member in new[] { "id", "breadcrumb", "etag" })
                    Compare(differences, $"{what} {member}", [agent[member]?.ToJsonString() ?? "null"], [web![member]?.ToJsonString() ?? "null"]);
                Compare(differences, $"{what} can", [agent["can"]?.ToJsonString() ?? "null"], [web!["can"]?.ToJsonString() ?? "null"]);

                var agentAttributes = agent["attributes"]!.AsObject();
                var webAttributes = web["attributes"]!.AsArray().ToDictionary(a => a!["name"]!.GetValue<string>(), a => a!);
                var webCompact = AsAgentAttributes(web!.AsObject(), include: "Jobs");
                Compare(differences, $"{what} attributes", agentAttributes.Select(a => a.Key).Order(StringComparer.Ordinal), webAttributes.Keys.Order(StringComparer.Ordinal));
                foreach (var (name, value) in agentAttributes)
                {
                    if (!webAttributes.ContainsKey(name))
                        continue;
                    if (!JsonNode.DeepEquals(value, webCompact[name]))
                        differences.Add($"{what} {name}: agent {value?.ToJsonString()} web {webCompact[name]?.ToJsonString()}");
                }

                if (id == "People/parity-1")
                {
                    agentAttributes["Address"]!["City"]!.GetValue<string>().Should().Be("Brussel", $"{caller.Name}: the person's embedded address, inline");
                    var jobs = agentAttributes["Jobs"]!;
                    jobs["count"]!.GetValue<int>().Should().Be(2, $"{caller.Name}: the contracts are the agent's to read (CarreerJob is exposed)");
                    jobs["rows"]!.AsArray().Select(r => r!["ProfessionId"]!["id"]!.GetValue<string>())
                        .Should().Equal(["Professions/parity-dev", "Professions/parity-consultant"], caller.Name);
                    if (caller.Name == "restricted")
                    {
                        agentAttributes.Select(a => a.Key).Should().NotContain("Company");
                        agentAttributes.Select(a => a.Key).Should().NotContain("Email");
                        mcp.Text.Should().NotContain("Companies/parity-north");
                    }
                    else
                    {
                        agentAttributes["Company"]!["id"]!.GetValue<string>().Should().Be("Companies/parity-north", caller.Name);
                        agentAttributes["Email"]!.GetValue<string>().Should().Be("avery@example.test", caller.Name);
                    }
                }
            }
        }

        differences.Should().BeEmpty();
    }

    // ── propose_changes / apply_changes ↔ POST /spark/po/update, /spark/po/delete ─────────────────────

    [Fact]
    public async Task Changes_are_allowed_refused_and_saved_as_the_web_allows_refuses_and_saves_them()
    {
        var (host, callers) = await StartAsync();
        await using var _ = host;
        var manager = callers.Single(c => c.Name == "manager");
        var viewer = callers.Single(c => c.Name == "viewer");
        var restricted = callers.Single(c => c.Name == "restricted");
        var anonymous = callers.Single(c => c.IsAnonymous);

        // A manager may edit people: the agent through a plan, the web through the form; both are saved.
        (await ApplyPlanAsync(host, manager, Update("People/parity-2", new { LastName = "Sample-agent" }))).Should().BeNull();
        (await WebUpdateAsync(manager, "People/parity-3", "LastName", "Placeholder-web")).Should().Be(HttpStatusCode.OK);
        (await LoadAsync("People/parity-2"))!.LastName.Should().Be("Sample-agent");
        (await LoadAsync("People/parity-3"))!.LastName.Should().Be("Placeholder-web");

        // A manager may not delete them (HR managers hold no Delete): refused on both, nothing deleted.
        (await ProposeAsync(host, manager, new { kind = "delete", type = "Person", id = "People/parity-3" })).Should().Be("Operation 1: Not found.");
        var etag = (await manager.Web.JsonAsync(HttpMethod.Post, "/spark/po/load", new { objectTypeId = "Person", id = "People/parity-3" })).Body!["etag"]!.GetValue<string>();
        var (deleteStatus, _) = await manager.Web.JsonAsync(HttpMethod.Post, "/spark/po/delete", new { objectTypeId = "Person", id = "People/parity-3", etag });
        ((int)deleteStatus).Should().BeGreaterThan(399, "the web refuses the delete too");
        (await LoadAsync("People/parity-3") is not null).Should().BeTrue("nothing was deleted");

        // A viewer may change nothing: refused on both, nothing saved.
        (await ProposeAsync(host, viewer, Update("People/parity-1", new { LastName = "Viewer" }))).Should().Be("Operation 1: Not found.");
        ((int)await WebUpdateAsync(viewer, "People/parity-1", "LastName", "Viewer")).Should().BeGreaterThan(399);
        (await LoadAsync("People/parity-1"))!.LastName.Should().Be("Example");

        // The restricted user may edit a person but sees neither the company nor the e-mail: the agent cannot name them
        // (a silent drop would make the shown diff false, M4); the web drops a posted value silently (D19). Both keep the
        // stored values.
        (await ProposeAsync(host, restricted, Update("People/parity-1", new { Company = "Companies/parity-south" })))
            .Should().Be("Operation 1: 'Company' is not an attribute you can set on Person.");
        (await ProposeAsync(host, restricted, Update("People/parity-1", new { Email = "changed@example.test" })))
            .Should().Be("Operation 1: 'Email' is not an attribute you can set on Person.");
        (await ApplyPlanAsync(host, restricted, Update("People/parity-1", new { FirstName = "Avery-agent" }))).Should().BeNull();
        (await WebUpdateAsync(restricted, "People/parity-2", "FirstName", "Blake-web", extra: ("Company", "Companies/parity-north"))).Should().Be(HttpStatusCode.OK);
        var averyStored = await LoadAsync("People/parity-1");
        var blakeStored = await LoadAsync("People/parity-2");
        averyStored!.FirstName.Should().Be("Avery-agent");
        averyStored!.Company.Should().Be("Companies/parity-north", "the agent's save keeps the hidden reference");
        averyStored!.Email.Should().Be("avery@example.test", "and the attribute the caller cannot read");
        blakeStored!.FirstName.Should().Be("Blake-web");
        blakeStored!.Company.Should().Be("Companies/parity-south", "the web's save keeps it too, whatever was posted");
        blakeStored!.Email.Should().Be("blake@example.test");

        // Anonymous callers change nothing: no write tools for them (D17), and the web refuses.
        var anonymousPropose = await AgentAsync(host, anonymous, "propose_changes", new { summary = "anonymous", operations = new[] { Update("People/parity-1", new { LastName = "Anon" }) } });
        anonymousPropose.Error.Should().Be("Unknown tool: 'hr_propose_changes'");
        ((int)await WebUpdateAsync(anonymous, "People/parity-1", "LastName", "Anon", loadAs: manager)).Should().BeGreaterThan(399);
        (await LoadAsync("People/parity-1"))!.LastName.Should().Be("Example");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static object Update(string id, object values) => new { kind = "update", type = "Person", id, values };

    /// <summary>Proposes one operation; answers the refusal text, or null with the plan left unapplied.</summary>
    private static async Task<string?> ProposeAsync(HrAgentHost host, Caller caller, object operation)
    {
        var reply = await AgentAsync(host, caller, "propose_changes", new { summary = "parity", operations = new[] { operation } });
        return reply.IsToolError ? reply.Text : reply.Error;
    }

    /// <summary>Proposes and applies one operation; answers null when it ran, else the refusal.</summary>
    private static async Task<string?> ApplyPlanAsync(HrAgentHost host, Caller caller, object operation)
    {
        var proposed = await AgentAsync(host, caller, "propose_changes", new { summary = "parity", operations = new[] { operation } });
        if (proposed.IsToolError || proposed.Error is not null)
            return proposed.IsToolError ? proposed.Text : proposed.Error;
        var plan = HrMcp.Unfence(proposed.Text);
        var applied = await AgentAsync(host, caller, "apply_changes", new { planId = plan["planId"]!.GetValue<string>(), changes = plan["changes"]!.AsArray() });
        return applied.IsToolError ? applied.Text : applied.Error;
    }

    /// <summary>
    /// The web's edit: the object as <c>po/load</c> presents it (to <paramref name="loadAs"/>, else the caller), one value
    /// changed and an optional extra one posted, then <c>po/update</c>.
    /// </summary>
    private static async Task<HttpStatusCode> WebUpdateAsync(Caller caller, string id, string attribute, string value, (string Name, string Value)? extra = null, Caller? loadAs = null)
    {
        var (loaded, po) = await (loadAs ?? caller).Web.JsonAsync(HttpMethod.Post, "/spark/po/load", new { objectTypeId = "Person", id });
        loaded.Should().Be(HttpStatusCode.OK);
        var attributes = po!["attributes"]!.AsArray();
        Set(attributes, attribute, value);
        if (extra is { } posted)
        {
            if (!attributes.Any(a => a!["name"]!.GetValue<string>() == posted.Name))
                attributes.Add(new JsonObject { ["name"] = posted.Name, ["value"] = posted.Value, ["isValueChanged"] = true });
            else
                Set(attributes, posted.Name, posted.Value);
        }
        var (status, _) = await caller.Web.JsonAsync(HttpMethod.Post, "/spark/po/update", new { objectTypeId = "Person", id, persistentObject = po });
        return status;

        static void Set(JsonArray attributes, string name, string value)
        {
            var target = attributes.Single(a => a!["name"]!.GetValue<string>() == name)!.AsObject();
            target["value"] = value;
            target["isValueChanged"] = true;
        }
    }

    /// <summary>The stored person, read past every right: what was really saved.</summary>
    private async Task<HR.Entities.Person?> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<HR.Entities.Person>(id);
    }

    /// <summary>
    /// A refusal on the web (<paramref name="webStatus"/>) or by the agent layers (<paramref name="agentNarrowed"/>: the web
    /// answered, the type is not exposed to agents) must be the agent's uniform refusal: the sign-in text for an anonymous
    /// caller the web answers 401, else "Not found." (D17).
    /// </summary>
    private static void ExpectRefusal(List<string> differences, string what, Caller caller, HrMcp.Reply mcp, HttpStatusCode webStatus, bool agentNarrowed)
    {
        if (!mcp.IsToolError)
        {
            differences.Add($"{what}: the web answered {(int)webStatus}{(agentNarrowed ? " (type not exposed to agents)" : "")}, the agent answered data");
            return;
        }
        var signIn = caller.IsAnonymous && webStatus == HttpStatusCode.Unauthorized;
        if (signIn ? mcp.Text == "Not found." : mcp.Text != "Not found.")
            differences.Add($"{what}: the web answered {(int)webStatus}, the agent '{mcp.Text}'");
        if (!agentNarrowed && webStatus is not (HttpStatusCode.NotFound or HttpStatusCode.Unauthorized))
            differences.Add($"{what}: the web refused with {(int)webStatus}, not the uniform 404/401");
    }

    /// <summary>
    /// A <c>po/load</c> object's attributes as <c>get</c> compacts them: a reference as its id and breadcrumb, a detail
    /// object inline, detail rows (from <c>objects</c>, where the web carries them) as their count, with the rows for the
    /// attribute named in <paramref name="include"/>.
    /// </summary>
    private static JsonObject AsAgentAttributes(JsonObject po, string? include, JsonObject? into = null)
    {
        var attributes = into ?? new JsonObject();
        foreach (var attribute in po["attributes"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var name = attribute["name"]!.GetValue<string>();
            if (attribute["objects"] is JsonArray rows)
            {
                attributes[name] = name == include
                    ? new JsonObject
                    {
                        ["count"] = rows.Count,
                        ["rows"] = new JsonArray([.. rows.OfType<JsonObject>().Take(100).Select(r =>
                            (JsonNode)AsAgentAttributes(r, null, r["id"] is JsonValue id ? new JsonObject { ["id"] = id.DeepClone() } : new JsonObject()))]),
                        ["omitted"] = Math.Max(0, rows.Count - 100),
                    }
                    : new JsonObject { ["count"] = rows.Count };
            }
            else if (attribute["object"] is JsonObject detail)
            {
                attributes[name] = AsAgentAttributes(detail, null);
            }
            else
            {
                attributes[name] = attribute["breadcrumb"] is JsonValue breadcrumb && attribute["value"] is JsonValue
                    ? new JsonObject { ["id"] = attribute["value"]!.DeepClone(), ["breadcrumb"] = breadcrumb.DeepClone() }
                    : attribute["value"]?.DeepClone();
            }
        }
        return attributes;
    }

    /// <summary>A web query value as the agent's table carries it: a reference as its id and breadcrumb.</summary>
    private static JsonNode? AsAgentValue(JsonNode value)
        => value["objectId"] is JsonValue objectId
            ? new JsonObject { ["id"] = objectId.DeepClone(), ["breadcrumb"] = (value["breadcrumb"] ?? value["value"])?.DeepClone() }
            : value["value"]?.DeepClone();

    private static void Compare(List<string> differences, string what, IEnumerable<string> agent, IEnumerable<string> web)
    {
        var a = agent.ToList();
        var w = web.ToList();
        if (!a.SequenceEqual(w, StringComparer.Ordinal))
            differences.Add($"{what}: agent [{string.Join(", ", a)}] web [{string.Join(", ", w)}]");
    }
}
