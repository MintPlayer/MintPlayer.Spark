using CheckoutContracts = MintPlayer.Spark.Commerce.Checkout.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Commerce.Checkout.Test;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Payments.Models;
using MintPlayer.Spark.Commerce.Pricing;
using MintPlayer.Spark.Commerce.Tests.Payments;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>A clock the test moves.</summary>
internal sealed class CheckoutClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>The buyer of the test: an account or a guest visitor.</summary>
internal sealed class TestBuyer : ICheckoutBuyer
{
    public string? UserId { get; set; }

    public string? VisitorId { get; set; } = "visitor-1";

    public string? IpAddress => "203.0.113.7";

    public ValueTask<string?> EnsureVisitorAsync() => ValueTask.FromResult(UserId is null ? VisitorId : null);
}

/// <summary>Items by id, each a fixed <see cref="CheckoutItem"/>; seats requested by the buyer go into the capacity.</summary>
internal sealed class TestCapacitySource : ICheckoutCapacitySource
{
    public Dictionary<string, CheckoutItem> Items { get; } = new(StringComparer.Ordinal);

    public bool Handles(string itemId) => Items.ContainsKey(itemId);

    public Task<CheckoutItem?> ResolveAsync(CheckoutItemRequest request, CancellationToken cancellationToken)
    {
        if (!Items.TryGetValue(request.ItemId, out var item))
            return Task.FromResult<CheckoutItem?>(null);
        // A seated item takes the buyer's seats; an unnumbered one N units of its mapped item.
        var capacity = request.Units is { Count: > 0 } units
            ? new CheckoutCapacity { Units = units }
            : item.Capacity.IsEmpty ? item.Capacity : new CheckoutCapacity { Items = [new ItemLine(item.ItemId, request.Quantity)] };
        return Task.FromResult<CheckoutItem?>(item with { Capacity = capacity });
    }
}

/// <summary>Payments without a provider: records every call; tests raise the provider's answers through the lifecycle.</summary>
internal sealed class FakePayments : ISparkPayments
{
    public List<PaymentCreation> Created { get; } = [];
    public List<(string PaymentId, Money Amount, string Reference)> Refunds { get; } = [];
    public List<string> Voided { get; } = [];
    public List<(string PaymentId, Money? Amount)> Captured { get; } = [];
    public List<string> Cancelled { get; } = [];
    public Dictionary<string, ProviderPayment> Payments { get; } = [];

    public static readonly PaymentMethodInfo Card = new("test", "card", "card", TimeSpan.FromMinutes(30), true, false, true, FingerprintTiming.AfterPayment, true);
    public static readonly PaymentMethodInfo Transfer = new("test", "banktransfer", "banktransfer", TimeSpan.FromDays(12), false, true, false, FingerprintTiming.None, false);

    public string DefaultProvider => "test";

    public IReadOnlyList<PaymentMethodInfo> GetMethods(string? provider = null) => [Card, Transfer];

    public IReadOnlyList<PaymentMethodInfo> GetOfferableMethods(PaymentMethodQuery query, string? provider = null)
        => PaymentMethodCatalog.Offerable(GetMethods(provider), query);

    public Task<ProviderPayment> CreatePaymentAsync(PaymentCreation creation, CancellationToken cancellationToken = default)
    {
        Created.Add(creation);
        var payment = new ProviderPayment
        {
            Id = $"ProviderPayments/{Payments.Count + 1}", Provider = "test", ProviderPaymentId = $"tr_{Payments.Count + 1}",
            Reference = creation.Reference, Amount = creation.Amount, Status = ProviderPaymentStatus.Open,
            Capture = creation.Capture, CheckoutUrl = "https://pay.example.org/" + (Payments.Count + 1),
        };
        Payments[payment.Id] = payment;
        return Task.FromResult(payment);
    }

    public Task<ProviderPayment?> GetPaymentAsync(string paymentId, bool refresh = false, CancellationToken cancellationToken = default)
        => Task.FromResult(Payments.TryGetValue(paymentId, out var p) ? p : null);

    public Task<ProviderPayment> CancelAsync(string paymentId, CancellationToken cancellationToken = default)
    {
        Cancelled.Add(paymentId);
        return Task.FromResult(Payments[paymentId]);
    }

    public Task<ProviderPayment> CaptureAsync(string paymentId, Money? amount = null, CancellationToken cancellationToken = default)
    {
        Captured.Add((paymentId, amount));
        return Task.FromResult(Payments[paymentId]);
    }

    public Task<ProviderPayment> VoidAsync(string paymentId, CancellationToken cancellationToken = default)
    {
        Voided.Add(paymentId);
        return Task.FromResult(Payments[paymentId]);
    }

    public Task<ProviderPaymentRefund> RefundAsync(string paymentId, Money amount, string reference, bool refundApplicationFee = true, int attempt = 0, CancellationToken cancellationToken = default)
    {
        Refunds.Add((paymentId, amount, reference));
        return Task.FromResult(new ProviderPaymentRefund { Reference = reference, Amount = amount, Status = ProviderRefundStatus.Pending });
    }

