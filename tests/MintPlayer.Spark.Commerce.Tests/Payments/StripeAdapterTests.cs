using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Payments.Stripe;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Tests.Payments;

/// <summary>
/// Spike S5/S6 for Stripe, fixture-based (round 4): the adapter against doc-derived events and responses
/// (Fixtures/Stripe), the held-flow shape (Checkout Session with a clamped expiry and an active expire), Connect direct
/// charges, refunds from a seller's balance, disputes and payouts.
/// </summary>
public class StripeAdapterTests
{
    private const string Stripe = "Stripe";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1792400400);

    private static (StripePaymentProvider Provider, FakeProviderHandler Handler) Create(Action<FakeProviderHandler>? routes = null, Action<StripePaymentsOptions>? configure = null)
    {
        var handler = new FakeProviderHandler();
        routes?.Invoke(handler);
        var options = new StripePaymentsOptions { SecretKey = "sk_test_placeholder", ApiVersion = "2025-09-30.clover" };
        configure?.Invoke(options);
        var api = new StripeApi(new FakeHttpClientFactory(handler), new StaticOptionsMonitor<StripePaymentsOptions>(options));
        return (new StripePaymentProvider(api, new ManualTimeProvider(Now), NullLogger<StripePaymentProvider>.Instance), handler);
    }

    [Fact]
    public void A_signed_fixture_verifies_and_a_tampered_one_does_not()
    {
        var body = Encoding.UTF8.GetBytes(Fixtures.Text(Stripe, "event-checkout-session-completed.json"));
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(1792400700);
        var header = StripeSignatureStrategy.ComputeHeader("whsec_test_placeholder", signedAt, body);
        var strategy = new StripeSignatureStrategy("whsec_test_placeholder", timeProvider: new ManualTimeProvider(signedAt.AddSeconds(30)));

        strategy.Verify(new WebhookRequest([new(StripeSignatureStrategy.HeaderName, new StringValues(header))], body)).IsAccepted.Should().BeTrue();
        var tampered = Encoding.UTF8.GetBytes(Fixtures.Text(Stripe, "event-checkout-session-completed.json").Replace("7500", "1"));
        strategy.Verify(new WebhookRequest([new(StripeSignatureStrategy.HeaderName, new StringValues(header))], tampered)).IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void A_completed_session_is_paid_and_names_its_PaymentIntent()
    {
        var events = StripePaymentProvider.EventsOf(Fixtures.Json(Stripe, "event-checkout-session-completed.json"));

        var changed = events.OfType<PaymentChangedEvent>().Should().ContainSingle().Which;
        changed.EventKey.Should().Be("evt_1SessionCompleted");
        changed.Payment.ProviderPaymentId.Should().Be("cs_test_a1b2c3");
        changed.Payment.Status.Should().Be(ProviderPaymentStatus.Paid);
        changed.Payment.Amount.Should().Be(new Money(75.00m, "EUR"));
        changed.Payment.RelatedIds.Should().Contain("pi_3Qtest777");
        changed.Payment.Reference.Should().Be("orders/777");
    }

    [Fact]
    public void A_capturable_PaymentIntent_is_authorized_and_carries_our_reference()
    {
        var payment = StripePaymentProvider.EventsOf(Fixtures.Json(Stripe, "event-payment-intent-capturable.json")).OfType<PaymentChangedEvent>().Single().Payment;

        payment.Status.Should().Be(ProviderPaymentStatus.Authorized);
        payment.ProviderPaymentId.Should().Be("pi_3Qtest888");
        payment.Reference.Should().Be("orders/888");
        payment.RelatedIds.Should().Contain("ch_3Qtest888");
    }

    [Fact]
    public void A_charge_gives_the_fingerprint_before_confirmation_and_the_application_fee_id()
    {
        var payment = StripePaymentProvider.EventsOf(Fixtures.Json(Stripe, "event-charge-succeeded.json")).OfType<PaymentChangedEvent>().Single().Payment;

        payment.Status.Should().Be(ProviderPaymentStatus.Paid);
        payment.ProviderPaymentId.Should().Be("pi_3Qtest777");
        payment.PayerFingerprint.Should().Be("Xt5EWLLDS7FJjR1c");
        payment.Prepaid.Should().Be(false);
        payment.ApplicationFee.Should().Be(new Money(3.75m, "EUR"));
        // The fee_ id is what the platform's application-fee settlement line names (GB4 matching).
        payment.RelatedIds.Should().Contain("fee_1Platform777");
        payment.RelatedIds.Should().Contain("ch_3Qtest777");
    }

    [Fact]
    public void A_refund_that_failed_for_insufficient_funds_is_an_insufficient_balance_failure()
    {
        var refund = StripePaymentProvider.EventsOf(Fixtures.Json(Stripe, "event-refund-failed.json")).OfType<RefundChangedEvent>().Single().Refund;

        refund.Status.Should().Be(ProviderRefundStatus.Failed);
        refund.FailureReason.Should().Be(RefundFailureReason.InsufficientBalance);
        refund.ProviderPaymentId.Should().Be("pi_3Qtest777");
        refund.Reference.Should().Be("credit-notes/9");
    }

    [Fact]
    public async Task A_refund_Stripe_refuses_with_balance_insufficient_is_recorded_as_failed()
    {
        var (provider, handler) = Create(h => h.On("POST", "/v1/refunds", Fixtures.Text(Stripe, "error-balance-insufficient.json"), HttpStatusCode.BadRequest));
        var seller = MerchantContext.Connected("stripe", "Organizations/seller", "acct_1Seller");

        var refund = await provider.RefundAsync(new RefundRequest
        {
            Merchant = seller,
            ProviderPaymentId = "pi_3Qtest777",
            Amount = new Money(25.00m, "EUR"),
            Reference = "credit-notes/9",
            IdempotencyKey = "spark-refund-z-0",
            RefundApplicationFee = true,
        }, CancellationToken.None);

        refund.FailureReason.Should().Be(RefundFailureReason.InsufficientBalance);
        var request = handler.Requests.Single();
        // A direct charge: the refund comes from the seller's own balance.
        request.Headers["Stripe-Account"].Should().Be("acct_1Seller");
        request.Headers["Idempotency-Key"].Should().Be("spark-refund-z-0");
        request.Body.Should().Contain("refund_application_fee=true");
    }

    [Fact]
    public void A_dispute_records_the_debit_and_its_fee_and_closes_won_with_the_reinstatement()
    {
        var opened = StripePaymentProvider.EventsOf(Fixtures.Json(Stripe, "event-dispute-created.json")).OfType<DisputeChangedEvent>().Single().Dispute;
        opened.Status.Should().Be(DisputeStatus.NeedsResponse);
        opened.FundsWithdrawn.Should().BeTrue();
        opened.Fee.Should().Be(new Money(15.00m, "EUR"));
        opened.EvidenceDueBy.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1793604800));
        opened.ProviderPaymentId.Should().Be("pi_3Qtest777");

        var closed = StripePaymentProvider.EventsOf(Fixtures.Json(Stripe, "event-dispute-closed-won.json")).OfType<DisputeChangedEvent>().Single().Dispute;
        closed.Status.Should().Be(DisputeStatus.Won);
        closed.FundsReinstated.Should().BeTrue();
    }

    [Fact]
    public void An_account_with_past_due_requirements_is_pending_and_a_complete_one_verified()
    {
        var pending = StripePaymentProvider.EventsOf(Fixtures.Json(Stripe, "event-account-updated.json")).OfType<ConnectedAccountChangedEvent>().Single().Account;
        pending.Kyc.Should().Be(KycStatus.Pending);
        pending.Requirements.Should().BeEquivalentTo(["individual.verification.document", "external_account"]);
        pending.DisabledReason.Should().Be("requirements.past_due");

        StripePaymentProvider.ReadAccount(Fixtures.Json(Stripe, "account-verified.json")).Kyc.Should().Be(KycStatus.Verified);
    }

    [Fact]
    public async Task A_held_payment_is_a_Checkout_Session_whose_expiry_is_clamped_to_Stripes_minimum()
    {
        var (provider, handler) = Create(h => h.On("POST", "/v1/checkout/sessions", Fixtures.Text(Stripe, "checkout-session-created.json")));
        var seller = MerchantContext.Connected("stripe", "Organizations/seller", "acct_1Seller");

        var snapshot = await provider.CreatePaymentAsync(new CreatePaymentRequest
        {
            Merchant = seller,
            Reference = "orders/777",
            Amount = new Money(75.00m, "EUR"),
            Description = "Festival ticket",
            AllowedMethods = ["card", "bancontact"],
            // A 10-minute hold: shorter than Stripe's 30-minute minimum.
            ExpiresAt = Now.AddMinutes(10),
            RedirectUrl = "https://shop.example.org/orders/777",
            ApplicationFee = new Money(3.75m, "EUR"),
            IdempotencyKey = "spark-create-q-0",
            Locale = "nl-BE",
            Metadata = new Dictionary<string, string> { ["spark_reference"] = "orders/777" },
        }, CancellationToken.None);

        snapshot.Status.Should().Be(ProviderPaymentStatus.Open);
        snapshot.CheckoutUrl.Should().Be("https://checkout.stripe.com/c/pay/cs_test_a1b2c3");
        var form = handler.Requests.Single().Body!;
        form.Should().Contain("expires_at=" + Now.AddMinutes(31).ToUnixTimeSeconds());
        form.Should().Contain("payment_intent_data%5Bapplication_fee_amount%5D=375");
        form.Should().Contain("payment_method_types%5B1%5D=bancontact");
        form.Should().Contain("locale=nl");
        handler.Requests.Single().Headers["Stripe-Account"].Should().Be("acct_1Seller");
        handler.Requests.Single().Headers["Stripe-Version"].Should().Be("2025-09-30.clover");
    }

    [Fact]
    public void The_expiry_clamp_keeps_within_30_minutes_and_24_hours()
    {
        StripePaymentProvider.ClampExpiry(Now.AddMinutes(5), Now).Should().Be(Now.AddMinutes(31));
        StripePaymentProvider.ClampExpiry(Now.AddHours(2), Now).Should().Be(Now.AddHours(2));
        StripePaymentProvider.ClampExpiry(Now.AddDays(3), Now).Should().Be(Now.AddHours(24).AddMinutes(-1));
    }

    [Fact]
    public void The_catalog_offers_immediate_methods_in_held_channels_and_never_delayed_ones()
    {
        var (provider, _) = Create();

        var held = PaymentMethodCatalog.Offerable(provider.Methods, new PaymentMethodQuery { Held = true, MaxPaymentWindow = TimeSpan.FromMinutes(10) });

        held.Select(m => m.Id).Should().Contain("card");
        held.Select(m => m.Id).Should().Contain("ideal");
        held.Select(m => m.Id).Should().NotContain("sepadebit");
        held.Select(m => m.Id).Should().NotContain("banktransfer");
        provider.Methods.Single(m => m.Id == "card").Fingerprint.Should().Be(FingerprintTiming.BeforeConfirm);
    }

    [Fact]
    public void Split_capability_follows_the_separate_charges_and_transfers_setting()
    {
        Create().Provider.Capabilities.SupportsSplit.Should().BeFalse();
        var (withSplit, _) = Create(configure: o => o.SeparateChargesAndTransfers = true);
        withSplit.Capabilities.SupportsSplit.Should().BeTrue();
        withSplit.Methods.Single(m => m.Id == "card").SupportsSplit.Should().BeTrue();
    }

    [Fact]
    public async Task Payouts_are_read_with_their_balance_transactions()
    {
        var handler = new FakeProviderHandler()
            .On("GET", "/v1/payouts", Fixtures.Text(Stripe, "payouts.json"))
            .On("GET", "/v1/balance_transactions", Fixtures.Text(Stripe, "balance-transactions.json"));
        var api = new StripeApi(new FakeHttpClientFactory(handler), new StaticOptionsMonitor<StripePaymentsOptions>(new StripePaymentsOptions { SecretKey = "sk_test_placeholder" }));
        var source = new StripeSettlementSource(api);

        var settlements = new List<ProviderSettlement>();
        await foreach (var settlement in source.GetSettlementsAsync(MerchantContext.Platform("stripe"), Now.AddDays(-30), CancellationToken.None))
            settlements.Add(settlement);

        var payout = settlements.Should().ContainSingle().Which;
        payout.SettlementId.Should().Be("po_1QtestPayout");
        payout.Lines.Single(l => l.LineId == "txn_1Charge").Kind.Should().Be(SettlementLineKind.Payment);
        payout.Lines.Single(l => l.LineId == "txn_1Charge").Fee.Should().Be(new Money(1.58m, "EUR"));
        payout.Lines.Single(l => l.LineId == "txn_1Refund").Kind.Should().Be(SettlementLineKind.Refund);
        payout.Lines.Single(l => l.LineId == "txn_1StripeFee").Kind.Should().Be(SettlementLineKind.ProviderFee);
        payout.Lines.Single(l => l.LineId == "txn_1Payout").Kind.Should().Be(SettlementLineKind.Payout);
        handler.Requests.First().Uri.Query.Should().Contain("status=paid");
    }

    [Fact]
    public async Task Connect_creates_a_connected_account_then_a_fresh_account_link()
    {
        var handler = new FakeProviderHandler()
            .On("POST", "/v1/accounts", Fixtures.Text(Stripe, "account-verified.json"))
            .On("POST", "/v1/account_links", Fixtures.Text(Stripe, "account-link.json"));
        var api = new StripeApi(new FakeHttpClientFactory(handler), new StaticOptionsMonitor<StripePaymentsOptions>(new StripePaymentsOptions { SecretKey = "sk_test_placeholder" }));
        var connect = new StripeConnect(api);

        var start = await connect.StartAsync("Organizations/seller", null, "https://shop.example.org/cb?state=s", "https://shop.example.org/cb?state=s&refresh=1", "s", CancellationToken.None);

        start.AccountId.Should().Be("acct_1Seller");
        start.RedirectUrl.Should().Be("https://connect.stripe.com/setup/s/acct_1Seller/placeholder");
        handler.Requests[0].Body.Should().Contain("type=standard");
        handler.Requests[0].Headers.ContainsKey("Idempotency-Key").Should().BeTrue();
        handler.Requests[1].Body.Should().Contain("type=account_onboarding");
        // An Account Link is single-use: no idempotency key that could replay a stale one.
        handler.Requests[1].Headers.ContainsKey("Idempotency-Key").Should().BeFalse();
    }
}
