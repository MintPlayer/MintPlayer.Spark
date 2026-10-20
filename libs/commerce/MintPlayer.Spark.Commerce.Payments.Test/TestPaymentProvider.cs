using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments.Services;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Payments.Test;

/// <summary>
/// The in-process test provider (§4.5, D6): every simulated outcome of a real provider, driven by
/// <see cref="TestPaymentSimulator"/> or its payment page. Its webhooks go through the real webhook path (signed with a
/// per-process HMAC key, queued, deduplicated on the event id), so E2E tests exercise what production runs.
/// </summary>
internal sealed class TestPaymentProvider(
    TestPaymentStore store,
    PaymentUrls urls,
    IOptionsMonitor<TestPaymentsOptions> options,
    TimeProvider time) : IPaymentProvider, IPaymentSplitSettler
{
    public const string ProviderName = "test";

    public string Name => ProviderName;

    public PaymentProviderCapabilities Capabilities => new(
        ConnectOnboarding: true,
        ApplicationFees: true,
        SupportsSplit: options.CurrentValue.SupportsSplit,
        DisputeEvidenceByApi: options.CurrentValue.EvidenceByApi,
        Settlements: true);

    public IReadOnlyList<PaymentMethodInfo> Methods =>
    [
        new(ProviderName, "test", "test", null, true, false, true, FingerprintTiming.BeforeConfirm, options.CurrentValue.SupportsSplit),
        new(ProviderName, "card", "card", TimeSpan.FromMinutes(30), false, false, true, FingerprintTiming.AfterPayment, options.CurrentValue.SupportsSplit),
        new(ProviderName, "ideal", "ideal", TimeSpan.FromMinutes(15), false, false, false, FingerprintTiming.None, false),
        new(ProviderName, "banktransfer", "banktransfer", TimeSpan.FromDays(12), false, true, false, FingerprintTiming.None, false),
    ];

    public Task<ProviderPaymentSnapshot> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var id = store.IdempotencyKeys.GetOrAdd(request.IdempotencyKey, _ =>
        {
            var created = new TestPayment
            {
                Id = TestPaymentStore.NewId("tst_"),
                Amount = request.Amount,
                Reference = request.Reference,
                Method = request.Method,
                Capture = request.Capture,
                CreatedAt = time.GetUtcNow(),
                ExpiresAt = request.ExpiresAt,
                RedirectUrl = request.RedirectUrl,
                AccountId = request.Merchant.AccountId,
                ApplicationFee = request.ApplicationFee,
                UpdatedAt = time.GetUtcNow(),
            };
            store.Payments[created.Id] = created;
            return created.Id;
        });
        return Task.FromResult(Snapshot(store.Payments[id]));
    }

    public Task<ProviderPaymentSnapshot> GetPaymentAsync(MerchantContext merchant, string providerPaymentId, CancellationToken cancellationToken)
        => Task.FromResult(Snapshot(Find(providerPaymentId)));

    public Task<ProviderPaymentSnapshot> CancelPaymentAsync(MerchantContext merchant, string providerPaymentId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var payment = Find(providerPaymentId);
        lock (payment)
        {
            if (payment.Status is ProviderPaymentStatus.Open)
                Move(payment, ProviderPaymentStatus.Canceled);
        }
        return Task.FromResult(Snapshot(payment));
    }

    public Task<ProviderPaymentSnapshot> CaptureAsync(MerchantContext merchant, string providerPaymentId, Money? amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        var payment = Find(providerPaymentId);
        lock (payment)
        {
            if (payment.Status == ProviderPaymentStatus.Authorized)
                Move(payment, ProviderPaymentStatus.Paid);
        }
        return Task.FromResult(Snapshot(payment));
    }

    public Task<ProviderPaymentSnapshot> VoidAsync(MerchantContext merchant, string providerPaymentId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var payment = Find(providerPaymentId);
        lock (payment)
        {
            if (payment.Status == ProviderPaymentStatus.Authorized)
                Move(payment, ProviderPaymentStatus.Canceled);
        }
        return Task.FromResult(Snapshot(payment));
    }

    public Task<ProviderRefundSnapshot> RefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        var payment = Find(request.ProviderPaymentId);
        lock (payment)
        {
            if (store.IdempotencyKeys.TryGetValue(request.IdempotencyKey, out var existingId)
                && payment.Refunds.FirstOrDefault(r => r.Id == existingId) is { } existing)
                return Task.FromResult(RefundSnapshot(payment, existing));

            var refund = new TestRefund
            {
                Id = TestPaymentStore.NewId("tre_"),
                Amount = request.Amount,
                Reference = request.Reference,
                Status = payment.RefundBehaviour switch
                {
                    TestRefundBehaviour.Succeed => ProviderRefundStatus.Succeeded,
                    TestRefundBehaviour.Pending => ProviderRefundStatus.Pending,
                    _ => ProviderRefundStatus.Failed,
                },
                Failure = payment.RefundBehaviour switch
                {
                    TestRefundBehaviour.InsufficientBalance => RefundFailureReason.InsufficientBalance,
                    TestRefundBehaviour.Fail => RefundFailureReason.Other,
                    _ => null,
                },
            };
            payment.Refunds.Add(refund);
            store.IdempotencyKeys[request.IdempotencyKey] = refund.Id;
            return Task.FromResult(RefundSnapshot(payment, refund));
        }
    }

    public Task<IReadOnlyList<PaymentProviderEvent>> ParseWebhookAsync(WebhookDelivery delivery, IPaymentMerchantResolver merchants, CancellationToken cancellationToken)
    {
        using var json = delivery.ReadJson();
        var root = json.RootElement;
        var eventId = root.GetProperty("id").GetString()!;
        var events = new List<PaymentProviderEvent>();
        if (root.TryGetProperty("account", out var accountId) && store.Accounts.TryGetValue(accountId.GetString()!, out var account))
        {
            events.Add(new ConnectedAccountChangedEvent(ProviderName, eventId, time.GetUtcNow(), AccountSnapshot(account)));
        }
        if (root.TryGetProperty("payment", out var paymentId) && store.Payments.TryGetValue(paymentId.GetString()!, out var payment))
        {
            lock (payment)
            {
                events.Add(new PaymentChangedEvent(ProviderName, eventId, payment.UpdatedAt, Snapshot(payment)));
                foreach (var refund in payment.Refunds)
                    events.Add(new RefundChangedEvent(ProviderName, $"{eventId}:{refund.Id}", payment.UpdatedAt, RefundSnapshot(payment, refund)));
                foreach (var dispute in payment.Disputes)
                    events.Add(new DisputeChangedEvent(ProviderName, $"{eventId}:{dispute.Id}", payment.UpdatedAt, DisputeSnapshot(payment, dispute)));
            }
        }
        return Task.FromResult<IReadOnlyList<PaymentProviderEvent>>(events);
    }

    public Task<DisputeEvidenceSubmission> SubmitDisputeEvidenceAsync(MerchantContext merchant, string providerDisputeId, DisputeEvidencePack pack, CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.EvidenceByApi)
            return Task.FromResult(DisputeEvidenceSubmission.ManualOnly);
        foreach (var payment in store.Payments.Values)
        {
            lock (payment)
            {
                if (payment.Disputes.FirstOrDefault(d => d.Id == providerDisputeId) is { } dispute)
                {
                    if (dispute.Status != DisputeStatus.NeedsResponse)
                        return Task.FromResult(DisputeEvidenceSubmission.Closed);
                    dispute.Status = DisputeStatus.UnderReview;
                    return Task.FromResult(DisputeEvidenceSubmission.Submitted);
                }
            }
        }
        return Task.FromResult(DisputeEvidenceSubmission.Closed);
    }

    public Task<string> TransferAsync(string providerPaymentId, IReadOnlyList<string> relatedIds, PaymentSplit split, string reference, string idempotencyKey, CancellationToken cancellationToken)
        => Task.FromResult(store.IdempotencyKeys.GetOrAdd(idempotencyKey, _ => TestPaymentStore.NewId("ttr_")));

    // ---- snapshots ------------------------------------------------------------------------------------------------

    internal TestPayment Find(string id)
        => store.Payments.TryGetValue(id, out var payment) ? payment : throw new KeyNotFoundException($"No test payment '{id}'.");

    internal void Move(TestPayment payment, ProviderPaymentStatus status)
    {
        payment.Status = status;
        payment.UpdatedAt = time.GetUtcNow();
        if (status == ProviderPaymentStatus.Paid)
            payment.PaidAt = payment.UpdatedAt;
    }

    internal ProviderPaymentSnapshot Snapshot(TestPayment payment)
    {
        var refunded = payment.Refunds.Where(r => r.Status == ProviderRefundStatus.Succeeded).Sum(r => r.Amount.ToMinorUnits());
        return new ProviderPaymentSnapshot
        {
            Provider = ProviderName,
            ProviderPaymentId = payment.Id,
            Status = payment.Status,
            Amount = payment.Amount,
            AmountRefunded = refunded > 0 ? Money.FromMinorUnits(refunded, payment.Amount.Currency) : null,
            CheckoutUrl = payment.Status == ProviderPaymentStatus.Open ? CheckoutUrl(payment.Id) : null,
            ExpiresAt = payment.ExpiresAt,
            Method = payment.Method ?? "test",
            Reference = payment.Reference,
            PayerFingerprint = payment.Status is ProviderPaymentStatus.Paid or ProviderPaymentStatus.Authorized ? payment.Fingerprint : null,
            Prepaid = false,
            ApplicationFee = payment.ApplicationFee,
            PaidAt = payment.PaidAt,
            UpdatedAt = payment.UpdatedAt,
        };
    }

    private string? CheckoutUrl(string id)
    {
        try { return urls.Absolute(TestPaymentEndpoints.PayPath + "?id=" + Uri.EscapeDataString(id)); }
        catch (InvalidOperationException) { return TestPaymentEndpoints.PayPath + "?id=" + Uri.EscapeDataString(id); }
    }

    internal static ProviderRefundSnapshot RefundSnapshot(TestPayment payment, TestRefund refund) => new()
    {
        Provider = ProviderName,
        ProviderRefundId = refund.Id,
        ProviderPaymentId = payment.Id,
        Status = refund.Status,
        Amount = refund.Amount,
        Reference = refund.Reference,
        FailureReason = refund.Failure,
        FailureMessage = refund.Failure is null ? null : "Simulated by the test provider.",
        UpdatedAt = payment.UpdatedAt,
    };

    internal static ProviderDisputeSnapshot DisputeSnapshot(TestPayment payment, TestDispute dispute) => new()
    {
        Provider = ProviderName,
        ProviderDisputeId = dispute.Id,
        ProviderPaymentId = payment.Id,
        Status = dispute.Status,
        Amount = dispute.Amount,
        Reason = "fraudulent",
        ReasonText = "Simulated by the test provider.",
        EvidenceDueBy = dispute.DueBy,
        FundsWithdrawn = true,
        FundsReinstated = dispute.Reinstated,
        Fee = Money.FromMinorUnits(1500, dispute.Amount.Currency),
        CreatedAt = dispute.CreatedAt,
    };

    internal static ConnectedAccountSnapshot AccountSnapshot(TestAccount account) => new()
    {
        Provider = ProviderName,
        AccountId = account.Id,
        Kyc = account.Kyc,
        ChargesEnabled = account.Kyc == KycStatus.Verified,
        PayoutsEnabled = account.Kyc == KycStatus.Verified,
        Requirements = account.Kyc == KycStatus.Pending ? ["test.identity_document"] : [],
        Name = "Test seller " + account.SellerOrganizationId,
    };
}

