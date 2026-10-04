using MintPlayer.Spark.Tests._Infrastructure;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Endpoints.Actions;

using Po = Abstractions.PersistentObject;

/// <summary>
/// #460 D13 — <c>OnDisableActionsAsync</c> is the single source of truth for disabled actions, asked
/// at load and at submit, against the real route table. Also the measurements behind spikes
/// <b>S6</b> (load + submit evaluation, no operation leakage, 403 only after the row gate),
/// <b>S-MOD-F</b> (404 / 400 / 429 / 403 distinguishable through the envelope) and <b>S7</b> (a
/// custom action's result through the envelope and a 449 retry, in <see cref="SparkClient"/>).
/// </summary>
/// <remarks>
/// The fixture's names start with <c>DisProbe</c> for the reason given on <c>RetryFromEveryHookTests</c>:
/// <c>ActionsResolver</c> matches actions classes by simple name across the whole assembly.
/// </remarks>
public class DisableActionsTests : SparkTestDriver
{
    private static readonly Guid ProbeTypeId = Guid.Parse("d13d0000-0000-4000-8000-d13d00000001");
    private static readonly Guid ProbesQueryId = Guid.Parse("d13d0000-0000-4000-8000-d13d00000002");
    private static readonly Guid FrozenQueryId = Guid.Parse("d13d0000-0000-4000-8000-d13d00000003");

    private const string Run = "DisProbeRun";
    private const string Export = "DisProbeExport";
    private const string Touch = "DisProbeTouch";

    public class DisProbe
    {
        public string? Id { get; set; }
        public string Reference { get; set; } = "";
        public string State { get; set; } = "Open";
    }

    private sealed class DisProbeContext : SparkContext
    {
        public Raven.Client.Documents.Linq.IRavenQueryable<DisProbe> Probes => Session.Query<DisProbe>();
    }

    /// <summary>What the hook and the actions saw, so a test can assert calls rather than infer them.</summary>
    public sealed class DisProbeRecorder
    {
        public ConcurrentQueue<DisableActionsContext> Contexts { get; } = new();
        public ConcurrentQueue<int> Batches { get; } = new();
        public ConcurrentQueue<string> Executed { get; } = new();
        public bool DisableNew { get; set; }
    }

    /// <summary>
    /// Locked rows withhold Edit, Delete and the custom action; the "frozen" query withholds the
    /// custom action; <see cref="DisProbeRecorder.DisableNew"/> withholds New everywhere. A row
    /// referenced "hidden" is invisible to the row rule, on every action.
    /// </summary>
    public class DisProbeActions(IEntityMapper mapper, DisProbeRecorder recorder)
        : DefaultPersistentObjectActions<DisProbe>(mapper)
    {
        public override Task<bool> IsAllowedAsync(string action, DisProbe entity)
            => Task.FromResult(entity.Reference != "hidden");

        public override async Task OnDisableActionsAsync(IReadOnlyList<DisableActionsItem> items)
        {
            recorder.Batches.Enqueue(items.Count);
            await base.OnDisableActionsAsync(items);
        }

        public override Task OnDisableActionsAsync(IDisablable target, DisableActionsContext context)
        {
            recorder.Contexts.Enqueue(context);

            if (context.TargetKind == DisableActionsTargetKind.PersistentObject
                && context.Entity is DisProbe { State: "Locked" })
            {
                target.DisableActions("Edit", "Delete", Run);
            }

            if (context.TargetKind == DisableActionsTargetKind.Query && context.Query?.Name == "DisProbesFrozen")
                target.DisableActions(Run);

            if (recorder.DisableNew)
                target.DisableActions("New");

            return Task.CompletedTask;
        }
    }

    /// <summary>Runs, and returns what it ran on (T5).</summary>
    public sealed class DisProbeRunAction(DisProbeRecorder recorder) : ICustomAction
    {
        public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        {
            recorder.Executed.Enqueue(Run);
            args.SetResult(new DisProbeRunResult(args.Parent?.Id, args.SelectedItems.Length));
            return Task.CompletedTask;
        }
    }

