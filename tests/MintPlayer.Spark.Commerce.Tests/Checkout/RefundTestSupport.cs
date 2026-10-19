using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Payments.Models;
using MintPlayer.Spark.Commerce.Tests.Payments;
using MintPlayer.Spark.Organizations;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>
/// A provider as the refund flows see it (M9, S15): a refund reference is executed once (the same reference again answers
/// the first refund, as ISparkPayments promises), every call is counted, a crash can be injected after the provider
/// executed a refund (the process dies before checkout saves), and a seller's balance can be short (D20).
/// </summary>
internal sealed class ProviderPayments(FakePayments inner) : ISparkPayments
{
    public Dictionary<string, ProviderPaymentRefund> Executed { get; } = new(StringComparer.Ordinal);
    public int Calls { get; private set; }

    /// <summary>Throw after executing the N-th call (1-based) of this run; null never.</summary>
    public int? CrashAfterCall { get; set; }

    /// <summary>Answer these references "failed: insufficient balance" (without executing them).</summary>
    public HashSet<string> ShortOfBalance { get; } = new(StringComparer.Ordinal);

    /// <summary>Answer Succeeded at once instead of Pending.</summary>
    public bool SucceedAtOnce { get; set; }

    public Task<ProviderPaymentRefund> RefundAsync(string paymentId, Money amount, string reference, bool refundApplicationFee = true, int attempt = 0, CancellationToken cancellationToken = default)
    {
        Calls++;
        ProviderPaymentRefund refund;
        if (ShortOfBalance.Contains(reference))
            refund = new ProviderPaymentRefund { Reference = reference, Amount = amount, Status = ProviderRefundStatus.Failed, FailureReason = RefundFailureReason.InsufficientBalance };
        else if (!Executed.TryGetValue(reference, out refund!))
        {
            refund = new ProviderPaymentRefund { Reference = reference, Amount = amount, Status = SucceedAtOnce ? ProviderRefundStatus.Succeeded : ProviderRefundStatus.Pending };
            Executed[reference] = refund;
            inner.Refunds.Add((paymentId, amount, reference));
        }
        if (CrashAfterCall is { } crash && Calls == crash)
            throw new InvalidOperationException("Simulated crash after the provider answered (the process dies before checkout saves).");
        return Task.FromResult(refund);
    }

    public string DefaultProvider => inner.DefaultProvider;
    public IReadOnlyList<PaymentMethodInfo> GetMethods(string? provider = null) => inner.GetMethods(provider);
    public IReadOnlyList<PaymentMethodInfo> GetOfferableMethods(PaymentMethodQuery query, string? provider = null) => inner.GetOfferableMethods(query, provider);
    public Task<ProviderPayment> CreatePaymentAsync(PaymentCreation creation, CancellationToken cancellationToken = default) => inner.CreatePaymentAsync(creation, cancellationToken);
    public Task<ProviderPayment?> GetPaymentAsync(string paymentId, bool refresh = false, CancellationToken cancellationToken = default) => inner.GetPaymentAsync(paymentId, refresh, cancellationToken);
    public Task<ProviderPayment> CancelAsync(string paymentId, CancellationToken cancellationToken = default) => inner.CancelAsync(paymentId, cancellationToken);
    public Task<ProviderPayment> CaptureAsync(string paymentId, Money? amount = null, CancellationToken cancellationToken = default) => inner.CaptureAsync(paymentId, amount, cancellationToken);
    public Task<ProviderPayment> VoidAsync(string paymentId, CancellationToken cancellationToken = default) => inner.VoidAsync(paymentId, cancellationToken);
    public Task ProcessEventsAsync(IReadOnlyList<PaymentProviderEvent> events, CancellationToken cancellationToken = default) => inner.ProcessEventsAsync(events, cancellationToken);
    public Task<DisputeEvidencePack> AssembleEvidenceAsync(string disputeId, CancellationToken cancellationToken = default) => inner.AssembleEvidenceAsync(disputeId, cancellationToken);
    public Task<DisputeEvidenceSubmission> SubmitEvidenceAsync(string disputeId, string actingUserId, CancellationToken cancellationToken = default) => inner.SubmitEvidenceAsync(disputeId, actingUserId, cancellationToken);
    public Task<PaymentDispute> AcceptDisputeAsync(string disputeId, string actingUserId, CancellationToken cancellationToken = default) => inner.AcceptDisputeAsync(disputeId, actingUserId, cancellationToken);

    /// <summary>The provider reports a refund paid out (the webhook's notice).</summary>
    public RefundChangeNotice Paid(string reference)
    {
        var refund = Executed[reference];
        refund.Status = ProviderRefundStatus.Succeeded;
        return new RefundChangeNotice(new ProviderPayment { Id = "ProviderPayments/x", Reference = reference.Split(":refund:")[0] }, refund, ProviderRefundStatus.Pending);
    }
}

/// <summary>Seller policies and withdrawal terms by item (D20, GA1).</summary>
internal sealed class TestRefundTerms : IRefundTermsSource
{
    public Dictionary<string, RefundTerms> Terms { get; } = new(StringComparer.Ordinal);

    public bool Handles(string itemId) => Terms.ContainsKey(itemId);

    public Task<RefundTerms?> ResolveAsync(string itemId, string channel, CancellationToken cancellationToken)
        => Task.FromResult(Terms.TryGetValue(itemId, out var terms) ? terms : null);
}

/// <summary>A delivery extra on every order with a physical line: 6.95 charged, of which 4.95 is the standard method (CRD Art. 13(2)).</summary>
internal sealed class TestDeliveryStep : ICheckoutPricingStep, IStandardDeliveryCost
{
    public int Order => 500;