/// <summary>Test Connect: onboarding returns at once, ending in <see cref="TestPaymentsOptions.OnboardingOutcome"/>.</summary>
internal sealed class TestConnect(TestPaymentStore store, IOptionsMonitor<TestPaymentsOptions> options) : IConnectOnboardingProvider
{
    public string Provider => TestPaymentProvider.ProviderName;

    public Task<ConnectOnboardingStart> StartAsync(string sellerOrganizationId, string? existingAccountId, string returnUrl, string refreshUrl, string state, CancellationToken cancellationToken)
    {
        var account = store.Accounts.Values.FirstOrDefault(a => a.SellerOrganizationId == sellerOrganizationId)
            ?? store.Accounts.GetOrAdd(TestPaymentStore.NewId("tacct_"), id => new TestAccount { Id = id, SellerOrganizationId = sellerOrganizationId, Kyc = KycStatus.Pending });
        // The "provider" sends the seller straight back, as if the hosted onboarding was completed.
        var separator = returnUrl.Contains('?') ? '&' : '?';
        return Task.FromResult(new ConnectOnboardingStart($"{returnUrl}{separator}code=test", account.Id));
    }

    public Task<ConnectOnboardingResult> CompleteAsync(string? accountId, string? code, string redirectUri, CancellationToken cancellationToken)
    {
        if (accountId is null || !store.Accounts.TryGetValue(accountId, out var account))
            throw new InvalidOperationException("Unknown test account.");
        account.Kyc = options.CurrentValue.OnboardingOutcome;
        return Task.FromResult(new ConnectOnboardingResult(account.Id, TestPaymentProvider.AccountSnapshot(account), null, null));
    }

