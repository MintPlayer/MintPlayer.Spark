using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Live;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Live;

/// <summary>
/// App streams (a live query whose rows come from an <c>IAsyncEnumerable</c> method, not from RavenDB), the
/// successor of streaming queries, through the real route table. Each test names the streaming defect it pins
/// (PRD §5 "Defects in today's streaming queries").
/// </summary>
/// <remarks>Fixture names start with <c>Ls</c>: <c>ActionsResolver</c> matches actions classes by simple name.</remarks>
public class LiveAppStreamTests : SparkTestDriver
{
    internal static readonly Guid TickerTypeId = Guid.Parse("1f5e0000-0000-4000-8000-000000000001");
    internal static readonly Guid TickersQueryId = Guid.Parse("1f5e0000-0000-4000-8000-000000000011");
    private static readonly Uri Server = new("http://localhost/");

    private readonly List<IAsyncDisposable> factories = [];
    private bool allowed = true;

    public override async Task DisposeAsync()
    {
        foreach (var factory in factories)
            await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private SparkEndpointFactory<LsContext> Start(bool live = true)
    {
        LsTickerActions.Reset();
        var access = SparkTestAccessControl.Matching(resource => allowed || !resource.EndsWith("/LsTicker", StringComparison.Ordinal));
        var factory = new SparkEndpointFactory<LsContext>(
            Store,
            [new EntityTypeFile
            {
                PersistentObject = new EntityTypeDefinition
                {
                    Id = TickerTypeId,
                    Name = "LsTicker",
                    ClrType = typeof(LsTicker).FullName!,
                    Attributes =
                    [
                        new() { Id = Guid.NewGuid(), Name = "Symbol", DataType = "string" },
                        new() { Id = Guid.NewGuid(), Name = "Price", DataType = "decimal" },
                    ],
                },
                Queries = [new SparkQuery { Id = TickersQueryId, Name = "LsTickers", Source = "Custom.Ticks", EntityType = "LsTicker", Live = live }],
            }],
            configureServices: services => services.UseSparkTestAccessControl(access));
        factories.Add(factory);
        return factory;
    }

    private static async Task<LiveTestClient> ConnectAsync(SparkEndpointFactory<LsContext> factory)
        => await LiveTestClient.ConnectAsync(factory.CreateWebSocketClient(), Server);

    private static IReadOnlyList<LsTicker> Set(params (string Symbol, decimal Price)[] rows)
        => [.. rows.Select(r => new LsTicker { Id = "LsTickers/" + r.Symbol, Symbol = r.Symbol, Price = r.Price })];

    private static List<string> Symbols(JsonElement result)
        => [.. result.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("values").EnumerateArray()
            .First(v => v.GetProperty("key").GetString() == "Symbol").GetProperty("value").GetString()!)];

    [Fact]
    public async Task A_row_missing_from_the_next_whole_set_is_removed_and_a_new_one_appears()
    {
        // Defect 1: removed rows were never signalled. Defect 2 (server half): added rows are sent with their place.
        var factory = Start();
        await using var client = await ConnectAsync(factory);
        await client.SubscribeQueryAsync("t", new { queryId = TickersQueryId.ToString() });

        await LsTickerActions.Feed.Writer.WriteAsync(Set(("AAA", 1), ("BBB", 2), ("CCC", 3)));
        Symbols((await client.NextAsync("result", "t")).GetProperty("result")).Should().Equal("AAA", "BBB", "CCC");

        await LsTickerActions.Feed.Writer.WriteAsync(Set(("AAA", 1), ("CCC", 3), ("DDD", 4)));
        var patch = (await client.NextAsync("patch", "t")).GetProperty("patch");
        patch.GetProperty("remove").EnumerateArray().Select(i => i.GetString()).Should().Equal("LsTickers/BBB");
        patch.GetProperty("upsert").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Should().Equal("LsTickers/DDD");
        patch.GetProperty("order").EnumerateArray().Select(i => i.GetString()).Should().Equal("LsTickers/AAA", "LsTickers/CCC", "LsTickers/DDD");
    }