    public Task ProcessEventsAsync(IReadOnlyList<PaymentProviderEvent> events, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<DisputeEvidencePack> AssembleEvidenceAsync(string disputeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<DisputeEvidenceSubmission> SubmitEvidenceAsync(string disputeId, string actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<PaymentDispute> AcceptDisputeAsync(string disputeId, string actingUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>The provider says the payment moved to <paramref name="status"/>.</summary>
    public ProviderPayment Set(string paymentId, ProviderPaymentStatus status, string? fingerprint = null)
    {
        var payment = Payments[paymentId];
        payment.Status = status;
        payment.PayerFingerprint = fingerprint ?? payment.PayerFingerprint;
        return payment;
    }
}

internal sealed class OpenSellers : ISparkSellers
{
    public Task<SellerStanding?> GetStandingAsync(string organizationId, CancellationToken cancellationToken = default) => Task.FromResult<SellerStanding?>(null);

    public Task<SellerGate> CanSellAsync(string? organizationId, string channel, CancellationToken cancellationToken = default) => Task.FromResult(SellerGate.Open);

    public Task<SellerGate> CanReceivePayoutsAsync(string organizationId, CancellationToken cancellationToken = default) => Task.FromResult(SellerGate.Open);
}

/// <summary>Terms of sale v1 for the platform and every seller (GA2).</summary>
internal sealed class TestLegalDocuments : ISparkLegalDocuments
{
    public int Version { get; set; } = 1;

    public bool Missing { get; set; }

    public LegalDocumentRef TermsFor(string? organizationId)
        => new($"LegalDocuments/terms-{organizationId ?? "platform"}-v{Version}", LegalDocumentKinds.TermsOfSale, organizationId, Version, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    public Task<IReadOnlyList<LegalDocumentRef>> GetInForceAsync(string? organizationId, DateTimeOffset at, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LegalDocumentRef>>(Missing ? [] : [TermsFor(organizationId)]);

    public Task<IReadOnlyList<string>> GetMissingAsync(string? organizationId, IEnumerable<string> required, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Missing ? required.ToList() : []);

    public Task RecordAcceptanceAsync(IAsyncDocumentSession session, string subjectId, LegalDocumentRef document, string source, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

internal sealed class NoAudit : ICommerceAudit
{
    public List<CommerceAuditEntry> Entries { get; } = [];

    public Task RecordAsync(IAsyncDocumentSession session, CommerceAuditEntry entry, CancellationToken cancellationToken = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingOrderEvents : IOrderEventRecipient
{
    public List<OrderEvent> Events { get; } = [];

    public Task OnOrderEventAsync(OrderEvent orderEvent, CancellationToken cancellationToken)
    {
        Events.Add(orderEvent);
        return Task.CompletedTask;
    }
}

/// <summary>Checkout wired by hand over a real RavenDB and inventory engine, with fake payments, sellers and legal documents.</summary>
internal sealed class CheckoutHost
{
    public required IServiceProvider Services { get; init; }
    public required CheckoutClock Clock { get; init; }
    public required TestBuyer Buyer { get; init; }
    public required TestCapacitySource Catalog { get; init; }
    public required FakePayments Payments { get; init; }
    public required TestLegalDocuments Legal { get; init; }
    public required RecordingOrderEvents Events { get; init; }
    public required InventoryEngine Inventory { get; init; }
    public required SparkCommerceOptions Commerce { get; init; }
    public required SparkCommerceCheckoutOptions Options { get; init; }

    public T Get<T>() where T : notnull => Services.CreateScope().ServiceProvider.GetRequiredService<T>();

    public ISparkCheckout Checkout => Get<ISparkCheckout>();

    public OrderLifecycle Lifecycle => Get<OrderLifecycle>();

    public static CheckoutHost Build(IDocumentStore store, Action<SparkCommerceOptions>? commerce = null, Action<SparkCommerceCheckoutOptions>? checkout = null,
        Action<IServiceCollection, FakePayments, InventoryEngine>? configure = null)
    {
        var clock = new CheckoutClock(DateTimeOffset.Parse("2026-10-19T10:00:00Z"));
        var commerceOptions = new SparkCommerceOptions();
        commerceOptions.Channels["tickets"] = new CommerceChannelOptions { Allocation = AllocationMode.HoldFromSelection, MaxPaymentWindow = TimeSpan.FromMinutes(30) };
        commerceOptions.Channels["box"] = new CommerceChannelOptions { Allocation = AllocationMode.HoldFromCheckout, MaxPaymentWindow = TimeSpan.FromMinutes(20) };
        commerceOptions.Channels["shop"] = new CommerceChannelOptions { Allocation = AllocationMode.OnPayment, MaxPaymentWindow = TimeSpan.FromHours(1) };
        commerce?.Invoke(commerceOptions);
        var checkoutOptions = new SparkCommerceCheckoutOptions();
        checkout?.Invoke(checkoutOptions);

        var inventory = new InventoryEngine(store, options: new InventoryEngineOptions { Grace = TimeSpan.FromSeconds(1) }, clock: clock);
        var buyer = new TestBuyer();
        var catalog = new TestCapacitySource();
        var payments = new FakePayments();
        var legal = new TestLegalDocuments();
        var events = new RecordingOrderEvents();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IOptionsMonitor<SparkCommerceOptions>>(new StaticOptionsMonitor<SparkCommerceOptions>(commerceOptions));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(commerceOptions));
        services.AddSingleton<IOptionsMonitor<SparkCommerceCheckoutOptions>>(new StaticOptionsMonitor<SparkCommerceCheckoutOptions>(checkoutOptions));
        services.AddSingleton<IOptionsMonitor<SparkCommerceInventoryOptions>>(new StaticOptionsMonitor<SparkCommerceInventoryOptions>(new SparkCommerceInventoryOptions { Grace = TimeSpan.FromSeconds(1) }));
        services.AddSingleton<ISparkInventory>(inventory);
        services.AddSingleton<ISparkPayments>(payments);
        services.AddSingleton<ISparkSellers>(new OpenSellers());
        services.AddSingleton<ISparkLegalDocuments>(legal);
        services.AddSingleton<ICommerceAudit>(new NoAudit());
        services.AddSingleton<IFinalPriceCalculator>(sp => new FinalPriceCalculator(sp.GetRequiredService<IOptionsMonitor<SparkCommerceOptions>>()));
        services.AddSingleton<ICheckoutBuyer>(buyer);
        services.AddSingleton<ICheckoutCapacitySource>(catalog);
        services.AddSingleton<IOrderEventRecipient>(events);
        services.AddSingleton(new TestInsuranceOptions());
        services.AddSingleton<IInsuranceProvider, TestInsuranceProvider>();
        services.AddScoped<BasketStore>();
        services.AddScoped<CheckoutItems>();
        services.AddScoped<CheckoutAdmission>();
        services.AddScoped<CheckoutPricing>();
        services.AddScoped<CheckoutInsurance>();
        services.AddScoped<OrderEvents>();
        services.AddScoped<CheckoutMails>();
        services.AddScoped<OrderLifecycle>();
        services.AddScoped<ISparkCheckout, CheckoutService>();
        services.AddScoped<ICheckoutPricingStep, DonationPricingStep>();
        services.AddScoped<ICheckoutPricingStep, InsurancePricingStep>();
        configure?.Invoke(services, payments, inventory); // M9: the refund flows' services (RefundTestSupport)

        return new CheckoutHost
        {
            Services = services.BuildServiceProvider(), Clock = clock, Buyer = buyer, Catalog = catalog, Payments = payments, Legal = legal,
            Events = events, Inventory = inventory, Commerce = commerceOptions, Options = checkoutOptions,
        };
    }

    /// <summary>A sellable item; <paramref name="quota"/> maps it 1:1 onto a quota of <paramref name="capacity"/>.</summary>
    public async Task<CheckoutItem> ItemAsync(string itemId, string channel, decimal price, int? capacity = 10, string? seller = null, int? minimumAge = null,
        int? perBuyerLimit = null, string? admissionScope = null, IReadOnlyList<string>? parents = null, IReadOnlyList<string>? admissionScopes = null)
    {
        if (capacity is { } c)
        {
            await Inventory.CreateQuotaAsync("quotas/" + itemId, c, 1);
            await Inventory.MapItemAsync(itemId, [new ItemQuota("quotas/" + itemId)]);
        }
        var item = new CheckoutItem
        {
            ItemId = itemId, Channel = channel, OrganizationId = seller, Price = new Money(price, "EUR"),
            Title = TranslatedString.Of("en", itemId), MinimumAge = minimumAge, PerBuyerLimit = perBuyerLimit,
            AdmissionScopes = admissionScopes ?? (admissionScope is null ? [] : [admissionScope]),
            RequiresParentItemIds = parents ?? [],
            Capacity = capacity is null ? new CheckoutCapacity() : new CheckoutCapacity { Items = [new ItemLine(itemId, 1)] },
        };
        Catalog.Items[itemId] = item;
        return item;
    }

    public Task<int> AvailableAsync(string itemId) => Inventory.GetAvailabilityAsync("quotas/" + itemId).ContinueWith(t => t.Result.Available);

    /// <summary>Places the basket accepting the terms in force.</summary>
    public Task<CheckoutContracts.SparkPlaceOrderResult> PlaceAsync(int? age = null, string? method = null, string? seller = null, string email = "buyer@example.org")
        => Checkout.PlaceOrderAsync(new CheckoutContracts.SparkPlaceOrderRequest([Legal.TermsFor(seller).Id], age, email, "Test Buyer", Method: method));
}