    public Task<ConnectedAccountSnapshot> GetAccountAsync(MerchantContext merchant, CancellationToken cancellationToken)
        => Task.FromResult(store.Accounts.TryGetValue(merchant.AccountId ?? "", out var account)
            ? TestPaymentProvider.AccountSnapshot(account)
            : throw new KeyNotFoundException($"No test account '{merchant.AccountId}'."));

    public Task<ConnectTokens?> RefreshAsync(string refreshToken, CancellationToken cancellationToken) => Task.FromResult<ConnectTokens?>(null);
}

/// <summary>Test settlements: every paid, refunded or disputed movement not settled yet, as one settlement per import.</summary>
internal sealed class TestSettlementSource(TestPaymentStore store, TimeProvider time) : ISettlementSource
{
    public string Provider => TestPaymentProvider.ProviderName;

    public async IAsyncEnumerable<ProviderSettlement> GetSettlementsAsync(MerchantContext merchant, DateTimeOffset since, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var id = "tstl_" + Interlocked.Increment(ref store.SettlementCounter);
        var now = time.GetUtcNow();
        var lines = new List<ProviderSettlementLine>();
        foreach (var payment in store.Payments.Values.Where(p => p.AccountId == merchant.AccountId))
        {
            lock (payment)
            {
                if (payment.Status == ProviderPaymentStatus.Paid && !payment.Settled)
                {
                    payment.Settled = true;
                    lines.Add(new($"{id}:{payment.Id}", SettlementLineKind.Payment, payment.Id, payment.Amount, null, payment.Amount, payment.PaidAt ?? now, null));
                }
                foreach (var refund in payment.Refunds.Where(r => r.Status == ProviderRefundStatus.Succeeded && !r.Settled))
                {
                    refund.Settled = true;
                    var negative = Money.FromMinorUnits(-refund.Amount.ToMinorUnits(), refund.Amount.Currency);
                    lines.Add(new($"{id}:{refund.Id}", SettlementLineKind.Refund, refund.Id, negative, null, negative, now, null));
                }
                foreach (var dispute in payment.Disputes.Where(d => !d.Settled))
                {
                    dispute.Settled = true;
                    var negative = Money.FromMinorUnits(-dispute.Amount.ToMinorUnits(), dispute.Amount.Currency);
                    lines.Add(new($"{id}:{dispute.Id}", SettlementLineKind.Chargeback, dispute.Id, negative, null, negative, dispute.CreatedAt, null));
                }
            }
        }
        if (lines.Count == 0)
            yield break;
        var currency = lines[0].Net.Currency;
        var total = Money.FromMinorUnits(lines.Where(l => l.Net.Currency == currency).Sum(l => l.Net.ToMinorUnits()), currency);
        lines.Add(new($"{id}:payout", SettlementLineKind.Payout, id, total, null, total, now, "Test payout"));
        yield return new ProviderSettlement(id, id.ToUpperInvariant(), now, now, total, lines);
    }
}
