using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Model;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Testing;

using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

using Po = Abstractions.PersistentObject;

/// <summary>
/// A retry raised from every hook that can prompt, against the real route table.
/// </summary>
/// <remarks>
/// <para>
/// A retry has two independent halves: <b>emit</b> — the endpoint catches
/// <c>SparkRetryActionException</c> and answers 449 — and <b>accept</b> — its request type binds
/// <c>RetryResults</c> and feeds <c>RetryAccessor.AnsweredResults</c>. Both are copy-pasted per
/// endpoint with nothing in the type system connecting them, so an endpoint can ship with one half
/// and nobody notices until a hook finally calls <c>Retry.Action(...)</c>.
/// </para>
/// <para>
/// Measured 2026-09-12, before M1: create, update, delete and custom action had both halves.
/// <b>Refresh had accept and not emit. New and delete-row had neither. Load and query could not have
/// either</b>, being behind a <c>GET</c> with no body to carry an answer. All nine work now, and these
/// rows are what stop the next endpoint being added half-wired.
/// </para>
/// <para>
/// ⚠️ The hooks here raise a retry <b>only on the first pass</b> — they check
/// <see cref="IRetryAccessor.Result"/> first. A hook that raises unconditionally makes an endpoint
/// that answers 449 forever, and the Angular client resubmits with no depth limit
/// (<c>spark.service.ts:375-376</c>). That is a real trap for anyone writing one of these hooks,
/// which is why the fixtures model the correct shape rather than the shortest one.
/// </para>
/// <para>
/// ⚠️ The entities are named <c>RetryProbe</c> rather than something natural like <c>Order</c> on
/// purpose. <c>ActionsResolver</c> matches an actions class to an entity by <b>simple name across the
/// whole assembly</b>, and being a nested type does not scope that lookup — so a nested
/// <c>OrderActions</c> here claimed to be the actions class for a <i>different</i> fixture's nested
/// <c>Order</c> and broke seven of its tests. Give fixture entities a name no other fixture would pick.
/// </para>
/// </remarks>
public class RetryFromEveryHookTests : SparkTestDriver
{
    private static readonly Guid ProbeTypeId = Guid.Parse("7b2d0000-0000-4000-8000-7b2d00000001");
    private static readonly Guid LineTypeId = Guid.Parse("7b2d0000-0000-4000-8000-7b2d00000002");
    private static readonly Guid ReadTypeId = Guid.Parse("7b2d0000-0000-4000-8000-7b2d00000003");

    public class RetryProbe
    {
        public string? Id { get; set; }
        public string Reference { get; set; } = "";
        public List<RetryProbeLine> Lines { get; set; } = [];
    }