    public sealed record DisProbeRunResult(string? Parent, int Rows);

    /// <summary>Saves the selected row through <see cref="IDatabaseAccess"/> — the gated write path.</summary>
    public sealed class DisProbeTouchAction(IDatabaseAccess databaseAccess) : ICustomAction
    {
        public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        {
            var po = await databaseAccess.GetPersistentObjectAsync(ProbeTypeId, args.SelectedItems[0].Id!);
            po!["Reference"].SetValue("touched");
            await databaseAccess.SavePersistentObjectAsync(po);
        }
    }

    public sealed record DisProbeExportResult(string JobId, string Answer);

    /// <summary>Prompts once, then returns a value built from the answer (S7).</summary>
    public sealed class DisProbeExportAction(DisProbeRecorder recorder, IRetryAccessor retry) : ICustomAction
    {
        public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        {
            if (retry.Result is null)
            {
                // A result set before the prompt must not leak onto the 449.
                args.SetResult(new DisProbeExportResult("never", "never"));
                retry.Action("Export?", ["Yes", "No"], defaultOption: "No", message: "Confirm?");
            }

            recorder.Executed.Enqueue(Export);
            args.SetResult(new DisProbeExportResult("job-1", retry.Result!.Option));
            return Task.CompletedTask;
        }
    }

    private SparkEndpointFactory<DisProbeContext> _factory = null!;
    private HttpClient _client = null!;
    private string _cookieHeader = null!;
    private string _xsrfToken = null!;