    public Task ApplyAsync(CheckoutPricingContext context, CancellationToken cancellationToken)
    {
        if (context.Lines.Any(l => l.Item.IsPhysicalGoods))
            context.Extras.Add(new PricedExtra("delivery", PricedExtraKind.Other, new Money(6.95m, context.Currency), "delivery", "test"));
        return Task.CompletedTask;
    }

    public Task<Money?> GetStandardAsync(CheckoutOrder order, CheckoutOrderExtra extra, CancellationToken cancellationToken)
        => Task.FromResult<Money?>(extra.Code == "delivery" ? new Money(4.95m, order.Currency) : null);
}

/// <summary>An interface whose every call answers its default (async: a completed task of the default).</summary>
internal class NullService<T> : DispatchProxy where T : class
{
    public static T Create() => DispatchProxy.Create<T, NullService<T>>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var type = targetMethod!.ReturnType;
        if (type == typeof(Task))
            return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = type.GetGenericArguments()[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : EmptyOf(inner);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
        }
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }

    private static object? EmptyOf(Type type)
        => type.IsGenericType && type.GetGenericArguments().Length == 1 && type.IsInterface
            ? Array.CreateInstance(type.GetGenericArguments()[0], 0)
            : null;
}

internal sealed class NobodySignedIn : ISparkCurrentUser
{
    public string? Id => null;
    public bool IsAuthenticated => false;
}

/// <summary>Checkout with the refund flows (M9) over RavenDB and the real inventory engine.</summary>
internal static class RefundHost
{
    public static (CheckoutHost Host, ProviderPayments Provider, TestRefundTerms Terms) Build(IDocumentStore store, Action<SparkCommerceCheckoutOptions>? checkout = null,
        Action<SparkCommerceOptions>? commerce = null)
    {
        ProviderPayments? provider = null;
        var terms = new TestRefundTerms();
        var host = CheckoutHost.Build(store, commerce, o =>
        {
            o.Refunds.MassRefunds.MaxRefundsPerSecond = 0; // no throttling in tests
            o.Refunds.MassRefunds.ChunkSize = 5;
            o.Refunds.MassRefunds.BatchSize = 100;
            checkout?.Invoke(o);
        }, (services, payments, inventory) =>
        {
            provider = new ProviderPayments(payments);
            services.AddSingleton<ISparkPayments>(provider);
            services.AddSingleton<IRefundTermsSource>(terms);
            var delivery = new TestDeliveryStep();
            services.AddSingleton<ICheckoutPricingStep>(delivery);
            services.AddSingleton<IStandardDeliveryCost>(delivery);
            services.AddSingleton<ISparkCurrentUser>(new NobodySignedIn());
            services.AddSingleton(NullService<ISparkOrganizations>.Create());
            services.AddSingleton<IOptionsMonitor<SparkCommercePaymentsOptions>>(new StaticOptionsMonitor<SparkCommercePaymentsOptions>(new SparkCommercePaymentsOptions()));
            services.AddDataProtection();
            services.AddScoped<OrderAccess>();
            services.AddScoped<OrderLinks>();
            services.AddScoped<RefundEngine>();
            services.AddScoped<RefundMails>();
            services.AddScoped<RefundService>();
            services.AddScoped<MassRefunds>();
            services.AddScoped<IOrderEventRecipient, RefundTermsRecipient>();
            services.AddScoped<RefundPaymentRecipient>();
        });
        return (host, provider!, terms);
    }

    /// <summary>Places and pays an order of <paramref name="lines"/> for a fresh guest; answers it confirmed.</summary>
    public static async Task<CheckoutOrder> PaidOrderAsync(CheckoutHost host, string visitor, params (string ItemId, int Quantity)[] lines)
    {
        host.Buyer.UserId = null;
        host.Buyer.VisitorId = visitor;
        foreach (var (itemId, quantity) in lines)
            await host.Checkout.AddLineAsync(new Spark.Commerce.Checkout.Contracts.SparkBasketAddRequest(itemId, quantity));
        var placed = await host.PlaceAsync(email: visitor + "@example.org");
        var order = await host.Checkout.GetOwnOrderAsync(placed.Number) ?? throw new InvalidOperationException("Order not found.");
        await host.Lifecycle.OnPaymentAsync(host.Payments.Set(order.Payment!.PaymentId!, ProviderPaymentStatus.Paid), default);
        return (await host.Checkout.GetOwnOrderAsync(placed.Number))!;
    }

    public static async Task<CheckoutRefundLedger> LedgerAsync(IDocumentStore store, string orderId)
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<CheckoutRefundLedger>(CheckoutRefundLedger.IdFor(orderId)))!;
    }

    public static async Task<CheckoutOrder> OrderAsync(IDocumentStore store, string orderId)
    {
        using var session = store.OpenAsyncSession();
        return (await session.LoadAsync<CheckoutOrder>(orderId))!;
    }

    /// <summary>Marks an item physical goods (the test catalog's items are services by default).</summary>
    public static void Goods(CheckoutHost host, string itemId) => host.Catalog.Items[itemId] = host.Catalog.Items[itemId] with { IsPhysicalGoods = true };

    /// <summary>Gives an item a service date (a dated leisure service: no withdrawal right, CRD Art. 16(l)).</summary>
    public static void Dated(CheckoutHost host, string itemId, DateTimeOffset date) => host.Catalog.Items[itemId] = host.Catalog.Items[itemId] with { ServiceDate = date };
}
