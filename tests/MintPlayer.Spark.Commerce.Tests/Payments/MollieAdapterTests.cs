using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Payments.Mollie;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Tests.Payments;

/// <summary>
/// Spike S5/S6 for Mollie, fixture-based (round 4): the adapter against doc-derived responses (Fixtures/Mollie). No
/// network: a fake handler serves the fixtures and records what the adapter sent.
/// </summary>
public class MollieAdapterTests
{
    private const string Mollie = "Mollie";

    private static (MolliePaymentProvider Provider, FakeProviderHandler Handler, MolliePaymentsOptions Options) Create(Action<FakeProviderHandler>? routes = null)
    {
        var handler = new FakeProviderHandler();
        routes?.Invoke(handler);
        var options = new MolliePaymentsOptions { ApiKey = "test_placeholder", ClientId = "app_test", ClientSecret = "secret_placeholder" };
        var api = new MollieApi(new FakeHttpClientFactory(handler), new StaticOptionsMonitor<MolliePaymentsOptions>(options));
        return (new MolliePaymentProvider(api, NullLogger<MolliePaymentProvider>.Instance), handler, options);
    }

    [Fact]
    public void An_open_payment_reads_its_checkout_url_expiry_and_reference()
    {
        var snapshot = MolliePaymentProvider.ReadPayment(Fixtures.Json(Mollie, "payment-open.json"));

        snapshot.ProviderPaymentId.Should().Be("tr_7UhSN1zuXS");
        snapshot.Status.Should().Be(ProviderPaymentStatus.Open);
        snapshot.Amount.Should().Be(new Money(10.00m, "EUR"));
        snapshot.CheckoutUrl.Should().Be("https://www.mollie.com/checkout/select-method/7UhSN1zuXS");
        snapshot.ExpiresAt.Should().Be(DateTimeOffset.Parse("2026-10-19T09:28:37+00:00"));
        snapshot.Reference.Should().Be("orders/12345");
        snapshot.Method.Should().Be("ideal");
    }

    [Fact]
    public void A_paid_card_payment_carries_the_fingerprint_after_payment_and_the_prepaid_flag()
    {
        var snapshot = MolliePaymentProvider.ReadPayment(Fixtures.Json(Mollie, "payment-paid-card-refunded.json"));

        snapshot.Status.Should().Be(ProviderPaymentStatus.Paid);
        snapshot.Method.Should().Be("card");
        // Research §4.5: Mollie's fingerprint is known only after payment (FingerprintTiming.AfterPayment).
        snapshot.PayerFingerprint.Should().Be("8f8d1c4b4f6a0b0f5a9e");
        snapshot.Prepaid.Should().Be(true);
        snapshot.ApplicationFee.Should().Be(new Money(3.75m, "EUR"));
        snapshot.AmountRefunded.Should().Be(new Money(25.00m, "EUR"));
    }

    [Fact]
    public void One_fetched_payment_yields_its_status_its_refunds_and_its_chargebacks()
    {
        var refunded = MolliePaymentProvider.EventsOf(Fixtures.Json(Mollie, "payment-paid-card-refunded.json"), DateTimeOffset.UtcNow);
        refunded.OfType<PaymentChangedEvent>().Should().ContainSingle();
        var refund = refunded.OfType<RefundChangedEvent>().Single().Refund;
        refund.ProviderRefundId.Should().Be("re_4qqhO89gsT");
        refund.Status.Should().Be(ProviderRefundStatus.Succeeded);
        refund.Reference.Should().Be("credit-notes/1");

        var charged = MolliePaymentProvider.EventsOf(Fixtures.Json(Mollie, "payment-paid-chargeback.json"), DateTimeOffset.UtcNow);
        var disputes = charged.OfType<DisputeChangedEvent>().Select(e => e.Dispute).ToList();
        disputes.Should().HaveCount(2);
        var open = disputes.Single(d => d.ProviderDisputeId == "chb_xFzwUN4ci8HAmSGUACS4J");
        open.Status.Should().Be(DisputeStatus.NeedsResponse);
        open.FundsWithdrawn.Should().BeTrue();
        open.EvidenceDueBy.HasValue.Should().BeFalse();
        open.Reason.Should().Be("AC01");
        // settlementAmount -50.07 against an amount of 35.07: the 15.00 beyond it is the chargeback fee.
        open.Fee.Should().Be(new Money(15.00m, "EUR"));
        var reversed = disputes.Single(d => d.ProviderDisputeId == "chb_reversed01");
        reversed.Status.Should().Be(DisputeStatus.Won);
        reversed.FundsReinstated.Should().BeTrue();
    }

