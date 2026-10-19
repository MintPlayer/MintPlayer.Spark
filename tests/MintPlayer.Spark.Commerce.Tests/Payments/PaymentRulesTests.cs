using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Payments.Services;

namespace MintPlayer.Spark.Commerce.Tests.Payments;

/// <summary>The rules of the payments package that need no database: D3's cap, GA11, idempotency keys, the platform fee.</summary>
public class PaymentRulesTests
{
    private static PaymentMethodInfo Method(string id, TimeSpan? expiry, bool onDemand = false, bool delayed = false, bool auth = false, bool split = false)
        => new("p", id, id, expiry, onDemand, delayed, auth, FingerprintTiming.None, split);

    [Fact]
    public void D3_offers_in_a_held_channel_only_what_cannot_outlive_the_window()
    {
        var methods = new[]
        {
            Method("ideal", TimeSpan.FromMinutes(15)),
            Method("paypal", TimeSpan.FromHours(6)),
            Method("stripe-card", TimeSpan.FromHours(24), onDemand: true),
            Method("transfer", TimeSpan.FromDays(12), delayed: true),
            Method("sepa", TimeSpan.FromHours(24), onDemand: true, delayed: true),
        };

        var held = PaymentMethodCatalog.Offerable(methods, new PaymentMethodQuery { Held = true, MaxPaymentWindow = TimeSpan.FromMinutes(30) });

        held.Select(m => m.Id).Should().Equal("ideal", "stripe-card");
        PaymentMethodCatalog.Offerable(methods, new PaymentMethodQuery()).Should().HaveCount(5);
    }

    [Fact]
    public void D3_extends_PaymentPending_to_the_method_expiry_plus_grace_capped_by_the_window()
    {
        PaymentMethodCatalog.PaymentPendingWindow(Method("ideal", TimeSpan.FromMinutes(15)), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2))
            .Should().Be(TimeSpan.FromMinutes(17));
        PaymentMethodCatalog.PaymentPendingWindow(Method("bancontact", TimeSpan.FromHours(1)), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2))
            .Should().Be(TimeSpan.FromMinutes(30));
        PaymentMethodCatalog.PaymentPendingWindow(Method("card", TimeSpan.FromHours(24), onDemand: true), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(2))
            .Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Authorization_and_split_requirements_filter_the_catalog()
    {
        var methods = new[] { Method("card", TimeSpan.FromMinutes(30), auth: true, split: true), Method("ideal", TimeSpan.FromMinutes(15)) };

        PaymentMethodCatalog.Offerable(methods, new PaymentMethodQuery { RequireAuthorization = true }).Select(m => m.Id).Should().Equal("card");
        PaymentMethodCatalog.Offerable(methods, new PaymentMethodQuery { RequireSplit = true }).Select(m => m.Id).Should().Equal("card");
        PaymentMethodCatalog.Offerable(methods, new PaymentMethodQuery { Excluded = ["card"] }).Select(m => m.Id).Should().Equal("ideal");
    }

    /// <summary>
    /// GA11 (Regulation (EU) 2018/302 Art. 5): no method may be hidden, and no payment refused, because of the card's
    /// issuing country, residence or nationality. Guarded structurally: nothing on the payments surface takes a country.
    /// </summary>
    [Fact]
    public void GA11_no_payments_api_takes_a_country()
    {
        var surface = new[]
        {
            typeof(IPaymentProvider), typeof(ISparkPayments), typeof(PaymentMethodCatalog), typeof(PaymentMethodQuery),
            typeof(CreatePaymentRequest), typeof(PaymentCreation),
        };
        string[] forbidden = ["country", "nationality", "residence", "issuer", "bin"];

        var offenders = surface.SelectMany(type =>
                type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).SelectMany(m => m.GetParameters()).Select(p => $"{type.Name}: parameter {p.Name}")
                    .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => $"{type.Name}: property {p.Name}")))
            .Where(name => forbidden.Any(f => name.Split(' ').Last().Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void Idempotency_keys_are_derived_never_random_and_never_carry_the_reference()
    {
        var first = PaymentIdempotencyKeys.For(PaymentIdempotencyKeys.Operations.Refund, "orders/12345|credit-notes/1");
        var again = PaymentIdempotencyKeys.For(PaymentIdempotencyKeys.Operations.Refund, "orders/12345|credit-notes/1");
        var retry = PaymentIdempotencyKeys.For(PaymentIdempotencyKeys.Operations.Refund, "orders/12345|credit-notes/1", attempt: 1);

        again.Should().Be(first);
        retry.Should().NotBe(first);
        first.Should().StartWith("spark-refund-");
        first.Should().NotContain("12345");
        first.Length.Should().BeLessThan(255);
        FluentThrow(() => PaymentIdempotencyKeys.For("Bad Op", "x"));
    }

    private static void FluentThrow(Action action) => action.Should().Throw<ArgumentException>();

    [Fact]
    public void The_platform_fee_is_rounded_once_and_never_exceeds_the_amount()
    {
        var fee = new PlatformFeeOptions { PercentBasisPoints = 500, Fixed = { ["EUR"] = 0.25m } };

        fee.For(new Money(19.99m, "EUR")).Should().Be(new Money(1.25m, "EUR")); // 0.9995 -> 1.00, + 0.25
        fee.For(new Money(0.10m, "EUR")).Should().Be(new Money(0.10m, "EUR"));  // capped at the amount
        (new PlatformFeeOptions().For(new Money(10m, "EUR")) is null).Should().BeTrue();
    }

    [Fact]
    public void A_payment_never_moves_back_from_paid_and_a_late_payment_still_lands()
    {
        PaymentEventProcessor.CanMove(ProviderPaymentStatus.Open, ProviderPaymentStatus.Paid).Should().BeTrue();
        PaymentEventProcessor.CanMove(ProviderPaymentStatus.Paid, ProviderPaymentStatus.Pending).Should().BeFalse();
        PaymentEventProcessor.CanMove(ProviderPaymentStatus.Paid, ProviderPaymentStatus.Failed).Should().BeFalse();
        PaymentEventProcessor.CanMove(ProviderPaymentStatus.Authorized, ProviderPaymentStatus.Pending).Should().BeFalse();
        PaymentEventProcessor.CanMove(ProviderPaymentStatus.Expired, ProviderPaymentStatus.Paid).Should().BeTrue();
        PaymentEventProcessor.CanMove(ProviderPaymentStatus.Expired, ProviderPaymentStatus.Failed).Should().BeFalse();
    }

    [Fact]
    public void Webhook_sources_name_their_provider()
    {
        PaymentWebhookSources.For("mollie").Should().Be("payments-mollie");
        PaymentWebhookSources.ProviderOf("payments-stripe-connect").Should().Be("stripe");
        PaymentWebhookSources.ProviderOf("payments-test").Should().Be("test");
        PaymentWebhookSources.ProviderOf("github").Should().BeNull();
    }

    [Fact]
    public void A_merchant_context_never_prints_its_token()
    {
        var merchant = MerchantContext.Connected("mollie", "Organizations/s", "org_1", accessToken: "access_secret_value");

        merchant.ToString().Should().NotContain("access_secret_value");
    }

}