    private DisProbeRecorder Recorder => _factory.GetService<DisProbeRecorder>();

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = NewFactory();
        _client = _factory.CreateClient();
        (_cookieHeader, _xsrfToken) = await _factory.MintAntiforgeryAsync();
    }

    public override async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private SparkEndpointFactory<DisProbeContext> NewFactory(Action<MintPlayer.Spark.Abstractions.Builder.ISparkBuilder>? configureSpark = null)
        => new(
            Store,
            [ProbeModel()],
            configureServices: services =>
            {
                services.AddSingleton<DisProbeRecorder>();
                services.AddScoped<DisProbeActions>();
                services.AddScoped<DisProbeRunAction>();
                services.AddScoped<DisProbeExportAction>();
                services.AddScoped<DisProbeTouchAction>();
                services.AddSingleton(TestActions.LoaderWithCustom(Run, Export, Touch));
                services.AddScoped<ICustomActionResolver>(sp => new StubActionResolver(new Dictionary<string, ICustomAction>
                {
                    [Run] = sp.GetRequiredService<DisProbeRunAction>(),
                    [Export] = sp.GetRequiredService<DisProbeExportAction>(),
                    [Touch] = sp.GetRequiredService<DisProbeTouchAction>(),
                }));
            },
            configureSpark: configureSpark,
            security: SparkTestSecurity.Permissive);

    // ---- S6: load ---------------------------------------------------------------------------------

    [Fact]
    public async Task Load_returns_the_hooks_answer_on_the_object()
    {
        var locked = await SeedAsync("L-1", "Locked");
        var open = await SeedAsync("O-1", "Open");

        var (lockedStatus, lockedBody) = await SendAsync("/spark/po/load", Wire.Typed(ProbeTypeId, id: locked.Id));
        var (openStatus, openBody) = await SendAsync("/spark/po/load", Wire.Typed(ProbeTypeId, id: open.Id));

        lockedStatus.Should().Be(HttpStatusCode.OK);
        Names(lockedBody.GetProperty("disabledActions")).Should().Equal("Edit", "Delete", Run);
        openStatus.Should().Be(HttpStatusCode.OK);
        openBody.TryGetProperty("disabledActions", out _).Should().BeFalse("nothing was withheld, and the field is omitted when null");

        var seen = Recorder.Contexts.First(c => c.Id == locked.Id);
        seen.Phase.Should().Be(DisableActionsPhase.Load);
        seen.TargetKind.Should().Be(DisableActionsTargetKind.PersistentObject);
        seen.ActionName.Should().BeNull();
        seen.Entity.Should().BeOfType<DisProbe>();
    }

    [Fact]
    public async Task Query_execute_returns_the_hooks_answer_and_no_operations()
    {
        await SeedAsync("O-1", "Open");

        var (frozenStatus, frozen) = await SendAsync("/spark/queries/execute", Wire.Query(FrozenQueryId));
        var (plainStatus, plain) = await SendAsync("/spark/queries/execute", Wire.Query(ProbesQueryId));

        frozenStatus.Should().Be(HttpStatusCode.OK);
        Names(frozen.GetProperty("disabledActions")).Should().Equal(Run);
        frozen.TryGetProperty("operations", out _).Should().BeFalse(
            "a query result is a bare body; the answer travels on disabledActions, never as a client operation");
        plainStatus.Should().Be(HttpStatusCode.OK);
        plain.GetProperty("disabledActions").ValueKind.Should().Be(JsonValueKind.Null);

        var seen = Recorder.Contexts.First(c => c.Query?.Name == "DisProbesFrozen");
        seen.Phase.Should().Be(DisableActionsPhase.Load);
        seen.TargetKind.Should().Be(DisableActionsTargetKind.Query);
        seen.Entity.Should().BeNull("a query target is judged before any row exists");
    }

    // ---- S6: submit, built-in actions ------------------------------------------------------------

    [Fact]
    public async Task Update_of_a_row_that_withholds_Edit_is_403_and_writes_nothing()
    {
        var locked = await SeedAsync("L-1", "Locked");

        var (status, body) = await SendAsync("/spark/po/update", UpdateBody(locked.Id!, await EtagAsync(locked.Id!), "changed"));

        status.Should().Be(HttpStatusCode.Forbidden);
        body.GetProperty("result").GetProperty("action").GetString().Should().Be("Edit");
        (await LoadAsync(locked.Id!))!.Reference.Should().Be("L-1");

        var seen = Recorder.Contexts.Last(c => c.Phase == DisableActionsPhase.Submit);
        seen.ActionName.Should().Be("Edit");
        seen.Entity.Should().BeOfType<DisProbe>().Which.Reference.Should().Be("L-1", "the hook judges the STORED entity, not the posted values");
    }

    [Fact]
    public async Task Update_of_a_row_that_withholds_nothing_goes_through()
    {
        var open = await SeedAsync("O-1", "Open");

        var (status, _) = await SendAsync("/spark/po/update", UpdateBody(open.Id!, await EtagAsync(open.Id!), "changed"));

        status.Should().Be(HttpStatusCode.OK);
        (await LoadAsync(open.Id!))!.Reference.Should().Be("changed");
    }

    [Fact]
    public async Task Delete_of_a_row_that_withholds_Delete_is_403_and_deletes_nothing()
    {
        var locked = await SeedAsync("L-1", "Locked");

        var (status, body) = await SendAsync("/spark/po/delete", Wire.Typed(ProbeTypeId, id: locked.Id, etag: await EtagAsync(locked.Id!)));

        status.Should().Be(HttpStatusCode.Forbidden);
        body.GetProperty("result").GetProperty("action").GetString().Should().Be("Delete");
        (await LoadAsync(locked.Id!)).Should().NotBeNull();
    }

    [Fact]
    public async Task Create_while_New_is_withheld_is_403_and_creates_nothing()
    {
        Recorder.DisableNew = true;

        var (status, body) = await SendAsync("/spark/po/create", Wire.Typed(ProbeTypeId, new
        {
            persistentObject = new
            {
                name = "DisProbe",
                objectTypeId = ProbeTypeId.ToString(),
                attributes = new[] { new { name = "Reference", value = "N-1", isValueChanged = true } },
            },
        }));

        status.Should().Be(HttpStatusCode.Forbidden);
        body.GetProperty("result").GetProperty("action").GetString().Should().Be("New");
        (await CountAsync()).Should().Be(0);

        var seen = Recorder.Contexts.Last(c => c.Phase == DisableActionsPhase.Submit);
        seen.ActionName.Should().Be("New");
        seen.Entity.Should().BeNull("there is no stored entity for a create");
    }

    /// <summary>
    /// The #453 lesson: a row the caller may not see is never confirmed, whatever the hook would say about
    /// it — the disabled-action gate runs only after the row gate passed, so a 403 never confirms a row.
    /// An update answers 409 <c>deleted</c> (#467, D30c), exactly as for a row that is gone.
    /// </summary>
    [Fact]
    public async Task A_hidden_row_is_never_confirmed_even_when_it_would_withhold_the_action()
    {
        var hidden = await SeedAsync("hidden", "Locked");

        var (updateStatus, _) = await SendAsync("/spark/po/update", UpdateBody(hidden.Id!, await EtagAsync(hidden.Id!), "changed"));
        var (deleteStatus, _) = await SendAsync("/spark/po/delete", Wire.Typed(ProbeTypeId, id: hidden.Id, etag: await EtagAsync(hidden.Id!)));
        var (actionStatus, _) = await SendAsync("/spark/actions/execute", Wire.Action(ProbeTypeId, Run, new
        {
            selectedItemIds = new[] { hidden.Id },
            queryId = ProbesQueryId.ToString(),
        }));

        updateStatus.Should().Be(HttpStatusCode.Conflict);
        deleteStatus.Should().Be(HttpStatusCode.NotFound);
        actionStatus.Should().Be(HttpStatusCode.NotFound);
        Recorder.Contexts.Should().NotContain(c => c.Phase == DisableActionsPhase.Submit && c.Id == hidden.Id);
    }

    // ---- S6: submit, custom actions (parent, query, rows — union) --------------------------------

    [Fact]
    public async Task A_custom_action_on_a_parent_that_withholds_it_is_403_and_does_not_run()
    {
        var locked = await SeedAsync("L-1", "Locked");

        var (status, body) = await SendAsync("/spark/actions/execute", Wire.Action(ProbeTypeId, Run, new
        {
            parent = ParentBody(locked.Id!),
        }));

        status.Should().Be(HttpStatusCode.Forbidden);
        body.GetProperty("result").GetProperty("action").GetString().Should().Be(Run);
        Recorder.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_custom_action_from_a_query_that_withholds_it_is_403()
    {
        var open = await SeedAsync("O-1", "Open");

        var (status, _) = await SendAsync("/spark/actions/execute", Wire.Action(ProbeTypeId, Run, new
        {
            selectedItemIds = new[] { open.Id },
            queryId = FrozenQueryId.ToString(),
        }));

        status.Should().Be(HttpStatusCode.Forbidden, "the query target withholds the action even though the row does not");
        Recorder.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task One_selected_row_that_withholds_the_action_refuses_the_whole_selection()
    {
        var open = await SeedAsync("O-1", "Open");
        var locked = await SeedAsync("L-1", "Locked");

        var (status, _) = await SendAsync("/spark/actions/execute", Wire.Action(ProbeTypeId, Run, new
        {
            selectedItemIds = new[] { open.Id, locked.Id },
            queryId = ProbesQueryId.ToString(),
        }));

        status.Should().Be(HttpStatusCode.Forbidden);
        Recorder.Executed.Should().BeEmpty();
    }

    /// <summary>
    /// The batched form is called once per submit with every target, and the success envelope carries
    /// the action's result and nothing the re-executed query or the hook produced (S6: no leakage).
    /// </summary>
    [Fact]
    public async Task An_enabled_custom_action_runs_once_with_one_batched_evaluation_and_returns_its_result()
    {
        var parent = await SeedAsync("P-1", "Open");
        var a = await SeedAsync("O-1", "Open");
        var b = await SeedAsync("O-2", "Open");

        var batchesBefore = Recorder.Batches.Count;
        var (status, body) = await SendAsync("/spark/actions/execute", Wire.Action(ProbeTypeId, Run, new
        {
            parent = ParentBody(parent.Id!),
            selectedItemIds = new[] { a.Id, b.Id },
            queryId = ProbesQueryId.ToString(),
        }));

        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("operations").GetArrayLength().Should().Be(0);
        var result = body.GetProperty("result");
        result.GetProperty("parent").GetString().Should().Be(parent.Id);
        result.GetProperty("rows").GetInt32().Should().Be(2);
        result.TryGetProperty("disabledActions", out _).Should().BeFalse();
        Recorder.Executed.Should().Equal(Run);

        // One load-phase batch for the re-executed query, then ONE submit batch: parent + query + 2 rows.
        Recorder.Batches.Skip(batchesBefore).Should().Equal(1, 4);
        var submit = Recorder.Contexts.Where(c => c.Phase == DisableActionsPhase.Submit).ToList();
        submit.Should().HaveCount(4);
        submit.Should().OnlyContain(c => c.ActionName == Run);
        submit.Count(c => c.TargetKind == DisableActionsTargetKind.Query).Should().Be(1);
        submit.Where(c => c.TargetKind == DisableActionsTargetKind.PersistentObject)
            .Should().OnlyContain(c => c.Entity is DisProbe);
    }

    /// <summary>
    /// The action itself is enabled (the hook withholds DisProbeRun, not DisProbeTouch), but the save
    /// it makes through IDatabaseAccess is an Edit the row withholds: a 403 naming Edit, not a 500.
    /// </summary>
    [Fact]
    public async Task A_write_an_action_makes_through_IDatabaseAccess_is_gated_too()
    {
        var locked = await SeedAsync("L-1", "Locked");

        var (status, body) = await SendAsync("/spark/actions/execute", Wire.Action(ProbeTypeId, Touch, new
        {
            selectedItemIds = new[] { locked.Id },
            queryId = ProbesQueryId.ToString(),
        }));

        status.Should().Be(HttpStatusCode.Forbidden);
        body.GetProperty("result").GetProperty("action").GetString().Should().Be("Edit");
        (await LoadAsync(locked.Id!))!.Reference.Should().Be("L-1");
    }

    // ---- S-MOD-F: 404 / 400 / 403 / 429, told apart by the client ---------------------------------

    [Fact]
    public async Task S_MOD_F_refusals_are_distinguishable_through_the_envelope_and_SparkClient()
    {
        var locked = await SeedAsync("L-1", "Locked");
        var hidden = await SeedAsync("hidden", "Open");

        var (unknown, unknownBody) = await SendAsync("/spark/actions/execute", Wire.Action(ProbeTypeId, "DisProbeNope"));
        var (missing, missingBody) = await SendAsync("/spark/actions/execute", Wire.Action(ProbeTypeId, Run, new
        {
            selectedItemIds = new[] { hidden.Id }, queryId = ProbesQueryId.ToString(),
        }));
        var (tooMany, tooManyBody) = await SendAsync("/spark/actions/execute", Wire.Action(ProbeTypeId, Run, new
        {
            selectedItemIds = Enumerable.Range(0, 201).Select(i => $"DisProbes/{i}").ToArray(), queryId = ProbesQueryId.ToString(),
        }));
        var (disabled, disabledBody) = await SendAsync("/spark/actions/execute", Wire.Action(ProbeTypeId, Run, new
        {
            parent = ParentBody(locked.Id!),
        }));

        unknown.Should().Be(HttpStatusCode.NotFound);
        unknownBody.GetProperty("result").GetProperty("error").GetString().Should().Contain("not found");
        missing.Should().Be(HttpStatusCode.NotFound);
        missingBody.GetProperty("result").GetProperty("error").GetString().Should().Be("Not found");
        tooMany.Should().Be(HttpStatusCode.BadRequest);
        tooManyBody.GetProperty("result").GetProperty("error").GetString().Should().Contain("At most 200");
        disabled.Should().Be(HttpStatusCode.Forbidden);
        disabledBody.GetProperty("result").GetProperty("action").GetString().Should().Be(Run);
        foreach (var body in new[] { unknownBody, missingBody, tooManyBody, disabledBody })
            body.GetProperty("operations").ValueKind.Should().Be(JsonValueKind.Array, "every refusal is an envelope");

        using var client = new SparkClient(_factory.CreateClient(), ownsClient: true);
        var parent = new Po { Id = locked.Id, Name = "DisProbe", ObjectTypeId = ProbeTypeId };

        (await StatusOfAsync(() => client.ExecuteActionAsync(ProbeTypeId, "DisProbeNope"))).Should().Be(HttpStatusCode.NotFound);
        (await StatusOfAsync(() => client.ExecuteActionAsync(ProbeTypeId, Run,
            selectedItemIds: [hidden.Id!], queryId: ProbesQueryId.ToString()))).Should().Be(HttpStatusCode.NotFound);
        (await StatusOfAsync(() => client.ExecuteActionAsync(ProbeTypeId, Run,
            selectedItemIds: [.. Enumerable.Range(0, 201).Select(i => $"DisProbes/{i}")], queryId: ProbesQueryId.ToString()))).Should().Be(HttpStatusCode.BadRequest);

        var forbidden = await Assert.ThrowsAsync<SparkClientException>(() => client.ExecuteActionAsync(ProbeTypeId, Run, parent));
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        forbidden.ResponseBody.Should().Contain(Run);
    }

    [Fact]
    public async Task S_MOD_F_a_throttled_request_is_429_from_the_rate_limiter()
    {
        await using var throttled = NewFactory(spark => spark.AddRateLimiter(o =>
        {
            o.PermitLimit = 3;
            o.Window = TimeSpan.FromMinutes(5);
        }));
        using var http = throttled.CreateClient();

        HttpResponseMessage? rejected = null;
        for (var i = 0; i < 10 && rejected is null; i++)
        {
            var response = await http.PostAsJsonAsync("/spark/actions/execute", Wire.Action(ProbeTypeId, "DisProbeNope"));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                rejected = response;
        }

        rejected.Should().NotBeNull("the limiter admits 3 requests per window");
        (await rejected!.Content.ReadAsStringAsync()).Should().BeEmpty(
            "the limiter answers before any endpoint runs, so a 429 carries no envelope — the status alone says it");

        using var client = new SparkClient(throttled.CreateClient(), ownsClient: true);
        (await StatusOfAsync(() => client.ExecuteActionAsync(ProbeTypeId, "DisProbeNope"))).Should().Be(HttpStatusCode.TooManyRequests);
    }

    // ---- S7: a custom action's result through the envelope and a 449 retry -----------------------

    [Fact]
    public async Task S7_the_result_survives_a_449_through_the_raw_envelope()
    {
        var (first, prompt) = await SendAsync("/spark/actions/execute", Wire.Action(ProbeTypeId, Export));

        ((int)first).Should().Be(449);
        prompt.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null, "a prompt carries no result, even one set before it");

        var answered = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            Wire.Action(ProbeTypeId, Export).ToJsonString())!;
        answered["retryResults"] = new[] { new { step = 0, option = "Yes" } };
        var (second, body) = await SendAsync("/spark/actions/execute", answered);

        second.Should().Be(HttpStatusCode.OK);
        body.GetProperty("result").GetProperty("jobId").GetString().Should().Be("job-1");
        body.GetProperty("result").GetProperty("answer").GetString().Should().Be("Yes");
    }

    [Fact]
    public async Task S7_SparkClient_reads_the_result_with_a_retry_handler_and_through_ContinueAsync()
    {
        using var client = new SparkClient(_factory.CreateClient(), ownsClient: true);

        var handled = await client.ExecuteActionAsync(ProbeTypeId, Export,
            onRetry: (prompt, _) => Task.FromResult<RetryAnswer?>(RetryAnswer.Choose("Yes")));
        handled.IsRetry.Should().BeFalse();
        handled.GetResult<DisProbeExportResult>().Should().Be(new DisProbeExportResult("job-1", "Yes"));

        var asked = await client.ExecuteActionAsync(ProbeTypeId, Export);
        asked.IsRetry.Should().BeTrue();
        asked.Result.Should().BeNull();

        var continued = await client.ContinueAsync(asked, "No");
        continued.GetResult<DisProbeExportResult>().Should().Be(new DisProbeExportResult("job-1", "No"));

        var plain = await client.ExecuteActionAsync(ProbeTypeId, Run);
        plain.GetResult<DisProbeRunResult>().Should().Be(new DisProbeRunResult(null, 0));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static async Task<HttpStatusCode> StatusOfAsync(Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<SparkClientException>(call);
        return ex.StatusCode;
    }

    private static IEnumerable<string?> Names(JsonElement array) => array.EnumerateArray().Select(e => e.GetString());

    private static object ParentBody(string id) => new
    {
        id,
        name = "DisProbe",
        objectTypeId = ProbeTypeId.ToString(),
        attributes = Array.Empty<object>(),
    };

    private static object UpdateBody(string id, string etag, string reference) => Wire.Typed(ProbeTypeId, new
    {
        persistentObject = new
        {
            id,
            etag,
            name = "DisProbe",
            objectTypeId = ProbeTypeId.ToString(),
            attributes = new[] { new { name = "Reference", value = reference, isValueChanged = true } },
        },
    }, id);

    private Task<string> EtagAsync(string id) => StoredEtag.OfAsync(Store, id);

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(string url, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Cookie", _cookieHeader);
        request.Headers.Add("X-XSRF-TOKEN", _xsrfToken);

        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        return (response.StatusCode,
            string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<DisProbe> SeedAsync(string reference, string state)
    {
        var probe = new DisProbe { Reference = reference, State = state };
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(probe);
        await session.SaveChangesAsync();
        return probe;
    }

    private async Task<DisProbe?> LoadAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.LoadAsync<DisProbe>(id);
    }

    private async Task<int> CountAsync()
    {
        using var session = Store.OpenAsyncSession();
        return await Raven.Client.Documents.LinqExtensions.CountAsync(
            session.Query<DisProbe>().Customize(c => c.WaitForNonStaleResults()));
    }

    private sealed class StubActionResolver(IReadOnlyDictionary<string, ICustomAction> actions) : ICustomActionResolver
    {
        public ICustomAction? Resolve(string name)
            => actions.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

        public IReadOnlyList<string> GetRegisteredActionNames() => [.. actions.Keys];
    }

    private static EntityTypeFile ProbeModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ProbeTypeId,
            Name = "DisProbe",
            ClrType = typeof(DisProbe).FullName!,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Reference", DataType = "string", IsVisible = true },
                new() { Id = Guid.NewGuid(), Name = "State", DataType = "string", IsVisible = true },
            ],
        },
        Queries =
        [
            new SparkQuery { Id = ProbesQueryId, Name = "DisProbes", Source = "Database.Probes", EntityType = "DisProbe" },
            new SparkQuery { Id = FrozenQueryId, Name = "DisProbesFrozen", Source = "Database.Probes", EntityType = "DisProbe" },
        ],
    };
}
