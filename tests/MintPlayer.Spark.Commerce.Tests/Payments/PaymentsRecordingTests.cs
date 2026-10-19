using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Commerce.Payments.Models;
using MintPlayer.Spark.Commerce.Payments.Services;
using MintPlayer.Spark.Commerce.Payments.Test;
using MintPlayer.Spark.Commerce.Payments.Webhooks;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Webhooks;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Tests.Payments;

/// <summary>
/// The payments package against RavenDB with the test provider (D6): idempotent creation and refunds, webhook
/// recording (duplicates, out-of-order), application fees and their refunds in the fee ledger (GB4), the insufficient-
/// balance refund (D20), disputes with the Connect debit (GB1), chargeback finality, and settlement reconciliation (GB4).
/// </summary>
public class PaymentsRecordingTests : SparkTestDriver
{
    private const string Seller = "Organizations/seller-1";
    private const string Platform = "Organizations/platform";
    private readonly ManualTimeProvider clock = new(DateTimeOffset.Parse("2026-10-19T10:00:00Z"));

    private sealed class RecordingRecipient : IPaymentEventRecipient
    {
        public List<PaymentChangeNotice> Payments { get; } = [];
        public List<RefundChangeNotice> Refunds { get; } = [];
        public List<DisputeChangeNotice> Disputes { get; } = [];
        public List<SellerAccountChangeNotice> Accounts { get; } = [];

        public Task OnPaymentChangedAsync(PaymentChangeNotice notice, CancellationToken cancellationToken) { Payments.Add(notice); return Task.CompletedTask; }
        public Task OnRefundChangedAsync(RefundChangeNotice notice, CancellationToken cancellationToken) { Refunds.Add(notice); return Task.CompletedTask; }
        public Task OnDisputeChangedAsync(DisputeChangeNotice notice, CancellationToken cancellationToken) { Disputes.Add(notice); return Task.CompletedTask; }
        public Task OnSellerAccountChangedAsync(SellerAccountChangeNotice notice, CancellationToken cancellationToken) { Accounts.Add(notice); return Task.CompletedTask; }
    }

    private sealed record Host(IServiceProvider Services, RecordingRecipient Recipient, TestPaymentStore TestStore, TestPaymentsOptions TestOptions)
    {
        public T Get<T>() where T : notnull => Services.CreateScope().ServiceProvider.GetRequiredService<T>();
    }

