using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Contracts;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Services;
using MintPlayer.Spark.WaitingRoom;

namespace MintPlayer.Spark.Commerce.Tests.Events;

/// <summary>Builders and fakes for the ticketing-events tests (M13).</summary>
internal static class EventFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    public static CommerceChannelOptions Channel(Action<CommerceChannelOptions>? configure = null)
    {
        var channel = new CommerceChannelOptions { Allocation = AllocationMode.HoldFromSelection, PerBuyerLimit = 6 };
        channel.WaitingRoom.Modes = [WaitingRoomMode.RandomizedPreQueue, WaitingRoomMode.Fifo];
        configure?.Invoke(channel);
        return channel;
    }

    public static SparkCommerceOptions CommerceOptions(CommerceChannelOptions? channel = null)
    {
        var options = new SparkCommerceOptions();
        options.Channels["tickets"] = channel ?? Channel();
        return options;
    }

    public static TicketEvent Event(string id = "TicketEvents/1-A") => new()
    {
        Id = id,
        OrganizationId = "Organizations/seller",
        IsPublished = true,
        Name = TranslatedString.Of(("en", "Summer Fest"), ("nl", "Zomerfeest")),
        Slug = "summer-fest",
        Channel = "tickets",
    };

    public static TicketEventDate Date(string id, DateTimeOffset startsAt, int capacity = 100, string eventId = "TicketEvents/1-A") => new()
    {
        Id = id,
        EventId = eventId,
        OrganizationId = "Organizations/seller",
        StartsAt = startsAt,
        Capacity = capacity,
        VenueTimeZoneId = "Europe/Brussels",
    };

    public static TicketType Type(string id, ETicketTypeKind kind = ETicketTypeKind.Admission, string eventId = "TicketEvents/1-A") => new()
    {
        Id = id,
        EventId = eventId,
        OrganizationId = "Organizations/seller",
        Name = TranslatedString.Of("en", id ?? "type"),
        Kind = kind,
    };

    public static TicketSalePhase Phase(string id, params (string TypeId, string Category, decimal Price)[] prices) => new()
    {
        Id = id,
        EventId = "TicketEvents/1-A",
        OrganizationId = "Organizations/seller",
        Name = TranslatedString.Of("en", "Sale"),
        StartsAt = Now.AddDays(-1),
        Prices = prices.Select(p => new TicketPhasePrice { TicketTypeId = p.TypeId, Category = p.Category, Price = new Money(p.Price, "EUR") }).ToList(),
    };

    public static IOptionsMonitor<T> Monitor<T>(T value) => new FixedOptionsMonitor<T>(value);

    private sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

internal sealed class EventTestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A seller that may sell, with every legal document published.</summary>
internal sealed class OpenSellers : ISparkSellers, ISparkLegalDocuments
{
    public List<string> MissingDocuments { get; } = [];

    public SellerGate Gate { get; set; } = SellerGate.Open;

    public Task<SellerStanding?> GetStandingAsync(string organizationId, CancellationToken cancellationToken = default) => Task.FromResult<SellerStanding?>(null);

    public Task<SellerGate> CanSellAsync(string? organizationId, string channel, CancellationToken cancellationToken = default) => Task.FromResult(Gate);

    public Task<SellerGate> CanReceivePayoutsAsync(string organizationId, CancellationToken cancellationToken = default) => Task.FromResult(SellerGate.Open);

    public Task<IReadOnlyList<LegalDocumentRef>> GetInForceAsync(string? organizationId, DateTimeOffset at, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LegalDocumentRef>>([]);

    public Task<IReadOnlyList<string>> GetMissingAsync(string? organizationId, IEnumerable<string> required, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(MissingDocuments.ToList());

    public Task RecordAcceptanceAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, string subjectId, LegalDocumentRef document, string source, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>Venues for unseated dates only: every call fails the test.</summary>
internal sealed class NoVenues : ISparkVenues
{
    private static Exception Unexpected() => new InvalidOperationException("An unseated date must not touch Ticketing.Venues.");

    public Task<VenueLayoutRef> CreateLayoutAsync(string venueId, string name, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<VenueLayoutRef> CopyLayoutAsync(string layoutId, string name, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<VenueLayoutPlanResponse> GetPlanAsync(string layoutId, string version, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<int> SaveDraftAsync(string layoutId, int revision, LayoutPlan plan, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<int> ImportCsvAsync(string layoutId, int revision, string csv, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<VenueLayoutPublishResponse> PublishAsync(string layoutId, int revision, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<LayoutDiffResult> DiffAsync(string layoutId, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<string> UploadBackgroundAsync(string layoutId, Stream content, string contentType, long length, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<string> SnapshotAsync(string layoutId, string scope, int? version = null, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task<VenueLayoutSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken cancellationToken = default) => Task.FromResult<VenueLayoutSnapshot?>(null);
    public Task<SnapshotInventory> ProvisionAsync(string snapshotId, CancellationToken cancellationToken = default) => throw Unexpected();
    public Task SetLiveAsync(string snapshotId, DateTime liveUntilUtc, CancellationToken cancellationToken = default) => throw Unexpected();
}

/// <summary>A checkout that knows one order (the caller's own) and records basket calls.</summary>
internal sealed class FakeCheckout : ISparkCheckout
{
    public CheckoutOrder? Order { get; set; }

    public List<SparkBasketLine> Lines { get; } = [];

    public Func<SparkBasketAddRequest, string?> Refuse { get; set; } = _ => null;

    public Task<SparkBasket> GetBasketAsync(CancellationToken cancellationToken = default) => Task.FromResult(Basket());

    public Task<SparkBasket> AddLineAsync(SparkBasketAddRequest request, CancellationToken cancellationToken = default)
    {
        if (Refuse(request) is { } code)
            throw CheckoutException.Of(code, subject: request.ItemId);
        Lines.Add(new SparkBasketLine(Guid.NewGuid().ToString("N"), request.ItemId, null, "tickets", null, request.Quantity, [], null, false, null, null, null, null));
        return Task.FromResult(Basket());
    }

    public Task<SparkBasket> SetQuantityAsync(string lineId, int quantity, CancellationToken cancellationToken = default)
    {
        var index = Lines.FindIndex(l => l.LineId == lineId);
        if (index < 0)
            throw CheckoutException.Of(CheckoutRefusals.LineNotFound);
        if (quantity == 0)
            Lines.RemoveAt(index);
        else
            Lines[index] = Lines[index] with { Quantity = quantity };
        return Task.FromResult(Basket());
    }

    public Task<SparkBasket> SetChoicesAsync(IReadOnlyDictionary<string, string?> choices, string? email, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SparkCheckoutSummary> GetSummaryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SparkPlaceOrderResult> PlaceOrderAsync(SparkPlaceOrderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CheckoutOrder?> GetOwnOrderAsync(string number, CancellationToken cancellationToken = default)
        => Task.FromResult(Order is { } order && order.Number == number ? order : null);

    public Task<CheckoutOrder?> CancelPaymentAsync(string number, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task MarkShippedAsync(string orderId, string lineId, DateTimeOffset shippedAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    private SparkBasket Basket() => new(Lines.ToList(), "EUR", null, null, false, new Dictionary<string, string>(), null);
}
