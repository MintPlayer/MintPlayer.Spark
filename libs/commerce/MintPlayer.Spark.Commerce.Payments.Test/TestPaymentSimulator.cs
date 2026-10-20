using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments.Services;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Payments.Test;

/// <summary>
/// Drives the test provider (§4.5: "in-process pages simulate success, failure, provider expiry, late and duplicate
/// webhooks, partial refunds and refund failure"). Each change is announced through the real webhook route's receiver
/// (<see cref="IWebhookReceiver"/>): signed, verified, queued on <c>spark-webhooks</c> and deduplicated on the event id,
/// exactly as a provider's delivery would be.
/// </summary>
public sealed class TestPaymentSimulator
{
    private readonly TestPaymentStore store;
    private readonly TestPaymentProvider provider;
    private readonly IWebhookReceiver receiver;
    private readonly TimeProvider time;

    internal TestPaymentSimulator(TestPaymentStore store, TestPaymentProvider provider, IWebhookReceiver receiver, TimeProvider time)
    {
        this.store = store;
        this.provider = provider;
        this.receiver = receiver;
        this.time = time;
    }

    /// <summary>The header the simulator signs into.</summary>
    public const string SignatureHeader = "X-Spark-Test-Signature";

    /// <summary>The buyer does <paramref name="outcome"/> at the provider; the webhook arrives as <paramref name="delivery"/> says.</summary>
    public async Task SimulateAsync(string providerPaymentId, TestPaymentOutcome outcome, TestDelivery delivery = TestDelivery.Immediate, CancellationToken cancellationToken = default)
    {
        var payment = provider.Find(providerPaymentId);
        lock (payment)
        {
            var target = outcome switch
            {
                TestPaymentOutcome.Pay => ProviderPaymentStatus.Paid,
                TestPaymentOutcome.Authorize => ProviderPaymentStatus.Authorized,
                TestPaymentOutcome.Pending => ProviderPaymentStatus.Pending,
                TestPaymentOutcome.Fail => ProviderPaymentStatus.Failed,
                TestPaymentOutcome.Cancel => ProviderPaymentStatus.Canceled,
                TestPaymentOutcome.Expire => ProviderPaymentStatus.Expired,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
            };
            if (target == ProviderPaymentStatus.Authorized && payment.Capture != PaymentCaptureMode.Manual)
                target = ProviderPaymentStatus.Paid;
            provider.Move(payment, target);
        }
        await AnnounceAsync(payment.Id, delivery, cancellationToken);
    }

    /// <summary>What the next refund of the payment does (partial refunds need no setting: refund any amount).</summary>
    public void SetRefundBehaviour(string providerPaymentId, TestRefundBehaviour behaviour)
    {
        var payment = provider.Find(providerPaymentId);
        lock (payment)
            payment.RefundBehaviour = behaviour;
    }

    /// <summary>Completes a pending refund, successfully or not, and announces it.</summary>
    public async Task CompleteRefundAsync(string providerPaymentId, string providerRefundId, bool succeeded, CancellationToken cancellationToken = default)
    {
        var payment = provider.Find(providerPaymentId);
        lock (payment)
        {
            var refund = payment.Refunds.Single(r => r.Id == providerRefundId);
            refund.Status = succeeded ? ProviderRefundStatus.Succeeded : ProviderRefundStatus.Failed;
            refund.Failure = succeeded ? null : RefundFailureReason.InsufficientBalance;
            payment.UpdatedAt = time.GetUtcNow();
        }
        await AnnounceAsync(providerPaymentId, TestDelivery.Immediate, cancellationToken);
    }

    /// <summary>The buyer's bank opens a dispute (GB1); answers the provider's dispute id.</summary>
    public async Task<string> OpenDisputeAsync(string providerPaymentId, Money? amount = null, CancellationToken cancellationToken = default)
    {
        var payment = provider.Find(providerPaymentId);
        TestDispute dispute;
        lock (payment)
        {
            dispute = new TestDispute
            {
                Id = TestPaymentStore.NewId("tdp_"),
                Amount = amount ?? payment.Amount,
                CreatedAt = time.GetUtcNow(),
                DueBy = time.GetUtcNow().AddDays(7),
            };
            payment.Disputes.Add(dispute);
            payment.UpdatedAt = time.GetUtcNow();
        }
        await AnnounceAsync(providerPaymentId, TestDelivery.Immediate, cancellationToken);
        return dispute.Id;
    }

    /// <summary>The issuer decides a dispute: <paramref name="won"/> gives the funds back.</summary>
    public async Task CloseDisputeAsync(string providerPaymentId, string providerDisputeId, bool won, CancellationToken cancellationToken = default)
    {
        var payment = provider.Find(providerPaymentId);
        lock (payment)
        {
            var dispute = payment.Disputes.Single(d => d.Id == providerDisputeId);
            dispute.Status = won ? DisputeStatus.Won : DisputeStatus.Lost;
            dispute.Reinstated = won;
            payment.UpdatedAt = time.GetUtcNow();
        }
        await AnnounceAsync(providerPaymentId, TestDelivery.Immediate, cancellationToken);
    }

    /// <summary>The provider finishes (or rejects) a seller's verification and says so (D21).</summary>
    public async Task SetAccountStatusAsync(string accountId, KycStatus status, CancellationToken cancellationToken = default)
    {
        if (!store.Accounts.TryGetValue(accountId, out var account))
            throw new KeyNotFoundException($"No test account '{accountId}'.");
        account.Kyc = status;
        await DeliverAsync(Body(new { id = TestPaymentStore.NewId("tevt_"), account = accountId }), cancellationToken);
    }

    /// <summary>Delivers every held (late) webhook now; answers how many.</summary>
    public async Task<int> DeliverLateAsync(CancellationToken cancellationToken = default)
    {
        var count = 0;
        while (store.LateDeliveries.TryDequeue(out var body))
        {
            await DeliverAsync(body, cancellationToken);
            count++;
        }
        return count;
    }

    private async Task AnnounceAsync(string providerPaymentId, TestDelivery delivery, CancellationToken cancellationToken)
    {
        var body = Body(new { id = TestPaymentStore.NewId("tevt_"), payment = providerPaymentId });
        switch (delivery)
        {
            case TestDelivery.Immediate:
                await DeliverAsync(body, cancellationToken);
                break;
            case TestDelivery.Late:
                store.LateDeliveries.Enqueue(body);
                break;
            case TestDelivery.Duplicate:
                await DeliverAsync(body, cancellationToken);
                await DeliverAsync(body, cancellationToken); // same event id: the receiver drops it
                await DeliverAsync(Body(new { id = TestPaymentStore.NewId("tevt_"), payment = providerPaymentId }), cancellationToken); // same change, new id
                break;
            case TestDelivery.None:
                break;
        }
    }

    private static byte[] Body(object value) => JsonSerializer.SerializeToUtf8Bytes(value);

    private async Task DeliverAsync(byte[] body, CancellationToken cancellationToken)
    {
        var signature = new HmacSignatureStrategyProbe(store.WebhookSecret).Sign(body);
        var request = new WebhookRequest(
        [
            new("Content-Type", new StringValues("application/json")),
            new(SignatureHeader, new StringValues(signature)),
        ], body);
        await receiver.ReceiveAsync(PaymentWebhookSources.For(TestPaymentProvider.ProviderName), request, cancellationToken);
    }

    /// <summary>HMAC-SHA256 of the body, lower-case hex: the scheme the source verifies (<see cref="HmacSignatureScheme"/>).</summary>
    private readonly struct HmacSignatureStrategyProbe(string secret)
    {
        public string Sign(byte[] body)
            => Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
    }
}