    private Host Build(MerchantMode mode, Action<TestPaymentsOptions>? test = null, Action<SparkCommercePaymentsOptions>? payments = null)
    {
        var testOptions = new TestPaymentsOptions();
        test?.Invoke(testOptions);
        var paymentOptions = new SparkCommercePaymentsOptions { PublicBaseUrl = "https://shop.example.org", PlatformOrganizationId = Platform };
        payments?.Invoke(paymentOptions);
        var recipient = new RecordingRecipient();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Store);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(Options.Create(new SparkCommerceOptions { Merchant = mode }));
        services.AddSingleton<IOptionsMonitor<SparkCommercePaymentsOptions>>(new StaticOptionsMonitor<SparkCommercePaymentsOptions>(paymentOptions));
        services.AddSingleton<IOptionsMonitor<TestPaymentsOptions>>(new StaticOptionsMonitor<TestPaymentsOptions>(testOptions));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<TestPaymentStore>();
        services.AddScoped<TestPaymentProvider>();
        services.AddScoped<IPaymentProvider>(sp => sp.GetRequiredService<TestPaymentProvider>());
        services.AddScoped<ISettlementSource, TestSettlementSource>();
        services.AddScoped<IConnectOnboardingProvider, TestConnect>();
        services.AddSingleton<ConnectTokenProtector>();
        services.AddScoped<PaymentProviderRegistry>();
        services.AddScoped<PaymentUrls>();
        services.AddScoped<IPaymentMerchantResolver, PaymentMerchantResolver>();
        services.AddScoped<PlatformFeeLedger>();
        services.AddScoped<PaymentNotices>();
        services.AddScoped<PaymentEventProcessor>();
        services.AddScoped<SparkPayments>();
        services.AddScoped<ISparkPayments>(sp => sp.GetRequiredService<SparkPayments>());
        services.AddScoped<SettlementReconciler>();
        services.AddScoped<PaymentWebhookHandler>();
        services.AddSingleton<IPaymentEventRecipient>(recipient);
        var provider = services.BuildServiceProvider();
        return new Host(provider, recipient, provider.GetRequiredService<TestPaymentStore>(), testOptions);
    }

    private async Task LinkVerifiedSellerAsync(Host host, string accountId = "tacct_seller1")
    {
        host.TestStore.Accounts[accountId] = new TestAccount { Id = accountId, SellerOrganizationId = Seller, Kyc = KycStatus.Verified };
        await SeedAsync(session => session.StoreAsync(new SellerPaymentAccount
        {
            Id = SellerPaymentAccount.IdFor("test", Seller),
            Provider = "test",
            SellerOrganizationId = Seller,
            AccountId = accountId,
            Kyc = KycStatus.Verified,
            ChargesEnabled = true,
            PayoutsEnabled = true,
        }));
    }

    private static PaymentCreation Order(string reference, decimal amount = 75.00m, string? seller = null, int attempt = 0) => new()
    {
        Reference = reference,
        Attempt = attempt,
        SellerOrganizationId = seller,
        Amount = new Money(amount, "EUR"),
        Description = "Order " + reference,
        Method = "test",
        RedirectUrl = "/orders/" + reference,
    };

    /// <summary>What a provider webhook does: the handler parses the delivery and records the events.</summary>
    private static Task DeliverAsync(Host host, string providerPaymentId, string? eventId = null)
        => host.Get<PaymentWebhookHandler>().HandleAsync(new WebhookDelivery
        {
            Source = "payments-test",
            DeliveryId = eventId,
            PayloadVerified = true,
            Body = JsonSerializer.SerializeToUtf8Bytes(new { id = eventId ?? "tevt_" + Guid.NewGuid().ToString("N"), payment = providerPaymentId }),
        }, CancellationToken.None);

    private static void Pay(Host host, string providerPaymentId)
    {
        var payment = host.TestStore.Payments[providerPaymentId];
        payment.Status = ProviderPaymentStatus.Paid;
        payment.PaidAt = DateTimeOffset.Parse("2026-10-19T10:01:00Z");
        payment.UpdatedAt = payment.PaidAt.Value;
    }

    [Fact]
    public async Task Creating_twice_for_the_same_reference_and_attempt_returns_one_payment()
    {
        var host = Build(MerchantMode.Platform);

        var first = await host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/1"));
        var again = await host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/1"));
        var second = await host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/1", attempt: 1));

        again.Id.Should().Be(first.Id);
        second.Id.Should().NotBe(first.Id);
        host.TestStore.Payments.Should().HaveCount(2);
        first.CheckoutUrl.Should().StartWith("https://shop.example.org/spark/commerce/payments/test/pay?id=");
        first.Status.Should().Be(ProviderPaymentStatus.Open);
    }

    [Fact]
    public async Task A_paid_webhook_is_recorded_once_and_announced_once_even_when_redelivered()
    {
        var host = Build(MerchantMode.Platform);
        var payment = await host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/2"));
        Pay(host, payment.ProviderPaymentId);

        await DeliverAsync(host, payment.ProviderPaymentId, "tevt_a");
        await DeliverAsync(host, payment.ProviderPaymentId, "tevt_a");
        await DeliverAsync(host, payment.ProviderPaymentId, "tevt_b");

        host.Recipient.Payments.Should().ContainSingle();
        host.Recipient.Payments[0].PreviousStatus.Should().Be(ProviderPaymentStatus.Open);
        using var session = Store.OpenAsyncSession();
        var stored = await session.LoadAsync<ProviderPayment>(payment.Id);
        stored.Status.Should().Be(ProviderPaymentStatus.Paid);
        stored.PayerFingerprint.Should().NotBeNull();
    }

    [Fact]
    public async Task A_late_pending_event_after_paid_does_not_move_the_payment_back()
    {
        var host = Build(MerchantMode.Platform);
        var payment = await host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/3"));
        Pay(host, payment.ProviderPaymentId);
        await DeliverAsync(host, payment.ProviderPaymentId);

        var pending = host.TestStore.Payments[payment.ProviderPaymentId];
        await host.Get<ISparkPayments>().ProcessEventsAsync([new PaymentChangedEvent("test", "late", clock.GetUtcNow(), new ProviderPaymentSnapshot
        {
            Provider = "test",
            ProviderPaymentId = pending.Id,
            Status = ProviderPaymentStatus.Pending,
            Amount = pending.Amount,
        })]);

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<ProviderPayment>(payment.Id)).Status.Should().Be(ProviderPaymentStatus.Paid);
        host.Recipient.Payments.Should().ContainSingle();
    }

    [Fact]
    public async Task An_event_for_a_payment_that_is_not_ours_is_ignored()
    {
        var host = Build(MerchantMode.Platform);

        await host.Get<ISparkPayments>().ProcessEventsAsync([new PaymentChangedEvent("test", "x", clock.GetUtcNow(), new ProviderPaymentSnapshot
        {
            Provider = "test",
            ProviderPaymentId = "tst_not_ours",
            Status = ProviderPaymentStatus.Paid,
            Amount = new Money(1m, "EUR"),
        })]);

        host.Recipient.Payments.Should().BeEmpty();
    }

    [Fact]
    public async Task A_connected_payment_needs_a_verified_seller()
    {
        var host = Build(MerchantMode.ConnectedSellers);

        var unlinked = () => host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/4", seller: Seller));

        (await unlinked.Should().ThrowAsync<SparkValidationException>()).Which.Message.Should().Be("commercePayments.sellerNotLinked");
    }

    [Fact]
    public async Task A_paid_connected_payment_books_the_application_fee_once_and_a_refund_gives_back_its_share()
    {
        var host = Build(MerchantMode.ConnectedSellers, payments: o => o.PlatformFee = new PlatformFeeOptions { PercentBasisPoints = 500 });
        await LinkVerifiedSellerAsync(host);
        var payment = await host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/5", 80.00m, Seller));
        payment.ApplicationFee.Should().Be(new Money(4.00m, "EUR"));
        payment.AccountId.Should().Be("tacct_seller1");
        Pay(host, payment.ProviderPaymentId);

        await DeliverAsync(host, payment.ProviderPaymentId);
        await DeliverAsync(host, payment.ProviderPaymentId);
        var refund = await host.Get<ISparkPayments>().RefundAsync(payment.Id!, new Money(20.00m, "EUR"), "credit-notes/5");

        refund.Status.Should().Be(ProviderRefundStatus.Succeeded);
        using var session = Store.OpenAsyncSession();
        var entries = await session.Query<PlatformFeeEntry>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        entries.Single(e => e.Kind == PlatformFeeEntryKind.ApplicationFee).Amount.Should().Be(new Money(4.00m, "EUR"));
        // 4.00 × 20/80 = 1.00, rounded once.
        entries.Single(e => e.Kind == PlatformFeeEntryKind.ApplicationFeeRefund).Amount.Should().Be(new Money(1.00m, "EUR"));
        entries.Should().OnlyContain(e => e.SellerOrganizationId == Seller);
    }

    [Fact]
    public async Task A_refund_refused_for_insufficient_balance_is_recorded_queued_for_finance_and_retried_with_a_new_attempt()
    {
        var host = Build(MerchantMode.ConnectedSellers);
        await LinkVerifiedSellerAsync(host);
        var payment = await host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/6", 50.00m, Seller));
        Pay(host, payment.ProviderPaymentId);
        await DeliverAsync(host, payment.ProviderPaymentId);
        host.TestStore.Payments[payment.ProviderPaymentId].RefundBehaviour = TestRefundBehaviour.InsufficientBalance;

        var failed = await host.Get<ISparkPayments>().RefundAsync(payment.Id!, new Money(50.00m, "EUR"), "credit-notes/6");
        var same = await host.Get<ISparkPayments>().RefundAsync(payment.Id!, new Money(50.00m, "EUR"), "credit-notes/6");

        failed.Status.Should().Be(ProviderRefundStatus.Failed);
        failed.FailureReason.Should().Be(RefundFailureReason.InsufficientBalance);
        same.IdempotencyKey.Should().Be(failed.IdempotencyKey);
        host.TestStore.Payments[payment.ProviderPaymentId].Refunds.Should().ContainSingle();
        host.Recipient.Refunds.Should().ContainSingle(r => r.Refund.FailureReason == RefundFailureReason.InsufficientBalance);
        using (var session = Store.OpenAsyncSession())
        {
            var items = await session.Query<ReconciliationItem>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
            items.Should().ContainSingle(i => i.Problem == ReconciliationProblem.RefundFailed && i.SellerOrganizationId == Seller);
        }

        host.TestStore.Payments[payment.ProviderPaymentId].RefundBehaviour = TestRefundBehaviour.Succeed;
        var retried = await host.Get<ISparkPayments>().RefundAsync(payment.Id!, new Money(50.00m, "EUR"), "credit-notes/6", attempt: 1);
        retried.Status.Should().Be(ProviderRefundStatus.Succeeded);
    }

    [Fact]
    public async Task A_refund_cannot_exceed_the_payment()
    {
        var host = Build(MerchantMode.Platform);
        var payment = await host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/7", 30.00m));
        Pay(host, payment.ProviderPaymentId);
        await DeliverAsync(host, payment.ProviderPaymentId);
        await host.Get<ISparkPayments>().RefundAsync(payment.Id!, new Money(20.00m, "EUR"), "credit-notes/7a");

        var tooMuch = () => host.Get<ISparkPayments>().RefundAsync(payment.Id!, new Money(10.01m, "EUR"), "credit-notes/7b");

        await tooMuch.Should().ThrowAsync<SparkValidationException>();
    }

    [Fact]
    public async Task A_dispute_is_recorded_the_seller_debited_in_the_ledger_and_a_win_reverses_it()
    {
        var host = Build(MerchantMode.ConnectedSellers);
        await LinkVerifiedSellerAsync(host);
        var payment = await host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/8", 60.00m, Seller));
        Pay(host, payment.ProviderPaymentId);
        await DeliverAsync(host, payment.ProviderPaymentId);
        var testPayment = host.TestStore.Payments[payment.ProviderPaymentId];
        var dispute = new TestDispute { Id = "tdp_1", Amount = testPayment.Amount, CreatedAt = clock.GetUtcNow(), DueBy = clock.GetUtcNow().AddDays(7) };
        testPayment.Disputes.Add(dispute);

        await DeliverAsync(host, payment.ProviderPaymentId);
        await DeliverAsync(host, payment.ProviderPaymentId);

        host.Recipient.Disputes.Should().ContainSingle();
        host.Recipient.Disputes[0].PreviousStatus.HasValue.Should().BeFalse();
        using (var session = Store.OpenAsyncSession())
        {
            var stored = await session.LoadAsync<PaymentDispute>(PaymentDispute.IdFor("test", "tdp_1"));
            stored.Status.Should().Be(DisputeStatus.NeedsResponse);
            stored.SellerOrganizationId.Should().Be(Seller);
            stored.Reference.Should().Be("orders/8");
            stored.SellerNotifiedAtUtc.HasValue.Should().BeTrue();
            var ledger = await session.Query<PlatformFeeEntry>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
            ledger.Single(e => e.Kind == PlatformFeeEntryKind.DisputeDebit).Amount.Should().Be(new Money(60.00m, "EUR"));
            ledger.Single(e => e.Kind == PlatformFeeEntryKind.DisputeFee).Amount.Should().Be(new Money(15.00m, "EUR"));
        }

        dispute.Status = DisputeStatus.Won;
        dispute.Reinstated = true;
        await DeliverAsync(host, payment.ProviderPaymentId);

        host.Recipient.Disputes.Should().HaveCount(2);
        host.Recipient.Disputes[1].Dispute.Status.Should().Be(DisputeStatus.Won);
        using (var session = Store.OpenAsyncSession())
        {
            var ledger = await session.Query<PlatformFeeEntry>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
            ledger.Should().ContainSingle(e => e.Kind == PlatformFeeEntryKind.DisputeReversal);
        }
    }

    [Fact]
    public async Task Evidence_is_assembled_from_every_contributor_and_submitted_where_the_provider_takes_it()
    {
        var host = Build(MerchantMode.Platform);
        var payment = await host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/9"));
        Pay(host, payment.ProviderPaymentId);
        host.TestStore.Payments[payment.ProviderPaymentId].Disputes.Add(new TestDispute { Id = "tdp_9", Amount = payment.Amount, CreatedAt = clock.GetUtcNow(), DueBy = clock.GetUtcNow().AddDays(7) });
        await DeliverAsync(host, payment.ProviderPaymentId);
        var disputeId = PaymentDispute.IdFor("test", "tdp_9");

        var pack = await host.Get<ISparkPayments>().AssembleEvidenceAsync(disputeId);
        pack.OrderReference.Should().Be("orders/9");
        pack.ToText().Should().Contain("tdp_9");

        (await host.Get<ISparkPayments>().SubmitEvidenceAsync(disputeId, "SparkUsers/finance")).Should().Be(DisputeEvidenceSubmission.Submitted);
        (await host.Get<ISparkPayments>().SubmitEvidenceAsync(disputeId, "SparkUsers/finance")).Should().Be(DisputeEvidenceSubmission.Closed);
        using var session = Store.OpenAsyncSession();
        var stored = await session.LoadAsync<PaymentDispute>(disputeId);
        stored.Status.Should().Be(DisputeStatus.UnderReview);
        stored.EvidenceSubmittedBy.Should().Be("SparkUsers/finance");
    }

    [Fact]
    public async Task A_chargeback_no_provider_decides_is_closed_as_lost_after_the_finality_window()
    {
        var host = Build(MerchantMode.Platform, test: o => o.EvidenceByApi = false);
        var payment = await host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/10"));
        Pay(host, payment.ProviderPaymentId);
        host.TestStore.Payments[payment.ProviderPaymentId].Disputes.Add(new TestDispute { Id = "tdp_10", Amount = payment.Amount, CreatedAt = clock.GetUtcNow(), DueBy = clock.GetUtcNow().AddDays(7) });
        await DeliverAsync(host, payment.ProviderPaymentId);
        (await host.Get<ISparkPayments>().SubmitEvidenceAsync(PaymentDispute.IdFor("test", "tdp_10"), "u")).Should().Be(DisputeEvidenceSubmission.ManualOnly);

        clock.Now = clock.Now.AddDays(121);
        var closed = await host.Get<SparkPayments>().CloseStaleChargebacksAsync(CancellationToken.None);

        closed.Should().Be(1);
        host.Recipient.Disputes.Last().Dispute.Status.Should().Be(DisputeStatus.Lost);
        host.Recipient.Disputes.Last().Dispute.AcceptedBy.Should().Be("system:finality");
    }

    [Fact]
    public async Task Settlement_lines_match_payments_and_refunds_and_queue_what_does_not_match()
    {
        var host = Build(MerchantMode.Platform);
        var payment = await host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/11", 40.00m));
        Pay(host, payment.ProviderPaymentId);
        await DeliverAsync(host, payment.ProviderPaymentId);
        await host.Get<ISparkPayments>().RefundAsync(payment.Id!, new Money(10.00m, "EUR"), "credit-notes/11");
        // A payment the provider settled that we never recorded.
        host.TestStore.Payments["tst_stranger"] = new TestPayment
        {
            Id = "tst_stranger", Amount = new Money(5.00m, "EUR"), Reference = "elsewhere", RedirectUrl = "https://x",
            Status = ProviderPaymentStatus.Paid, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow(),
        };

        var imported = await host.Get<SettlementReconciler>().ImportAllAsync(CancellationToken.None);
        var reimported = await host.Get<SettlementReconciler>().ImportAllAsync(CancellationToken.None);

        imported.Should().Be(4); // payment, refund, stranger, payout
        reimported.Should().Be(0);
        using var session = Store.OpenAsyncSession();
        var lines = await session.Query<SettlementLine>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        lines.Single(l => l.SourceId == payment.ProviderPaymentId).Match.Should().Be(SettlementMatch.Matched);
        lines.Single(l => l.Kind == SettlementLineKind.Refund).Match.Should().Be(SettlementMatch.Matched);
        lines.Single(l => l.Kind == SettlementLineKind.Payout).Match.Should().Be(SettlementMatch.NotApplicable);
        lines.Single(l => l.SourceId == "tst_stranger").Match.Should().Be(SettlementMatch.Unmatched);
        var queue = await session.Query<ReconciliationItem>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        var item = queue.Should().ContainSingle().Which;
        item.Problem.Should().Be(ReconciliationProblem.Unmatched);

        (await host.Get<SettlementReconciler>().ResolveAsync(item.Id!, "Another shop on the same account.", "SparkUsers/finance", CancellationToken.None)).Should().BeTrue();
        (await host.Get<SettlementReconciler>().ResolveAsync(item.Id!, "again", "SparkUsers/finance", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task An_amount_that_differs_from_the_record_is_a_mismatch()
    {
        var host = Build(MerchantMode.Platform);
        var payment = await host.Get<ISparkPayments>().CreatePaymentAsync(Order("orders/12", 40.00m));
        var line = new ProviderSettlementLine("stl_x:tr_x", SettlementLineKind.Payment, payment.ProviderPaymentId, new Money(39.00m, "EUR"), null, new Money(39.00m, "EUR"), clock.GetUtcNow(), null);

        using var session = Store.OpenAsyncSession();
        var match = await SettlementReconciler.MatchAsync(session, "test", line, CancellationToken.None);

        match.Match.Should().Be(SettlementMatch.AmountMismatch);
        match.Expected.Should().Be(new Money(40.00m, "EUR"));
    }

    [Fact]
    public async Task The_KYC_gate_reports_the_linked_account_state()
    {
        var host = Build(MerchantMode.ConnectedSellers);
        var kyc = new PaymentsKycSource(Store, host.Get<PaymentProviderRegistry>());

        (await kyc.GetKycStatusAsync(Seller)).Should().Be(KycStatus.NotStarted);
        await LinkVerifiedSellerAsync(host);
        (await kyc.GetKycStatusAsync(Seller)).Should().Be(KycStatus.Verified);
    }

    [Fact]
    public async Task An_account_event_updates_the_seller_and_is_announced_when_KYC_changes()
    {
        var host = Build(MerchantMode.ConnectedSellers);
        await LinkVerifiedSellerAsync(host);
        var snapshot = new ConnectedAccountSnapshot { Provider = "test", AccountId = "tacct_seller1", Kyc = KycStatus.Rejected, DisabledReason = "rejected.fraud" };

        await host.Get<ISparkPayments>().ProcessEventsAsync([new ConnectedAccountChangedEvent("test", "e1", clock.GetUtcNow(), snapshot)]);
        await host.Get<ISparkPayments>().ProcessEventsAsync([new ConnectedAccountChangedEvent("test", "e2", clock.GetUtcNow(), snapshot)]);

        host.Recipient.Accounts.Should().ContainSingle();
        host.Recipient.Accounts[0].PreviousKyc.Should().Be(KycStatus.Verified);
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<SellerPaymentAccount>(SellerPaymentAccount.IdFor("test", Seller))).Kyc.Should().Be(KycStatus.Rejected);
    }
}
