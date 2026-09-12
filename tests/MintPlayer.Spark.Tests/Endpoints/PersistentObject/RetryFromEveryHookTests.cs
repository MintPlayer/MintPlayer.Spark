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
using MintPlayer.Spark.Testing;

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
/// Measured 2026-09-12: create, update, delete and custom action have both halves. <b>Refresh has
/// accept and not emit. New and delete-row have neither.</b> These tests pin the whole matrix so the
/// next endpoint cannot be added half-wired.
/// </para>
/// <para>
/// ⚠️ The hooks here raise a retry <b>only on the first pass</b> — they check
/// <see cref="IRetryAccessor.Result"/> first. A hook that raises unconditionally makes an endpoint
/// that answers 449 forever, and the Angular client resubmits with no depth limit
/// (<c>spark.service.ts:375-376</c>). That is a real trap for anyone writing one of these hooks,
/// which is why the fixtures model the correct shape rather than the shortest one.
/// </para>
/// </remarks>
public class RetryFromEveryHookTests : SparkTestDriver
{
    private static readonly Guid OrderTypeId = Guid.Parse("7b2d0000-0000-4000-8000-7b2d00000001");
    private static readonly Guid LineTypeId = Guid.Parse("7b2d0000-0000-4000-8000-7b2d00000002");

    public class Order
    {
        public string? Id { get; set; }
        public string Reference { get; set; } = "";
        public List<OrderLine> Lines { get; set; } = [];
    }

