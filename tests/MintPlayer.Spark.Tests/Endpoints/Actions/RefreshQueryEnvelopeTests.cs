using MintPlayer.Spark.Tests._Infrastructure;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Models;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.Endpoints.Actions;

/// <summary>
/// #319 — the server half of a custom action's <c>refreshQuery</c>, against the real route table. The
/// client matches a refresh to a grid by the query's id OR alias, case-insensitively; that is only
/// right if the operation carries the key exactly as the action wrote it, and if every such form
/// resolves to one query whose definition reports both keys.
/// </summary>
/// <remarks>
/// The fixture's names start with <c>RefProbe</c> for the reason given on <c>RetryFromEveryHookTests</c>:
/// <c>ActionsResolver</c> matches actions classes by simple name across the whole assembly.
/// </remarks>
public class RefreshQueryEnvelopeTests : SparkTestDriver
{
    private static readonly Guid ProbeTypeId = Guid.Parse("31900000-0000-4000-8000-319000000001");
    private const string ProbesQueryIdText = "3190abcd-0000-4000-8000-3190000000ef";
    private static readonly Guid ProbesQueryId = Guid.Parse(ProbesQueryIdText);
    private const string ProbesAlias = "ref-probes";

    private const string Refresh = "RefProbeRefresh";

    public class RefProbe
    {
        public string? Id { get; set; }
        public string Reference { get; set; } = "";
    }

    private sealed class RefProbeContext : SparkContext
    {
        public Raven.Client.Documents.Linq.IRavenQueryable<RefProbe> Probes => Session.Query<RefProbe>();
    }

    /// <summary>The key the action passes to <c>RefreshQuery</c>, set per test.</summary>
    public sealed class RefProbeTarget
    {
        public string Key { get; set; } = "";
    }

    public sealed class RefProbeRefreshAction(IManager manager, RefProbeTarget target) : ICustomAction
    {
        public Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
        {
            manager.Client.RefreshQuery(target.Key);
            return Task.CompletedTask;
        }
    }

    private SparkEndpointFactory<RefProbeContext> _factory = null!;
    private HttpClient _client = null!;
    private string _cookieHeader = null!;
    private string _xsrfToken = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new(
            Store,
            [ProbeModel()],
            configureServices: services =>
            {
                services.AddSingleton<RefProbeTarget>();
                services.AddScoped<RefProbeRefreshAction>();
                services.AddSingleton(TestActions.LoaderWithCustom(Refresh));
                services.AddScoped<ICustomActionResolver>(sp => new StubActionResolver(new Dictionary<string, ICustomAction>
                {
                    [Refresh] = sp.GetRequiredService<RefProbeRefreshAction>(),
                }));
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

    /// <summary>
    /// The server does not canonicalise the key: rewriting an alias to the id (or back) would still
    /// leave a grid opened by the other form unmatched, so the client matches both and the server
    /// passes on what the action wrote.
    /// </summary>
    [Theory]
    [InlineData(ProbesAlias)]
    [InlineData("Ref-Probes")]
    [InlineData(ProbesQueryIdText)]
    [InlineData("3190ABCD-0000-4000-8000-3190000000EF")]
    public async Task A_custom_actions_RefreshQuery_arrives_verbatim_on_the_envelope(string key)
    {
        _factory.GetService<RefProbeTarget>().Key = key;
        var probe = await SeedAsync("P-1");

        var (status, body) = await SendAsync("/spark/actions/execute", Wire.Action(ProbeTypeId, Refresh, new
        {
            parent = new
            {
                id = probe.Id,
                name = "RefProbe",
                objectTypeId = ProbeTypeId.ToString(),
                attributes = Array.Empty<object>(),
            },
        }));

        status.Should().Be(HttpStatusCode.OK);
        var operations = body.GetProperty("operations");
        operations.GetArrayLength().Should().Be(1);
        operations[0].GetProperty("type").GetString().Should().Be("refreshQuery");
        operations[0].GetProperty("queryId").GetString().Should().Be(key);
    }

    /// <summary>
    /// Every form a refresh may carry resolves to the one query, and its definition carries both
    /// the id and the alias — the two keys a grid learns from it and answers to.
    /// </summary>
    [Theory]
    [InlineData(ProbesAlias)]
    [InlineData("REF-PROBES")]
    [InlineData(ProbesQueryIdText)]
    [InlineData("3190ABCD-0000-4000-8000-3190000000EF")]
    public async Task Every_form_of_the_key_resolves_to_the_query_which_reports_both_keys(string key)
    {
        var (status, body) = await SendAsync("/spark/queries/get", Wire.Query(key));

        status.Should().Be(HttpStatusCode.OK);
        Guid.Parse(body.GetProperty("id").GetString()!).Should().Be(ProbesQueryId);
        body.GetProperty("alias").GetString().Should().Be(ProbesAlias);
    }

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

    private async Task<RefProbe> SeedAsync(string reference)
    {
        var probe = new RefProbe { Reference = reference };
        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(probe);
        await session.SaveChangesAsync();
        return probe;
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
            Name = "RefProbe",
            ClrType = typeof(RefProbe).FullName!,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Reference", DataType = "string" },
            ],
        },
        Queries =
        [
            new SparkQuery { Id = ProbesQueryId, Name = "RefProbes", Alias = ProbesAlias, Source = "Database.Probes", EntityType = "RefProbe" },
        ],
    };
}
