using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Payments.Services;

/// <summary>
/// Records provider events and tells the recipients (§4.5). Idempotent and order-tolerant: a payment never moves back
/// from <see cref="ProviderPaymentStatus.Paid"/>, a status already recorded is not announced again, and every ledger entry
/// has a natural id. Runs inside the webhook queue's retries, so a recipient that throws makes the whole delivery retry;
/// the stored state then makes the second pass announce only what was not announced.
/// </summary>
internal sealed class PaymentEventProcessor(
    IDocumentStore store,
    IServiceProvider services,
    PlatformFeeLedger ledger,
    PaymentNotices notices,
    IOptions<SparkCommerceOptions> commerce,
    TimeProvider time,
    ILogger<PaymentEventProcessor> logger)
{
    /// <summary>Audit actions (<c>ICommerceAudit</c>), prefixed with this package's alias.</summary>
    internal static class AuditActions
    {
        public const string PaymentCreated = "commerce-payments.payment-created";
        public const string PaymentStatus = "commerce-payments.payment-status";
        public const string RefundRequested = "commerce-payments.refund-requested";
        public const string RefundStatus = "commerce-payments.refund-status";
        public const string DisputeStatus = "commerce-payments.dispute-status";
        public const string FeeBooked = "commerce-payments.fee-booked";
        public const string ReconciliationResolved = "commerce-payments.reconciliation-resolved";
    }

    /// <summary>How long a lookup query waits for the index; a failure bound, not a delay (memory: timeouts are failure bounds).</summary>
    private static readonly TimeSpan StaleWait = TimeSpan.FromSeconds(10);

    public async Task ProcessAsync(IReadOnlyList<PaymentProviderEvent> events, CancellationToken cancellationToken)
    {
        foreach (var @event in events.OrderBy(e => e.OccurredAt))
        {
            switch (@event)
            {
                case PaymentChangedEvent changed:
                    await OnPaymentAsync(changed, cancellationToken);
                    break;
                case RefundChangedEvent refund:
                    await OnRefundAsync(refund, cancellationToken);
                    break;
                case DisputeChangedEvent dispute:
                    await OnDisputeAsync(dispute, cancellationToken);
                    break;
                case ConnectedAccountChangedEvent account:
                    await OnAccountAsync(account, cancellationToken);
                    break;
                default:
                    logger.LogDebug("Payment event {Type} from {Provider} is not handled.", @event.GetType().Name, @event.Provider);
                    break;
            }
        }
    }

    /// <summary>A payment by its provider id, or by one of its related ids (Stripe's <c>pi_…</c>/<c>ch_…</c>).</summary>
    /// <remarks>
    /// The related-id lookup is a query: it waits (bounded) for the index to include a payment stored moments ago. When
    /// nothing matches and <paramref name="reference"/> is given (Stripe's PaymentIntent events carry our reference in
    /// their metadata and can arrive before the session event that names the PaymentIntent), the newest open payment of
    /// that reference at this provider is taken.
    /// </remarks>
    internal static async Task<ProviderPayment?> FindPaymentAsync(IAsyncDocumentSession session, string provider, string providerPaymentId, IEnumerable<string>? relatedIds, CancellationToken cancellationToken, string? reference = null)
    {
        if (await session.LoadAsync<ProviderPayment>(ProviderPayment.IdFor(provider, providerPaymentId), cancellationToken) is { } direct)
            return direct;
        var candidates = new List<string> { providerPaymentId };
        candidates.AddRange(relatedIds ?? []);
        foreach (var id in candidates.Distinct())
        {
            var found = await session.Query<ProviderPayment>()
                .Customize(x => x.WaitForNonStaleResults(StaleWait))
                .Where(p => p.Provider == provider && p.RelatedIds.Any(r => r == id))
                .FirstOrDefaultAsync(cancellationToken);
            if (found is not null)
                return found;
        }
        if (reference is { Length: > 0 })
        {
            return await session.Query<ProviderPayment>()
                .Customize(x => x.WaitForNonStaleResults(StaleWait))
                .Where(p => p.Provider == provider && p.Reference == reference)
                .OrderByDescending(p => p.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
        }
        return null;
    }

    /// <summary>
    /// Whether a payment may move from <paramref name="from"/> to <paramref name="to"/>. Forward only; never away from
    /// Paid; a cancelled, expired or failed payment may still turn Paid (a late payment, research §4.4: the provider is
    /// the truth and Checkout decides to re-claim or refund).
    /// </summary>
    internal static bool CanMove(ProviderPaymentStatus from, ProviderPaymentStatus to)
    {
        if (from == to || from == ProviderPaymentStatus.Paid)
            return false;
        if (IsFinal(from))
            return to == ProviderPaymentStatus.Paid;
        return Rank(to) > Rank(from);
    }

    internal static bool IsFinal(ProviderPaymentStatus status)
        => status is ProviderPaymentStatus.Paid or ProviderPaymentStatus.Canceled or ProviderPaymentStatus.Expired or ProviderPaymentStatus.Failed;

    private static int Rank(ProviderPaymentStatus status) => status switch
    {
        ProviderPaymentStatus.Open => 0,
        ProviderPaymentStatus.Pending => 1,
        ProviderPaymentStatus.Authorized => 2,
        _ => 3,
    };

    private async Task OnPaymentAsync(PaymentChangedEvent changed, CancellationToken cancellationToken)
    {
        var snapshot = changed.Payment;
        using var session = store.OpenAsyncSession();
        var payment = await FindPaymentAsync(session, changed.Provider, snapshot.ProviderPaymentId, snapshot.RelatedIds, cancellationToken, snapshot.Reference);
        if (payment is null)
        {
            // Not ours (another app on the same provider account), or a forged fetch-by-id hint: nothing to do.
            logger.LogDebug("Payment {PaymentId} at {Provider} is not one of ours; event ignored.", snapshot.ProviderPaymentId, changed.Provider);
            return;
        }

        var previous = payment.Status;
        var moved = CanMove(previous, snapshot.Status);
        foreach (var related in snapshot.RelatedIds.Where(r => !payment.RelatedIds.Contains(r)))
            payment.RelatedIds.Add(related);
        payment.PayerFingerprint ??= snapshot.PayerFingerprint;
        payment.Prepaid ??= snapshot.Prepaid;
        payment.Method ??= snapshot.Method;
        if (snapshot.AmountRefunded is { } refunded)
            payment.AmountRefunded = refunded;
        if (snapshot.ApplicationFee is { } fee)
            payment.ApplicationFee = fee;
        if (moved)
        {
            payment.Status = snapshot.Status;
            if (snapshot.Status == ProviderPaymentStatus.Paid)
                payment.PaidAtUtc = (snapshot.PaidAt ?? changed.OccurredAt).UtcDateTime;
        }
        payment.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;

        if (moved && snapshot.Status == ProviderPaymentStatus.Paid && commerce.Value.Merchant == MerchantMode.ConnectedSellers)
        {
            var paidAt = snapshot.PaidAt ?? changed.OccurredAt;
            if (payment.SellerOrganizationId is not null && payment.ApplicationFee is { } applicationFee)
                await ledger.BookAsync(session, PlatformFeeEntryKind.ApplicationFee, payment.Provider, payment.ProviderPaymentId, applicationFee, payment.SellerOrganizationId, payment.Reference, paidAt, cancellationToken);
            foreach (var split in payment.Splits.Where(s => s.ApplicationFee is not null))
                await ledger.BookAsync(session, PlatformFeeEntryKind.ApplicationFee, payment.Provider, $"{payment.ProviderPaymentId}:{split.SellerOrganizationId}", split.ApplicationFee!, split.SellerOrganizationId, payment.Reference, paidAt, cancellationToken);
        }

        if (moved)
            await AuditAsync(session, AuditActions.PaymentStatus, payment.Id!, payment.SellerOrganizationId, payment.Amount, $"{previous} -> {payment.Status}", cancellationToken);

        await session.SaveChangesAsync(cancellationToken);

        // D40: a split that needs a transfer per seller once paid (Stripe separate charges and transfers). Retried with
        // the same keys until every split has its transfer; a throw retries the whole delivery.
        if (payment.Status == ProviderPaymentStatus.Paid && payment.Splits.Any(s => s.TransferId is null)
            && services.GetServices<IPaymentProvider>().FirstOrDefault(p => p.Name == payment.Provider) is IPaymentSplitSettler settler)
        {
            foreach (var split in payment.Splits.Where(s => s.TransferId is null))
            {
                split.TransferId = await settler.TransferAsync(payment.ProviderPaymentId, payment.RelatedIds,
                    new PaymentSplit(split.SellerOrganizationId, split.AccountId, split.Amount, split.ApplicationFee),
                    payment.Reference, PaymentIdempotencyKeys.For("transfer", payment.Id + "|" + split.SellerOrganizationId), cancellationToken);
                await session.SaveChangesAsync(cancellationToken);
            }
        }

        if (moved)
            foreach (var recipient in services.GetServices<IPaymentEventRecipient>())
                await recipient.OnPaymentChangedAsync(new PaymentChangeNotice(payment, previous), cancellationToken);
    }

    private async Task OnRefundAsync(RefundChangedEvent changed, CancellationToken cancellationToken)
    {
        var snapshot = changed.Refund;
        using var session = store.OpenAsyncSession();
        var payment = await FindPaymentAsync(session, changed.Provider, snapshot.ProviderPaymentId, null, cancellationToken);
        if (payment is null)
        {
            logger.LogDebug("Refund {RefundId} of unknown payment {PaymentId} at {Provider} ignored.", snapshot.ProviderRefundId, snapshot.ProviderPaymentId, changed.Provider);
            return;
        }

        var refund = payment.Refunds.FirstOrDefault(r => snapshot.ProviderRefundId is not null && r.ProviderRefundId == snapshot.ProviderRefundId)
            ?? payment.Refunds.FirstOrDefault(r => snapshot.Reference is not null && r.Reference == snapshot.Reference);
        ProviderRefundStatus? previous = refund?.Status;
        if (refund is null)
        {
            // Made outside the suite (the provider's dashboard): recorded so the reconciliation matches it.
            refund = new ProviderPaymentRefund
            {
                Reference = "provider:" + (snapshot.ProviderRefundId ?? changed.EventKey),
                ProviderRefundId = snapshot.ProviderRefundId,
                Amount = snapshot.Amount,
                RequestedAtUtc = changed.OccurredAt.UtcDateTime,
            };
            payment.Refunds.Add(refund);
        }
        else if (refund.Status == snapshot.Status && refund.FailureReason == snapshot.FailureReason)
        {
            return;
        }
        else if (refund.Status is ProviderRefundStatus.Succeeded && snapshot.Status is ProviderRefundStatus.Pending)
        {
            return; // out of order: a pending event after the succeeded one
        }

        refund.ProviderRefundId ??= snapshot.ProviderRefundId;
        refund.Status = snapshot.Status;
        refund.FailureReason = snapshot.FailureReason;
        refund.FailureMessage = snapshot.FailureMessage;
        refund.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        payment.UpdatedAtUtc = refund.UpdatedAtUtc;

        await AfterRefundChangeAsync(session, payment, refund, changed.OccurredAt, cancellationToken);
        await AuditAsync(session, AuditActions.RefundStatus, payment.Id!, payment.SellerOrganizationId, refund.Amount, $"{refund.Reference}: {previous?.ToString() ?? "-"} -> {refund.Status}", cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        foreach (var recipient in services.GetServices<IPaymentEventRecipient>())
            await recipient.OnRefundChangedAsync(new RefundChangeNotice(payment, refund, previous), cancellationToken);
    }

    /// <summary>
    /// The bookkeeping a refund state implies, in the caller's session: a failed refund is queued for <c>finance</c>
    /// (D20, the seller's balance cannot cover it), a refund that went through gives back its share of the platform fee.
    /// </summary>
    internal async Task AfterRefundChangeAsync(IAsyncDocumentSession session, ProviderPayment payment, ProviderPaymentRefund refund, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (refund.Status == ProviderRefundStatus.Failed)
        {
            var id = ReconciliationItem.IdFor(payment.Provider, "refund-" + PaymentIdempotencyKeys.For("refund", payment.Id + "|" + refund.Reference + "|" + refund.IdempotencyKey));
            if (await session.LoadAsync<ReconciliationItem>(id, cancellationToken) is null)
            {
                await session.StoreAsync(new ReconciliationItem
                {
                    Id = id,
                    Provider = payment.Provider,
                    SellerOrganizationId = payment.SellerOrganizationId,
                    Problem = ReconciliationProblem.RefundFailed,
                    Summary = refund.FailureReason == RefundFailureReason.InsufficientBalance
                        ? $"Refund {refund.Reference} of order {payment.Reference} failed: the account's balance is insufficient."
                        : $"Refund {refund.Reference} of order {payment.Reference} failed ({refund.FailureReason}).",
                    SourceId = refund.ProviderRefundId ?? payment.ProviderPaymentId,
                    DocumentId = payment.Id,
                    Amount = refund.Amount,
                    CreatedAtUtc = time.GetUtcNow().UtcDateTime,
                }, id, cancellationToken);
            }
            return;
        }

        if (refund.ApplicationFeeRefunded
            && refund.Status is ProviderRefundStatus.Pending or ProviderRefundStatus.Succeeded
            && payment.ApplicationFee is { } fee
            && payment.SellerOrganizationId is not null
            && commerce.Value.Merchant == MerchantMode.ConnectedSellers
            && !payment.Amount.IsZero)
        {
            // Pro rata, rounded once (X4 RoundDiv).
            var share = Money.RoundDiv((Int128)fee.ToMinorUnits() * refund.Amount.ToMinorUnits(), payment.Amount.ToMinorUnits());
            var source = refund.ProviderRefundId ?? $"{payment.ProviderPaymentId}:{refund.Reference}";
            await ledger.BookAsync(session, PlatformFeeEntryKind.ApplicationFeeRefund, payment.Provider, source,
                Money.FromMinorUnits((long)share, fee.Currency), payment.SellerOrganizationId, payment.Reference, at, cancellationToken);
        }
    }

    private async Task OnDisputeAsync(DisputeChangedEvent changed, CancellationToken cancellationToken)
    {
        var snapshot = changed.Dispute;
        using var session = store.OpenAsyncSession();
        var payment = await FindPaymentAsync(session, changed.Provider, snapshot.ProviderPaymentId, null, cancellationToken);
        var id = PaymentDispute.IdFor(changed.Provider, snapshot.ProviderDisputeId);
        var dispute = await session.LoadAsync<PaymentDispute>(id, cancellationToken);
        var isNew = dispute is null;
        DisputeStatus? previous = dispute?.Status;
        var now = time.GetUtcNow().UtcDateTime;

        dispute ??= new PaymentDispute
        {
            Id = id,
            Provider = changed.Provider,
            ProviderDisputeId = snapshot.ProviderDisputeId,
            ProviderPaymentId = payment?.ProviderPaymentId ?? snapshot.ProviderPaymentId,
            PaymentId = payment?.Id,
            Reference = payment?.Reference,
            SellerOrganizationId = payment?.SellerOrganizationId,
            AccountId = payment?.AccountId,
            OpenedAtUtc = (snapshot.CreatedAt ?? changed.OccurredAt).UtcDateTime,
        };

        // A dispute the seller accepted as lost stays lost unless the provider reverses it (won).
        var keepLocal = !isNew && dispute.AcceptedBy is not null && snapshot.Status != DisputeStatus.Won;
        if (!keepLocal)
            dispute.Status = snapshot.Status;
        dispute.Amount = snapshot.Amount;
        dispute.Fee = snapshot.Fee ?? dispute.Fee;
        dispute.Reason = snapshot.Reason ?? dispute.Reason;
        dispute.ReasonText = snapshot.ReasonText ?? dispute.ReasonText;
        dispute.EvidenceDueByUtc = snapshot.EvidenceDueBy?.UtcDateTime ?? dispute.EvidenceDueByUtc;
        dispute.FundsWithdrawn |= snapshot.FundsWithdrawn;
        dispute.FundsReinstated |= snapshot.FundsReinstated;
        if (dispute.IsClosed)
            dispute.ClosedAtUtc ??= now;
        dispute.UpdatedAtUtc = now;
        if (isNew)
            await session.StoreAsync(dispute, id, cancellationToken);

        // GB1/GB4: in connected mode the provider debits the seller's account; the platform records it.
        if (commerce.Value.Merchant == MerchantMode.ConnectedSellers && dispute.SellerOrganizationId is not null)
        {
            if (dispute.FundsWithdrawn)
                await ledger.BookAsync(session, PlatformFeeEntryKind.DisputeDebit, dispute.Provider, dispute.ProviderDisputeId, dispute.Amount, dispute.SellerOrganizationId, dispute.Reference, changed.OccurredAt, cancellationToken);
            if (dispute.FundsWithdrawn && dispute.Fee is { } disputeFee)
                await ledger.BookAsync(session, PlatformFeeEntryKind.DisputeFee, dispute.Provider, dispute.ProviderDisputeId, disputeFee, dispute.SellerOrganizationId, dispute.Reference, changed.OccurredAt, cancellationToken);
            if (dispute.FundsReinstated || (dispute.Status == DisputeStatus.Won && dispute.FundsWithdrawn))
                await ledger.BookAsync(session, PlatformFeeEntryKind.DisputeReversal, dispute.Provider, dispute.ProviderDisputeId, dispute.Amount, dispute.SellerOrganizationId, dispute.Reference, changed.OccurredAt, cancellationToken);
        }

        var statusChanged = isNew || previous != dispute.Status;
        if (statusChanged)
            await AuditAsync(session, AuditActions.DisputeStatus, id, dispute.SellerOrganizationId, dispute.Amount, $"{previous?.ToString() ?? "opened"} -> {dispute.Status}", cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        if (dispute.SellerNotifiedAtUtc is null && dispute.Status is not (DisputeStatus.WarningClosed))
        {
            await notices.NotifyDisputeAsync(dispute, cancellationToken);
            dispute.SellerNotifiedAtUtc = time.GetUtcNow().UtcDateTime;
            await session.SaveChangesAsync(cancellationToken);
        }

        if (statusChanged)
            await AnnounceDisputeAsync(dispute, previous, cancellationToken);
    }

    internal async Task AnnounceDisputeAsync(PaymentDispute dispute, DisputeStatus? previous, CancellationToken cancellationToken)
    {
        foreach (var recipient in services.GetServices<IPaymentEventRecipient>())
            await recipient.OnDisputeChangedAsync(new DisputeChangeNotice(dispute, previous), cancellationToken);
    }

    private async Task OnAccountAsync(ConnectedAccountChangedEvent changed, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var account = await session.Query<SellerPaymentAccount>()
            .Customize(x => x.WaitForNonStaleResults(StaleWait))
            .Where(a => a.Provider == changed.Provider && a.AccountId == changed.Account.AccountId)
            .FirstOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            logger.LogDebug("Account {AccountId} at {Provider} is not linked to a seller; event ignored.", changed.Account.AccountId, changed.Provider);
            return;
        }
        var previous = await ApplyAccountAsync(account, changed.Account);
        await session.SaveChangesAsync(cancellationToken);
        if (previous != account.Kyc)
            await AnnounceAccountAsync(account, previous, cancellationToken);
    }

    /// <summary>Copies a provider's account state onto the seller's record; returns the previous KYC state.</summary>
    internal Task<KycStatus> ApplyAccountAsync(SellerPaymentAccount account, ConnectedAccountSnapshot snapshot)
    {
        var previous = account.Kyc;
        account.Kyc = snapshot.Kyc;
        account.ChargesEnabled = snapshot.ChargesEnabled;
        account.PayoutsEnabled = snapshot.PayoutsEnabled;
        account.Requirements = snapshot.Requirements.ToList();
        account.DisabledReason = snapshot.DisabledReason;
        account.AccountName = snapshot.Name ?? account.AccountName;
        account.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        return Task.FromResult(previous);
    }

    internal async Task AnnounceAccountAsync(SellerPaymentAccount account, KycStatus previous, CancellationToken cancellationToken)
    {
        foreach (var recipient in services.GetServices<IPaymentEventRecipient>())
            await recipient.OnSellerAccountChangedAsync(new SellerAccountChangeNotice(account, previous), cancellationToken);
    }

    /// <summary>Records a money-moving change in Commerce's audit trail when Commerce registered one.</summary>
    internal async Task AuditAsync(IAsyncDocumentSession session, string action, string subjectId, string? organizationId, Money? amount, string reason, CancellationToken cancellationToken)
    {
        if (services.GetService<ICommerceAudit>() is not { } audit)
            return;
        await audit.RecordAsync(session, new CommerceAuditEntry
        {
            Action = action,
            SubjectId = subjectId,
            OrganizationId = organizationId,
            After = amount,
            Reason = reason,
            At = time.GetUtcNow(),
        }, cancellationToken);
    }
}