    public class OrderLine
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Description { get; set; } = "";
    }

    private class OrderContext : SparkContext
    {
        public Raven.Client.Documents.Linq.IRavenQueryable<Order> Orders => Session.Query<Order>();
    }

    static RetryFromEveryHookTests()
        => SparkValueObjects.Register(typeof(OrderLine), "Id", row => ((OrderLine)row).Id.ToString());

    /// <summary>Raises one retry from every hook it overrides, once each.</summary>
    public class OrderActions : DefaultPersistentObjectActions<Order>, ISparkOwnsRowSecurity
    {
        private readonly IRetryAccessor retry;
        public OrderActions(IEntityMapper mapper, IRetryAccessor retry) : base(mapper) => this.retry = retry;

        public string RowSecurityRationale => "Test fixture; every order is created by the test that reads it.";

        private void PromptOnce(string title)
        {
            // Ask only until answered. Re-raising unconditionally is an infinite loop, not a test.
            if (retry.Result is null)
                retry.Action(title, ["Yes", "No"], defaultOption: "No", message: "Confirm?");
        }

        public override Task OnBeforeSaveAsync(Po obj, Order entity)
        {
            PromptOnce("Save?");
            return Task.CompletedTask;
        }

        public override Task OnBeforeDeleteAsync(Order entity)
        {
            PromptOnce("Delete?");
            return Task.CompletedTask;
        }

        public override Task OnRefreshAsync(SparkRefreshArgs<Order> args)
        {
            PromptOnce("Refresh?");
            return Task.CompletedTask;
        }
    }

    /// <summary>Raises a retry from the two row-lifecycle hooks.</summary>
    public class OrderLineActions : DefaultPersistentObjectActions<OrderLine>, ISparkOwnsRowSecurity
    {
        private readonly IRetryAccessor retry;
        public OrderLineActions(IEntityMapper mapper, IRetryAccessor retry) : base(mapper) => this.retry = retry;

        public string RowSecurityRationale => "Test fixture; lines are reachable only through Order.";

        public override Task OnNewAsync(SparkNewArgs<OrderLine> args)
        {
            if (retry.Result is null)
                retry.Action("New line?", ["Yes", "No"], defaultOption: "No", message: "Confirm?");
            return Task.CompletedTask;
        }

        public override Task OnDeleteRowAsync(SparkDeleteRowArgs<OrderLine> args)
        {
            if (retry.Result is null)
                retry.Action("Remove line?", ["Yes", "No"], defaultOption: "No", message: "Confirm?");
            return Task.CompletedTask;
        }
    }

    private SparkEndpointFactory<OrderContext> _factory = null!;
    private HttpClient _client = null!;
    private string _cookieHeader = null!;
    private string _xsrfToken = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        _factory = new SparkEndpointFactory<OrderContext>(
            Store,
            [OrderModel(), LineModel()],
            configureServices: services =>
            {
                services.AddScoped<OrderActions>();
                services.AddScoped<OrderLineActions>();
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
        HttpMethod.Post, $"/spark/po/{OrderTypeId}/", NewOrderBody(), "Save?");

    [Fact]
    public async Task Delete_emits_a_retry()
    {
        var order = await SeedAsync();
        await AssertEmitsRetryAsync(
            HttpMethod.Delete, $"/spark/po/{OrderTypeId}/{order.Id}", new { }, "Delete?");
    }

    [Fact]
    public async Task Refresh_emits_a_retry()
    {
        var order = await SeedAsync();
        await AssertEmitsRetryAsync(
            HttpMethod.Post,
            $"/spark/po/{OrderTypeId}/refresh",
            new
            {
                persistentObject = new
                {
                    id = order.Id,
                    name = "Order",
                    objectTypeId = OrderTypeId.ToString(),
                    attributes = new[] { new { name = "Reference", value = "changed", isValueChanged = true } },
                },
                triggeredBy = "Reference",
            },
            "Refresh?");
    }

    [Fact]
    public async Task New_row_emits_a_retry()
    {
        var order = await SeedAsync();
        await AssertEmitsRetryAsync(
            HttpMethod.Post, $"/spark/po/{LineTypeId}/new", NewRowBody(order.Id!), "New line?");
    }

    [Fact]
    public async Task Delete_row_emits_a_retry()
    {
        var order = await SeedAsync();
        await AssertEmitsRetryAsync(
            HttpMethod.Post,
            $"/spark/po/{LineTypeId}/delete-row",
            DeleteRowBody(order.Id!, order.Lines[0].Id.ToString()),
            "Remove line?");
    }

    // ---- accept: does answering it let the hook through? ---------------------------------------

    [Fact]
    public async Task New_row_accepts_the_answer()
    {
        var order = await SeedAsync();
        var body = NewRowBody(order.Id!);

        var (status, _) = await SendAsync(HttpMethod.Post, $"/spark/po/{LineTypeId}/new", Answered(body));

        status.Should().Be(HttpStatusCode.OK,
            "once the prompt is answered the hook must run to completion, not raise the same prompt again");
    }

    [Fact]
    public async Task Delete_row_accepts_the_answer()
    {
        var order = await SeedAsync();
        var body = DeleteRowBody(order.Id!, order.Lines[0].Id.ToString());

        var (status, _) = await SendAsync(HttpMethod.Post, $"/spark/po/{LineTypeId}/delete-row", Answered(body));

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

    private async Task<Order> SeedAsync()
    {
        var order = new Order
        {
            Reference = "ORD-1",
            Lines = [new OrderLine { Description = "Consultancy" }],
        };

        using var session = Store.OpenAsyncSession();
        await session.StoreAsync(order);
        await session.SaveChangesAsync();
        return order;
    }

    private static object NewOrderBody() => new
    {
        persistentObject = new
        {
            name = "Order",
            objectTypeId = OrderTypeId.ToString(),
            attributes = new[] { new { name = "Reference", value = "ORD-NEW", isValueChanged = true } },
        },
    };

    private static object NewRowBody(string orderId) => new
    {
        asDetailAttribute = "Lines",
        parentType = OrderTypeId.ToString(),
        parentId = orderId,
    };

    private static object DeleteRowBody(string orderId, string rowKey) => new
    {
        asDetailAttribute = "Lines",
        parentType = OrderTypeId.ToString(),
        parentId = orderId,
        rowKey,
    };

    private static EntityTypeFile OrderModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = OrderTypeId,
            Name = "Order",
            ClrType = typeof(Order).FullName!,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Reference", DataType = "string", IsVisible = true },
                new()
                {
                    Id = Guid.NewGuid(), Name = "Lines", DataType = "AsDetail", IsArray = true,
                    IsVisible = true, AsDetailType = typeof(OrderLine).FullName!,
                },
            ],
        },
    };

    private static EntityTypeFile LineModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = LineTypeId,
            Name = "OrderLine",
            ClrType = typeof(OrderLine).FullName!,
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Description", DataType = "string", IsVisible = true },
            ],
        },
    };
}