    [Fact]
    public void The_event_key_is_the_state_not_the_id_because_Mollie_reuses_the_id()
    {
        var open = MolliePaymentProvider.EventsOf(Fixtures.Json(Mollie, "payment-open.json"), DateTimeOffset.UtcNow).Single();
        var paidJson = Fixtures.Text(Mollie, "payment-open.json").Replace("\"status\": \"open\"", "\"status\": \"paid\"");
        using var paid = JsonDocument.Parse(paidJson);
        var paidEvent = MolliePaymentProvider.EventsOf(paid.RootElement, DateTimeOffset.UtcNow).Single();

        paidEvent.EventKey.Should().NotBe(open.EventKey);
    }

    [Fact]
    public async Task A_webhook_for_a_payment_that_is_not_ours_is_ignored_without_a_fetch()
    {
        var (provider, handler, _) = Create(h => h.On("GET", "/v2/payments/tr_7UhSN1zuXS", Fixtures.Text(Mollie, "payment-open.json")));
        var delivery = new WebhookDelivery { Source = "payments-mollie", ResourceId = "tr_7UhSN1zuXS", Body = Encoding.UTF8.GetBytes("id=tr_7UhSN1zuXS") };

        var events = await provider.ParseWebhookAsync(delivery, new NoMerchants(), CancellationToken.None);

        events.Should().BeEmpty();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_webhook_fetches_the_payment_with_the_merchant_it_was_created_with()
    {
        var (provider, handler, _) = Create(h => h.On("GET", "/v2/payments/tr_WDqYK6vllg", Fixtures.Text(Mollie, "payment-paid-card-refunded.json")));
        var seller = MerchantContext.Connected("mollie", "Organizations/seller", "org_12345", "pfl_QkEhN94Ba", "access_seller_token", testMode: true);
        var delivery = new WebhookDelivery { Source = "payments-mollie", ResourceId = "tr_WDqYK6vllg", Body = Encoding.UTF8.GetBytes("id=tr_WDqYK6vllg") };

        var events = await provider.ParseWebhookAsync(delivery, new OneMerchant(seller), CancellationToken.None);

        events.Should().HaveCount(2);
        var request = handler.Requests.Single();
        request.Headers["Authorization"].Should().Be("Bearer access_seller_token");
        request.Uri.Query.Should().Contain("embed=refunds,chargebacks");
        request.Uri.Query.Should().Contain("testmode=true");
    }

    [Fact]
    public async Task A_connected_payment_is_created_on_the_sellers_profile_with_an_application_fee_and_an_idempotency_key()
    {
        var (provider, handler, _) = Create(h => h.On("POST", "/v2/payments", Fixtures.Text(Mollie, "payment-open.json"), HttpStatusCode.Created));
        var seller = MerchantContext.Connected("mollie", "Organizations/seller", "org_12345", "pfl_QkEhN94Ba", "access_seller_token");

        var snapshot = await provider.CreatePaymentAsync(new CreatePaymentRequest
        {
            Merchant = seller,
            Reference = "orders/12345",
            Amount = new Money(10.00m, "EUR"),
            Description = "Order 12345",
            Method = "ideal",
            RedirectUrl = "https://shop.example.org/orders/12345",
            WebhookUrl = "https://shop.example.org/spark/webhooks/payments-mollie",
            ApplicationFee = new Money(0.50m, "EUR"),
            IdempotencyKey = "spark-create-abc-0",
            Locale = "nl-BE",
            Metadata = new Dictionary<string, string> { ["spark_reference"] = "orders/12345" },
        }, CancellationToken.None);

        snapshot.Status.Should().Be(ProviderPaymentStatus.Open);
        var request = handler.Requests.Single();
        request.Headers["Idempotency-Key"].Should().Be("spark-create-abc-0");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("amount").GetProperty("value").GetString().Should().Be("10.00");
        body.RootElement.GetProperty("profileId").GetString().Should().Be("pfl_QkEhN94Ba");
        body.RootElement.GetProperty("applicationFee").GetProperty("amount").GetProperty("value").GetString().Should().Be("0.50");
        body.RootElement.GetProperty("locale").GetString().Should().Be("nl_BE");
        body.RootElement.GetProperty("metadata").GetProperty("spark_reference").GetString().Should().Be("orders/12345");
    }

    [Fact]
    public async Task A_refund_refused_for_lack_of_balance_is_a_failed_refund_not_an_exception()
    {
        var (provider, _, _) = Create(h => h.On("POST", "/v2/payments/tr_WDqYK6vllg/refunds", Fixtures.Text(Mollie, "refund-insufficient-balance.json"), HttpStatusCode.UnprocessableEntity));
        var seller = MerchantContext.Connected("mollie", "Organizations/seller", "org_12345", accessToken: "access_seller_token");

        var refund = await provider.RefundAsync(new RefundRequest
        {
            Merchant = seller,
            ProviderPaymentId = "tr_WDqYK6vllg",
            Amount = new Money(25.00m, "EUR"),
            Reference = "credit-notes/3",
            IdempotencyKey = "spark-refund-x-0",
        }, CancellationToken.None);

        refund.Status.Should().Be(ProviderRefundStatus.Failed);
        refund.FailureReason.Should().Be(RefundFailureReason.InsufficientBalance);
    }

    [Fact]
    public async Task A_queued_refund_is_pending()
    {
        var (provider, _, _) = Create(h => h.On("POST", "/v2/payments/tr_WDqYK6vllg/refunds", Fixtures.Text(Mollie, "refund-queued.json"), HttpStatusCode.Created));

        var refund = await provider.RefundAsync(new RefundRequest
        {
            Merchant = MerchantContext.Platform("mollie"),
            ProviderPaymentId = "tr_WDqYK6vllg",
            Amount = new Money(5.95m, "EUR"),
            Reference = "credit-notes/2",
            IdempotencyKey = "spark-refund-y-0",
        }, CancellationToken.None);

        refund.Status.Should().Be(ProviderRefundStatus.Pending);
        refund.ProviderRefundId.Should().Be("re_4qqhO89gsT");
    }

    [Fact]
    public void Onboarding_status_maps_onto_KYC()
    {
        var pending = MollieConnect.ReadOnboarding(Fixtures.Json(Mollie, "onboarding-needs-data.json"), "org_12345", null);
        pending.Kyc.Should().Be(KycStatus.Pending);
        pending.ChargesEnabled.Should().BeFalse();
        pending.Requirements.Should().Contain("needs-data");

        var verified = MollieConnect.ReadOnboarding(Fixtures.Json(Mollie, "onboarding-completed.json"), "org_12345", null);
        verified.Kyc.Should().Be(KycStatus.Verified);
        verified.ChargesEnabled.Should().BeTrue();
        verified.PayoutsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Connect_exchanges_the_code_and_reads_organization_profile_and_onboarding()
    {
        var handler = new FakeProviderHandler()
            .On("POST", "/oauth2/tokens", Fixtures.Text(Mollie, "oauth-token.json"))
            .On("GET", "/v2/organizations/me", Fixtures.Text(Mollie, "organization-me.json"))
            .On("GET", "/v2/profiles", Fixtures.Text(Mollie, "profiles.json"))
            .On("GET", "/v2/onboarding/me", Fixtures.Text(Mollie, "onboarding-needs-data.json"));
        var options = new MolliePaymentsOptions { ClientId = "app_test", ClientSecret = "secret_placeholder" };
        var api = new MollieApi(new FakeHttpClientFactory(handler), new StaticOptionsMonitor<MolliePaymentsOptions>(options));
        var connect = new MollieConnect(api, new ManualTimeProvider(DateTimeOffset.Parse("2026-10-19T12:00:00Z")));

        var start = await connect.StartAsync("Organizations/seller", null, "https://shop.example.org/spark/commerce/payments/connect/callback?state=s1", "https://x", "s1", CancellationToken.None);
        start.RedirectUrl.Should().StartWith("https://my.mollie.com/oauth2/authorize?");
        start.RedirectUrl.Should().Contain("state=s1");
        // The redirect URI must equal the registered one exactly: no query.
        start.RedirectUrl.Should().Contain("redirect_uri=https%3A%2F%2Fshop.example.org%2Fspark%2Fcommerce%2Fpayments%2Fconnect%2Fcallback&");

        var result = await connect.CompleteAsync(null, "auth_code", "https://shop.example.org/spark/commerce/payments/connect/callback?state=s1", CancellationToken.None);

        result.AccountId.Should().Be("org_12345");
        result.ProfileId.Should().Be("pfl_QkEhN94Ba");
        result.Account.Kyc.Should().Be(KycStatus.Pending);
        result.Tokens!.ExpiresAt.Should().Be(DateTimeOffset.Parse("2026-10-19T13:00:00Z"));
        var tokenRequest = handler.Requests.First();
        tokenRequest.Headers["Authorization"].Should().StartWith("Basic ");
        tokenRequest.Body.Should().Contain("grant_type=authorization_code");
        handler.Requests.Skip(1).Should().OnlyContain(r => r.Headers["Authorization"] == "Bearer access_placeholder_not_a_real_token");
    }

    [Fact]
    public async Task Settlements_import_only_paid_out_ones_with_payments_refunds_costs_and_the_payout()
    {
        var handler = new FakeProviderHandler()
            .On("GET", "/v2/settlements?", Fixtures.Text(Mollie, "settlements.json"))
            .On("GET", "/v2/settlements/stl_jDk30akdN/payments", Fixtures.Text(Mollie, "settlement-payments.json"))
            .On("GET", "/v2/settlements/stl_jDk30akdN/refunds", Fixtures.Text(Mollie, "settlement-refunds.json"))
            .On("GET", "/v2/settlements/stl_jDk30akdN/chargebacks", Fixtures.Text(Mollie, "settlement-chargebacks.json"));
        var options = new MolliePaymentsOptions { OrganizationToken = "access_platform_placeholder" };
        var source = new MollieSettlementSource(new MollieApi(new FakeHttpClientFactory(handler), new StaticOptionsMonitor<MolliePaymentsOptions>(options)));

        var settlements = new List<ProviderSettlement>();
        await foreach (var settlement in source.GetSettlementsAsync(MerchantContext.Platform("mollie"), DateTimeOffset.Parse("2026-10-01T00:00:00Z"), CancellationToken.None))
            settlements.Add(settlement);

        var only = settlements.Should().ContainSingle().Which;
        only.SettlementId.Should().Be("stl_jDk30akdN");
        only.Lines.Single(l => l.Kind == SettlementLineKind.Payment).SourceId.Should().Be("tr_WDqYK6vllg");
        only.Lines.Single(l => l.Kind == SettlementLineKind.Refund).Gross.Should().Be(new Money(-25.00m, "EUR"));
        only.Lines.Single(l => l.Kind == SettlementLineKind.ProviderFee).Gross.Should().Be(new Money(-1.58m, "EUR"));
        only.Lines.Single(l => l.Kind == SettlementLineKind.Payout).Net.Should().Be(new Money(73.42m, "EUR"));
        handler.Requests.Should().OnlyContain(r => r.Headers["Authorization"] == "Bearer access_platform_placeholder");
    }

    [Fact]
    public void The_catalog_keeps_bank_transfer_out_of_held_channels_and_has_no_country_input()
    {
        var (provider, _, _) = Create();

        var held = PaymentMethodCatalog.Offerable(provider.Methods, new PaymentMethodQuery { Held = true, MaxPaymentWindow = TimeSpan.FromMinutes(30) });

        held.Select(m => m.Id).Should().BeEquivalentTo(["card", "ideal"]);
        PaymentMethodCatalog.Offerable(provider.Methods, new PaymentMethodQuery { Held = true, MaxPaymentWindow = TimeSpan.FromHours(2) })
            .Select(m => m.Id).Should().Contain("bancontact");
        PaymentMethodCatalog.Offerable(provider.Methods, new PaymentMethodQuery()).Select(m => m.Id).Should().Contain("banktransfer");
    }

    private sealed class NoMerchants : IPaymentMerchantResolver
    {
        public Task<MerchantContext?> ForPaymentAsync(string provider, string providerPaymentId, CancellationToken cancellationToken = default) => Task.FromResult<MerchantContext?>(null);
        public Task<MerchantContext?> ForAccountAsync(string provider, string accountId, CancellationToken cancellationToken = default) => Task.FromResult<MerchantContext?>(null);
        public Task<MerchantContext> ForSellerAsync(string provider, string? sellerOrganizationId, CancellationToken cancellationToken = default) => Task.FromResult(MerchantContext.Platform(provider));
    }

    private sealed class OneMerchant(MerchantContext merchant) : IPaymentMerchantResolver
    {
        public Task<MerchantContext?> ForPaymentAsync(string provider, string providerPaymentId, CancellationToken cancellationToken = default) => Task.FromResult<MerchantContext?>(merchant);
        public Task<MerchantContext?> ForAccountAsync(string provider, string accountId, CancellationToken cancellationToken = default) => Task.FromResult<MerchantContext?>(merchant);
        public Task<MerchantContext> ForSellerAsync(string provider, string? sellerOrganizationId, CancellationToken cancellationToken = default) => Task.FromResult(merchant);
    }
}