    public class RetryProbeLine
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Description { get; set; } = "";
    }

    private class RetryProbeContext : SparkContext
    {
        public Raven.Client.Documents.Linq.IRavenQueryable<RetryProbe> Orders => Session.Query<RetryProbe>();
        public Raven.Client.Documents.Linq.IRavenQueryable<RetryProbeRead> Reads => Session.Query<RetryProbeRead>();
    }

    static RetryFromEveryHookTests()
        => SparkValueObjects.Register(typeof(RetryProbeLine), "Id", row => ((RetryProbeLine)row).Id.ToString());

    /// <summary>Raises one retry from every hook it overrides, once each.</summary>
    public class RetryProbeActions : DefaultPersistentObjectActions<RetryProbe>, ISparkOwnsRowSecurity
    {
        private readonly IRetryAccessor retry;
        public RetryProbeActions(IEntityMapper mapper, IRetryAccessor retry) : base(mapper) => this.retry = retry;

        public string RowSecurityRationale => "Test fixture; every probe is created by the test that reads it.";

        private void PromptOnce(string title)
        {
            // Ask only until answered. Re-raising unconditionally is an infinite loop, not a test.
            if (retry.Result is null)
                retry.Action(title, ["Yes", "No"], defaultOption: "No", message: "Confirm?");
        }

        public override Task OnBeforeSaveAsync(Po obj, RetryProbe entity)
        {
            PromptOnce("Save?");
            return Task.CompletedTask;
        }

        public override Task OnBeforeDeleteAsync(RetryProbe entity)
        {
            PromptOnce("Delete?");
            return Task.CompletedTask;
        }

        public override Task OnRefreshAsync(SparkRefreshArgs<RetryProbe> args)
        {
            PromptOnce("Refresh?");
            return Task.CompletedTask;
        }

    }

    /// <summary>A second entity, so the load prompt reaches only the load test.</summary>
    /// <remarks>
    /// ⚠️ <c>OnLoadAsync</c> cannot live on <see cref="RetryProbeActions"/>. It is not only the load
    /// endpoint's hook — delete, refresh and delete-row all load the object first, through the same
    /// seam — so prompting there made five of the other rows fail with <c>"Load?"</c>. Which is a
    /// genuine warning for anyone writing one: a retry in <c>OnLoadAsync</c> fires on <b>every</b> read
    /// of that type, including the ones another operation does on its way somewhere else.
    /// </remarks>
    public class RetryProbeRead
    {
        public string? Id { get; set; }
        public string Reference { get; set; } = "";
    }

    /// <summary>
    /// The hook that could not prompt at all until the read endpoints grew a body.
    /// </summary>
    /// <remarks>
    /// It was not missing a <c>catch</c>: a <c>GET</c> has no body, so an answer had nowhere to travel
    /// and the exception left the pipeline unhandled. Now that a load is <c>POST /spark/po/load</c> it
    /// goes through the same seam as every other hook, which is the claim these rows keep honest.
    /// </remarks>
    public class RetryProbeReadActions : DefaultPersistentObjectActions<RetryProbeRead>, ISparkOwnsRowSecurity
    {
        private readonly IRetryAccessor retry;
        public RetryProbeReadActions(IEntityMapper mapper, IRetryAccessor retry) : base(mapper) => this.retry = retry;

        public string RowSecurityRationale => "Test fixture; every probe is created by the test that reads it.";

        public override Task<Po?> OnLoadAsync(string id, Po? parent)
        {
            if (retry.Result is null)
                retry.Action("Load?", ["Yes", "No"], defaultOption: "No", message: "Confirm?");
            return base.OnLoadAsync(id, parent);
        }

        /// <summary>The other hook the read-verb change reached.</summary>
        public override Task OnQueryAsync(MintPlayer.Spark.Queries.SparkQueryContext context)
        {
            if (retry.Result is null)
                retry.Action("Query?", ["Yes", "No"], defaultOption: "No", message: "Confirm?");
            return Task.CompletedTask;
        }
    }

    /// <summary>A custom action that prompts before doing anything.</summary>
    /// <remarks>
    /// Registered by name through a stubbed configuration loader and resolver below, because the real
    /// ones read <c>App_Data/customActions.json</c> from the content root — a file this fixture has no
    /// reason to own.
    /// </remarks>
    public class RetryProbeConfirmAction : MintPlayer.Spark.Abstractions.Actions.ICustomAction
    {
        private readonly IRetryAccessor retry;
        public RetryProbeConfirmAction(IRetryAccessor retry) => this.retry = retry;

        public Task ExecuteAsync(
            MintPlayer.Spark.Abstractions.Actions.CustomActionArgs args,
            CancellationToken cancellationToken = default)
        {
            if (retry.Result is null)
                retry.Action("Run it?", ["Yes", "No"], defaultOption: "No", message: "Confirm?");
            return Task.CompletedTask;
        }
    }

    /// <summary>Raises a retry from the two row-lifecycle hooks.</summary>
    public class RetryProbeLineActions : DefaultPersistentObjectActions<RetryProbeLine>, ISparkOwnsRowSecurity
    {
        private readonly IRetryAccessor retry;
        public RetryProbeLineActions(IEntityMapper mapper, IRetryAccessor retry) : base(mapper) => this.retry = retry;

        public string RowSecurityRationale => "Test fixture; lines are reachable only through RetryProbe.";

        public override Task OnNewAsync(SparkNewArgs<RetryProbeLine> args)
        {
            if (retry.Result is null)
                retry.Action("New line?", ["Yes", "No"], defaultOption: "No", message: "Confirm?");
            return Task.CompletedTask;
        }

        public override Task OnDeleteRowAsync(SparkDeleteRowArgs<RetryProbeLine> args)
        {
            if (retry.Result is null)
                retry.Action("Remove line?", ["Yes", "No"], defaultOption: "No", message: "Confirm?");
            return Task.CompletedTask;
        }
    }

    private SparkEndpointFactory<RetryProbeContext> _factory = null!;
    private HttpClient _client = null!;
    private string _cookieHeader = null!;
    private string _xsrfToken = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        _factory = new SparkEndpointFactory<RetryProbeContext>(
            Store,
            [ProbeModel(), LineModel(), ReadModel()],
            configureServices: services =>
            {
                services.AddScoped<RetryProbeActions>();
                services.AddScoped<RetryProbeLineActions>();
                services.AddScoped<RetryProbeReadActions>();

                // The custom-action row needs a named action the endpoint will both find in the
                // configuration (its M3 gate: execution must agree with the listing) and resolve to
                // an implementation. Both come from files/assembly scanning in production; here they
                // are stubbed so the fixture owns no App_Data.
                services.AddScoped<RetryProbeConfirmAction>();
                services.AddSingleton<ICustomActionsConfigurationLoader>(
                    new StubCustomActions("RetryProbeConfirm"));
                services.AddScoped<ICustomActionResolver>(sp =>
                    new StubActionResolver("RetryProbeConfirm", sp.GetRequiredService<RetryProbeConfirmAction>()));
            },
            security: SparkTestSecurity.Permissive);

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

    // ---- emit: does the endpoint answer 449 at all? --------------------------------------------

    [Fact]
    public async Task Create_emits_a_retry() => await AssertEmitsRetryAsync(
        HttpMethod.Post, "/spark/po/create", Wire.Typed(ProbeTypeId, NewProbeBody()), "Save?");

    [Fact]
    public async Task Delete_emits_a_retry()
    {
        var probe = await SeedAsync();
        await AssertEmitsRetryAsync(
            HttpMethod.Post, "/spark/po/delete", Wire.Typed(ProbeTypeId, id: probe.Id), "Delete?");
    }

    [Fact]
    public async Task Refresh_emits_a_retry()
    {
        var probe = await SeedAsync();
        await AssertEmitsRetryAsync(
            HttpMethod.Post,
            "/spark/po/refresh", Wire.Typed(ProbeTypeId, new
            {
                persistentObject = new
                {
                    id = probe.Id,
                    name = "RetryProbe",
                    objectTypeId = ProbeTypeId.ToString(),
                    attributes = new[] { new { name = "Reference", value = "changed", isValueChanged = true } },
                },
                triggeredBy = "Reference",
            }),
            "Refresh?");
    }

    [Fact]
    public async Task New_row_emits_a_retry()
    {
        var probe = await SeedAsync();
        await AssertEmitsRetryAsync(
            HttpMethod.Post, "/spark/po/new", Wire.Typed(LineTypeId, NewRowBody(probe.Id!)), "New line?");
    }

    [Fact]
    public async Task Delete_row_emits_a_retry()
    {
        var probe = await SeedAsync();
        await AssertEmitsRetryAsync(
            HttpMethod.Post,
            "/spark/po/delete-row", Wire.Typed(LineTypeId, DeleteRowBody(probe.Id!, probe.Lines[0].Id.ToString())),
            "Remove line?");
    }

    /// <summary>
    /// The row M2 exists for: a read can prompt.
    /// </summary>
    /// <remarks>
    /// ⚠️ This one could not be written before the route table moved, and that is the point of having
    /// it. <c>OnLoadAsync</c> ran behind a <c>GET</c>, so a retry had no body to be answered in and the
    /// exception left the pipeline unhandled — a missing <c>catch</c> was never the problem.
    /// </remarks>
    [Fact]
    public async Task Load_emits_a_retry()
    {
        var read = await SeedReadAsync();
        await AssertEmitsRetryAsync(
            HttpMethod.Post, "/spark/po/load", Wire.Typed(ReadTypeId, id: read.Id), "Load?");
    }

    /// <summary>
    /// The row that guards the one endpoint whose catch-all could swallow a prompt.
    /// </summary>
    /// <remarks>
    /// ⚠️ Custom actions were the <b>only</b> retry-capable path this matrix did not cover, which was
    /// invisible until the emit half moved into the middleware: <c>ExecuteCustomAction</c> has a
    /// catch-all (R2-M1 — log the detail, return a generic 500), so removing its own retry
    /// <c>catch</c> left the prompt being logged as a failure and answered 500. The filter
    /// <c>when (ex is not SparkRetryActionException)</c> is what prevents that, and before this row
    /// nothing failed when it was absent.
    /// </remarks>
    [Fact]
    public async Task Custom_action_emits_a_retry()
    {
        await AssertEmitsRetryAsync(
            HttpMethod.Post,
            "/spark/actions/execute",
            Wire.Action(ProbeTypeId, "RetryProbeConfirm"),
            "Run it?");
    }

    /// <inheritdoc cref="Load_emits_a_retry" />
    [Fact]
    public async Task Query_emits_a_retry()
    {
        await SeedReadAsync();
        await AssertEmitsRetryAsync(
            HttpMethod.Post, "/spark/queries/execute", Wire.Query(ReadQueryId), "Query?");
    }

    // ---- accept: does answering it let the hook through? ---------------------------------------

    [Fact]
    public async Task Query_accepts_the_answer()
    {
        await SeedReadAsync();

        var (status, _) = await SendAsync(
            HttpMethod.Post, "/spark/queries/execute", Answered(Wire.Query(ReadQueryId)));

        status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Load_accepts_the_answer()
    {
        var read = await SeedReadAsync();

        var (status, _) = await SendAsync(
            HttpMethod.Post, "/spark/po/load", Answered(Wire.Typed(ReadTypeId, id: read.Id)));

        status.Should().Be(HttpStatusCode.OK,
            "the whole reason a load became a POST is that the answer has somewhere to travel");
    }

    [Fact]
    public async Task New_row_accepts_the_answer()
    {
        var probe = await SeedAsync();
        var body = NewRowBody(probe.Id!);

        var (status, _) = await SendAsync(HttpMethod.Post, "/spark/po/new", Wire.Typed(LineTypeId, Answered(body)));

        status.Should().Be(HttpStatusCode.OK,
            "once the prompt is answered the hook must run to completion, not raise the same prompt again");
    }

    [Fact]
    public async Task Delete_row_accepts_the_answer()
    {
        var probe = await SeedAsync();
        var body = DeleteRowBody(probe.Id!, probe.Lines[0].Id.ToString());

        var (status, _) = await SendAsync(HttpMethod.Post, "/spark/po/delete-row", Wire.Typed(LineTypeId, Answered(body)));

        status.Should().Be(HttpStatusCode.OK);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task AssertEmitsRetryAsync(HttpMethod method, string url, object payload, string expectedTitle)
    {
        var (status, body) = await SendAsync(method, url, payload);

        ((int)status).Should().Be(449,
            $"a hook raised a retry; the endpoint must surface it as a prompt rather than an error. "
            + $"Body was: {body}");

        var retry = body.GetProperty("operations").EnumerateArray()
            .First(o => o.GetProperty("type").GetString() == "retry");
        retry.GetProperty("title").GetString().Should().Be(expectedTitle);
        retry.GetProperty("options").EnumerateArray().Select(e => e.GetString()).Should().Equal("Yes", "No");
    }

    /// <summary>The same payload with step 0 answered — what the client resubmits.</summary>
    private static Dictionary<string, object?> Answered(object payload)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(payload))!;
        dict["retryResults"] = new[] { new { step = 0, option = "Yes" } };
        return dict;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string url, object payload)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("Cookie", _cookieHeader);
        request.Headers.Add("X-XSRF-TOKEN", _xsrfToken);

        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        return (response.StatusCode,
            string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<RetryProbe> SeedAsync()
    {
        var probe = new RetryProbe
        {
            Reference = "ORD-1",
            Lines = [new RetryProbeLine { Description = "Consultancy" }],
        };

        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(probe);
        await session.SaveChangesAsync();
        return probe;
    }

    private async Task<RetryProbeRead> SeedReadAsync()
    {
        var read = new RetryProbeRead { Reference = "READ-1" };

        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(read);
        await session.SaveChangesAsync();
        return read;
    }

    private static object NewProbeBody() => new
    {
        persistentObject = new
        {
            name = "RetryProbe",
            objectTypeId = ProbeTypeId.ToString(),
            attributes = new[] { new { name = "Reference", value = "ORD-NEW", isValueChanged = true } },
        },
    };

    private static object NewRowBody(string orderId) => new
    {
        asDetailAttribute = "Lines",
        parentType = ProbeTypeId.ToString(),
        parentId = orderId,
    };

    private static object DeleteRowBody(string orderId, string rowKey) => new
    {
        asDetailAttribute = "Lines",
        parentType = ProbeTypeId.ToString(),
        parentId = orderId,
        rowKey,
    };

    private static EntityTypeFile ProbeModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ProbeTypeId,
            Name = "RetryProbe",
            ClrType = typeof(RetryProbe).FullName!,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Reference", DataType = "string", IsVisible = true },
                new()
                {
                    Id = Guid.NewGuid(), Name = "Lines", DataType = "AsDetail", IsArray = true,
                    IsVisible = true, AsDetailType = typeof(RetryProbeLine).FullName!,
                },
            ],
        },
    };

    private static readonly Guid ReadQueryId = Guid.Parse("7b2d0000-0000-4000-8000-7b2d00000004");

    /// <summary>Stands in for <c>App_Data/customActions.json</c>, declaring exactly one action.</summary>
    private sealed class StubCustomActions(string actionName) : ICustomActionsConfigurationLoader
    {
        public CustomActionsConfiguration GetConfiguration() => new()
        {
            [actionName] = new CustomActionDefinition
            {
                DisplayName = TranslatedString.Create(actionName),
                ShowedOn = "both",
            },
        };

        public void InvalidateCache() { }
    }

    /// <summary>Stands in for the assembly scan, resolving exactly one action.</summary>
    private sealed class StubActionResolver(string actionName, MintPlayer.Spark.Abstractions.Actions.ICustomAction action)
        : ICustomActionResolver
    {
        public MintPlayer.Spark.Abstractions.Actions.ICustomAction? Resolve(string name)
            => string.Equals(name, actionName, StringComparison.OrdinalIgnoreCase) ? action : null;

        public IReadOnlyList<string> GetRegisteredActionNames() => [actionName];
    }

    private static EntityTypeFile ReadModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ReadTypeId,
            Name = "RetryProbeRead",
            ClrType = typeof(RetryProbeRead).FullName!,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Reference", DataType = "string", IsVisible = true },
            ],
        },
        Queries =
        [
            new SparkQuery
            {
                Id = ReadQueryId, Name = "RetryProbeReads",
                Source = "Database.Reads", EntityType = "RetryProbeRead",
            },
        ],
    };

    private static EntityTypeFile LineModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = LineTypeId,
            Name = "RetryProbeLine",
            ClrType = typeof(RetryProbeLine).FullName!,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Description", DataType = "string", IsVisible = true },
            ],
        },
    };
}