    [Fact]
    public async Task Search_sort_and_paging_are_applied_on_the_server_with_a_real_total()
    {
        // The old stream had no total, no order and no paging: the client kept and filtered the whole set.
        var factory = Start();
        await using var client = await ConnectAsync(factory);
        await client.SubscribeQueryAsync("t", new { queryId = TickersQueryId.ToString(), take = 2, sortColumns = new[] { new { property = "Price", direction = "desc" } } });
        await LsTickerActions.Feed.Writer.WriteAsync(Set(("AAA", 1), ("BBB", 30), ("CCC", 4), ("ABC", 10)));

        var result = (await client.NextAsync("result", "t")).GetProperty("result");
        Symbols(result).Should().Equal("BBB", "ABC");
        result.GetProperty("totalItems").GetInt32().Should().Be(4);

        // An update restarts the method with the new arguments, and the search narrows in memory.
        await client.UpdateAsync("t", new { queryId = TickersQueryId.ToString(), search = "a", take = 10, sortColumns = new[] { new { property = "Symbol", direction = "asc" } } });
        // Feed the restarted method, not the one being stopped.
        for (var waited = 0; LsTickerActions.Starts < 2 && waited < 100; waited++)
            await Task.Delay(50);
        await LsTickerActions.Feed.Writer.WriteAsync(Set(("AAA", 1), ("BBB", 30), ("CCC", 4), ("ABC", 10)));
        var narrowed = await client.NextAsync();
        var page = narrowed.GetProperty("type").GetString() == "result" ? narrowed.GetProperty("result") : default;
        if (page.ValueKind == JsonValueKind.Undefined)
        {
            // A patch against the previous page: the whole new page is AAA, ABC.
            narrowed.GetProperty("patch").GetProperty("order").EnumerateArray().Select(i => i.GetString()).Should().Equal("LsTickers/AAA", "LsTickers/ABC");
            narrowed.GetProperty("patch").GetProperty("set").GetProperty("totalItems").GetInt32().Should().Be(2);
        }
        else
        {
            Symbols(page).Should().Equal("AAA", "ABC");
        }
        LsTickerActions.Starts.Should().Be(2, "an update restarts the app stream with the new page, sort and search");
        LsTickerActions.LastArgs!.Search.Should().Be("a");
        // Commerce PRD X2: the method is told who watches, frozen at the handshake (anonymous here: an address).
        LsTickerActions.LastArgs.Viewer.IdentityKey.Should().StartWith("ip:");
        LsTickerActions.LastArgs.Viewer.UserId.Should().BeNull();
    }

    [Fact]
    public async Task A_revoked_right_ends_the_subscription_with_unavailable_not_a_silent_close()
    {
        // Defect 4: a revoked right closed the socket with 1000, and the grid silently kept stale rows.
        // Defect 3: access denied closed with 1011, so the client retried ten times; now it is a per-subscription
        // error and the socket stays open.
        var factory = Start();
        await using var client = await ConnectAsync(factory);
        await client.SubscribeQueryAsync("t", new { queryId = TickersQueryId.ToString() });
        await LsTickerActions.Feed.Writer.WriteAsync(Set(("AAA", 1)));
        await client.NextAsync("result", "t");

        allowed = false;
        await LsTickerActions.Feed.Writer.WriteAsync(Set(("AAA", 2)));

        var error = await client.NextAsync("error", "t");
        error.GetProperty("code").GetString().Should().Be(LiveCodes.Unavailable);
        client.State.Should().Be(System.Net.WebSockets.WebSocketState.Open);
    }

    [Fact]
    public async Task The_method_is_not_started_for_a_viewer_without_the_Query_right()
    {
        allowed = false;
        var factory = Start();
        await using var client = await ConnectAsync(factory);
        await client.SubscribeQueryAsync("t", new { queryId = TickersQueryId.ToString() });

        (await client.NextAsync("error", "t")).GetProperty("code").GetString().Should().Be(LiveCodes.Unavailable);
        LsTickerActions.Starts.Should().Be(0, "an app stream is app code with its own cost: it never runs for a caller who may not see it");
    }

    [Fact]
    public async Task Execute_answers_an_app_stream_with_an_empty_page_and_filters_are_not_offered()
    {
        var factory = Start();
        using var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync("/spark/queries/execute", new { queryId = TickersQueryId.ToString() });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("totalItems").GetInt32().Should().Be(0);
        LsTickerActions.Starts.Should().Be(0);
    }

    [Fact]
    public void Startup_refuses_an_app_stream_method_on_a_query_that_is_not_live()
    {
        var act = () => Start(live: false);

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("Add \"live\": true");
    }
}

public class LsTicker
{
    public string? Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

public class LsContext : SparkContext
{
    public IRavenQueryable<LsTicker> Tickers => Session.Query<LsTicker>();
}

public class LsTickerActions(MintPlayer.Spark.Services.IEntityMapper mapper) : DefaultPersistentObjectActions<LsTicker>(mapper), ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: in-memory tickers, no rows in RavenDB.";

    // Per test class, and xUnit runs a class's tests one at a time: Reset() at each host start.
    internal static Channel<IReadOnlyList<LsTicker>> Feed = Channel.CreateUnbounded<IReadOnlyList<LsTicker>>();
    internal static int Starts;
    internal static LiveQueryArgs? LastArgs;

    internal static void Reset()
    {
        Feed = Channel.CreateUnbounded<IReadOnlyList<LsTicker>>();
        Starts = 0;
        LastArgs = null;
    }

    public async IAsyncEnumerable<IReadOnlyList<LsTicker>> Ticks(LiveQueryArgs args, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Starts);
        LastArgs = args;
        await foreach (var set in Feed.Reader.ReadAllAsync(cancellationToken))
            yield return set;
    }
}
