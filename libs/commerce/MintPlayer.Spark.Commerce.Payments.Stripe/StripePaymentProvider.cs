using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Payments.Stripe;

/// <summary>
/// Stripe payments (commerce PRD §4.5, D6).
/// <para>
/// <b>Held-flow shape (decided here, spike S5, fixture-based):</b> a payment is a <b>Checkout Session</b>
/// (hosted, redirect: the same buyer flow as Mollie, no Stripe.js in the host) with an explicit <c>expires_at</c> = the
/// hold's PaymentPending deadline, clamped to Stripe's bounds (30 min to 24 h after creation;
/// <see href="https://docs.stripe.com/api/checkout/sessions/create#create_checkout_session-expires_at">expires_at</see>),
/// <b>and</b> an active expiry when the hold dies (<c>POST /v1/checkout/sessions/{id}/expire</c>). A 10-minute hold is
/// shorter than Stripe's 30-minute minimum, so the active expiry is what keeps the payment window equal to the hold;
/// a payment already in flight can still complete, which the late-payment path handles (research §4.4). The catalog
/// therefore marks Stripe's immediate methods <see cref="PaymentMethodInfo.ExpiresOnDemand"/> and its delayed ones
/// (SEPA Direct Debit, bank transfer) <see cref="PaymentMethodInfo.DelayedConfirmation"/>, which D3 never offers in a held
/// channel. The PaymentIntent under the session carries the capture mode, the application fee and the metadata.
/// </para>
/// <para>
/// <b>Connect:</b> direct charges on the seller's connected account (<c>Stripe-Account</c> header) with
/// <c>application_fee_amount</c>, so the platform never holds the sellers' funds (D5) and refunds and disputes debit the
/// seller's balance.
/// </para>
/// </summary>
internal sealed class StripePaymentProvider(StripeApi api, TimeProvider time, ILogger<StripePaymentProvider> logger)
    : IPaymentProvider, IPaymentSplitSettler
{
    public const string ProviderName = "stripe";

    /// <summary>Stripe Checkout's bounds for <c>expires_at</c>.</summary>
    internal static readonly TimeSpan MinimumSessionExpiry = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan MaximumSessionExpiry = TimeSpan.FromHours(24);

    /// <summary>Stripe caps <c>evidence[uncategorized_text]</c> at 20,000 characters.</summary>
    private const int EvidenceTextLimit = 20_000;

    public string Name => ProviderName;

    public PaymentProviderCapabilities Capabilities => new(
        ConnectOnboarding: true,
        ApplicationFees: true,
        SupportsSplit: api.Options.SeparateChargesAndTransfers,
        DisputeEvidenceByApi: true,
        Settlements: true);

    public IReadOnlyList<PaymentMethodInfo> Methods
    {
        get
        {
            var split = api.Options.SeparateChargesAndTransfers;
            return
            [
                new(ProviderName, "card", "card", MaximumSessionExpiry, true, false, true, FingerprintTiming.BeforeConfirm, split),
                new(ProviderName, "ideal", "ideal", MaximumSessionExpiry, true, false, false, FingerprintTiming.None, split),
                new(ProviderName, "bancontact", "bancontact", MaximumSessionExpiry, true, false, false, FingerprintTiming.None, split),
                new(ProviderName, "klarna", "klarna", MaximumSessionExpiry, true, false, true, FingerprintTiming.None, false),
                new(ProviderName, "paypal", "paypal", MaximumSessionExpiry, true, false, true, FingerprintTiming.None, false),
                new(ProviderName, "sepadebit", "sepa_debit", MaximumSessionExpiry, true, true, false, FingerprintTiming.None, false),
                new(ProviderName, "banktransfer", "customer_balance", MaximumSessionExpiry, true, true, false, FingerprintTiming.None, false),
            ];
        }
    }

    public async Task<ProviderPaymentSnapshot> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var form = new StripeForm()
            .Add("mode", "payment")
            .Add("line_items[0][quantity]", 1)
            .Add("line_items[0][price_data][currency]", request.Amount.Currency.ToLowerInvariant())
            .Add("line_items[0][price_data][unit_amount]", request.Amount.ToMinorUnits())
            .Add("line_items[0][price_data][product_data][name]", Truncate(request.Description, 250))
            .Add("success_url", request.RedirectUrl)
            .Add("cancel_url", request.CancelUrl ?? request.RedirectUrl)
            .Add("client_reference_id", Truncate(request.Reference, 200))
            .Add("customer_email", request.CustomerEmail)
            .Add("locale", StripeLocale(request.Locale));
        foreach (var (key, value) in request.Metadata)
        {
            form.Add($"metadata[{key}]", value);
            form.Add($"payment_intent_data[metadata][{key}]", value);
        }

        IReadOnlyList<string> methods = request.Method is not null ? [request.Method] : request.AllowedMethods;
        for (var i = 0; i < methods.Count; i++)
            form.Add($"payment_method_types[{i}]", methods[i]);
        if (request.Capture == PaymentCaptureMode.Manual)
            form.Add("payment_intent_data[capture_method]", "manual");
        if (request.ExpiresAt is { } expiresAt)
            form.Add("expires_at", ClampExpiry(expiresAt, time.GetUtcNow()).ToUnixTimeSeconds());
        if (request.Merchant.IsConnected && request.ApplicationFee is { } fee)
            form.Add("payment_intent_data[application_fee_amount]", fee.ToMinorUnits());
        if (request.Splits.Count > 0)
            // Separate charges and transfers: the transfers are made from the charge once paid (TransferAsync).
            form.Add("payment_intent_data[transfer_group]", Truncate(request.Reference, 200));

        var session = await api.PostAsync("checkout/sessions", request.Merchant, form, request.IdempotencyKey, cancellationToken);
        return ReadSession(session);
    }

    /// <summary>The session's <c>expires_at</c>: the requested deadline within Stripe's bounds (a minute of margin on the minimum).</summary>
    internal static DateTimeOffset ClampExpiry(DateTimeOffset requested, DateTimeOffset now)
    {
        var min = now + MinimumSessionExpiry + TimeSpan.FromMinutes(1);
        var max = now + MaximumSessionExpiry - TimeSpan.FromMinutes(1);
        return requested < min ? min : requested > max ? max : requested;
    }

    public async Task<ProviderPaymentSnapshot> GetPaymentAsync(MerchantContext merchant, string providerPaymentId, CancellationToken cancellationToken)
        => ReadSession(await FetchSessionAsync(merchant, providerPaymentId, cancellationToken));

    private Task<JsonElement> FetchSessionAsync(MerchantContext merchant, string sessionId, CancellationToken cancellationToken)
        => api.GetAsync($"checkout/sessions/{Uri.EscapeDataString(sessionId)}?expand[]=payment_intent&expand[]=payment_intent.latest_charge", merchant, cancellationToken);

    public async Task<ProviderPaymentSnapshot> CancelPaymentAsync(MerchantContext merchant, string providerPaymentId, string idempotencyKey, CancellationToken cancellationToken)
    {
        try
        {
            await api.PostAsync($"checkout/sessions/{Uri.EscapeDataString(providerPaymentId)}/expire", merchant, new StripeForm(), idempotencyKey, cancellationToken);
        }
        catch (StripeApiException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            // Only an open session can be expired: it completed or expired meanwhile. The late-payment path decides.
            logger.LogDebug("Stripe session {SessionId} could not be expired: {Code}", providerPaymentId, ex.Code);
        }
        return await GetPaymentAsync(merchant, providerPaymentId, cancellationToken);
    }

    public async Task<ProviderPaymentSnapshot> CaptureAsync(MerchantContext merchant, string providerPaymentId, Money? amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        var intent = await PaymentIntentOfAsync(merchant, providerPaymentId, cancellationToken);
        var form = new StripeForm();
        if (amount is not null)
            form.Add("amount_to_capture", amount.ToMinorUnits());
        await api.PostAsync($"payment_intents/{Uri.EscapeDataString(intent)}/capture", merchant, form, idempotencyKey, cancellationToken);
        return await GetPaymentAsync(merchant, providerPaymentId, cancellationToken);
    }

    public async Task<ProviderPaymentSnapshot> VoidAsync(MerchantContext merchant, string providerPaymentId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var intent = await PaymentIntentOfAsync(merchant, providerPaymentId, cancellationToken);
        await api.PostAsync($"payment_intents/{Uri.EscapeDataString(intent)}/cancel", merchant, new StripeForm(), idempotencyKey, cancellationToken);
        return await GetPaymentAsync(merchant, providerPaymentId, cancellationToken);
    }

    private async Task<string> PaymentIntentOfAsync(MerchantContext merchant, string sessionId, CancellationToken cancellationToken)
        => StripeJson.Id(await api.GetAsync($"checkout/sessions/{Uri.EscapeDataString(sessionId)}", merchant, cancellationToken), "payment_intent")
            ?? throw new InvalidOperationException($"Stripe session {sessionId} has no PaymentIntent yet.");

    public async Task<ProviderRefundSnapshot> RefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        var intent = request.ProviderPaymentId.StartsWith("cs_", StringComparison.Ordinal)
            ? await PaymentIntentOfAsync(request.Merchant, request.ProviderPaymentId, cancellationToken)
            : request.ProviderPaymentId;
        var form = new StripeForm()
            .Add("payment_intent", intent)
            .Add("amount", request.Amount.ToMinorUnits())
            .Add("metadata[spark_reference]", request.Reference);
        if (request.Merchant.IsConnected && request.RefundApplicationFee)
            form.Add("refund_application_fee", true);
        try
        {
            return ReadRefund(await api.PostAsync("refunds", request.Merchant, form, request.IdempotencyKey, cancellationToken), request.ProviderPaymentId);
        }
        catch (StripeApiException ex) when (ex.Code is "balance_insufficient" or "insufficient_funds")
        {
            // D20: the (connected) account's balance cannot cover the refund
            // (https://docs.stripe.com/error-codes#balance-insufficient).
            return new ProviderRefundSnapshot
            {
                Provider = ProviderName,
                ProviderPaymentId = request.ProviderPaymentId,
                Status = ProviderRefundStatus.Failed,
                Amount = request.Amount,
                Reference = request.Reference,
                FailureReason = RefundFailureReason.InsufficientBalance,
                FailureMessage = ex.StripeMessage,
            };
        }
        catch (StripeApiException ex) when (ex.Code is "charge_disputed")
        {
            return new ProviderRefundSnapshot
            {
                Provider = ProviderName,
                ProviderPaymentId = request.ProviderPaymentId,
                Status = ProviderRefundStatus.Failed,
                Amount = request.Amount,
                Reference = request.Reference,
                FailureReason = RefundFailureReason.Disputed,
                FailureMessage = ex.StripeMessage,
            };
        }
    }

    public Task<IReadOnlyList<PaymentProviderEvent>> ParseWebhookAsync(WebhookDelivery delivery, IPaymentMerchantResolver merchants, CancellationToken cancellationToken)
    {
        // Signed by StripeSignatureStrategy: the body is authentic and is read as it is (X5).
        using var json = delivery.ReadJson();
        return Task.FromResult(EventsOf(json.RootElement));
    }

    /// <summary>The events one Stripe event object implies (<see href="https://docs.stripe.com/api/events/types">event types</see>).</summary>
    internal static IReadOnlyList<PaymentProviderEvent> EventsOf(JsonElement @event)
    {
        var id = StripeJson.String(@event, "id") ?? throw new InvalidOperationException("A Stripe event without an id.");
        var type = StripeJson.String(@event, "type") ?? string.Empty;
        var created = StripeJson.Time(@event, "created") ?? DateTimeOffset.UnixEpoch;
        var data = StripeJson.Path(@event, "data", "object");

        PaymentProviderEvent? result = type switch
        {
            "checkout.session.completed" or "checkout.session.async_payment_succeeded" or "checkout.session.expired"
                => new PaymentChangedEvent(ProviderName, id, created, ReadSession(data)),
            "checkout.session.async_payment_failed"
                => new PaymentChangedEvent(ProviderName, id, created, ReadSession(data) with { Status = ProviderPaymentStatus.Failed }),
            "payment_intent.succeeded" or "payment_intent.amount_capturable_updated" or "payment_intent.canceled" or "payment_intent.processing"
                => new PaymentChangedEvent(ProviderName, id, created, ReadPaymentIntent(data)),
            "charge.succeeded" or "charge.captured"
                => new PaymentChangedEvent(ProviderName, id, created, ReadCharge(data)),
            "refund.created" or "refund.updated" or "refund.failed" or "charge.refund.updated"
                => new RefundChangedEvent(ProviderName, id, created, ReadRefund(data, null)),
            _ when type.StartsWith("charge.dispute.", StringComparison.Ordinal)
                => new DisputeChangedEvent(ProviderName, id, created, ReadDispute(data, type)),
            "account.updated"
                => new ConnectedAccountChangedEvent(ProviderName, id, created, ReadAccount(data)),
            _ => null,
        };
        return result is null ? [] : [result];
    }

    public async Task<DisputeEvidenceSubmission> SubmitDisputeEvidenceAsync(MerchantContext merchant, string providerDisputeId, DisputeEvidencePack pack, CancellationToken cancellationToken)
    {
        var text = pack.ToText();
        var form = new StripeForm().Add("evidence[uncategorized_text]", text.Length <= EvidenceTextLimit ? text : text[..EvidenceTextLimit]);
        foreach (var (field, value) in pack.Fields)
            form.Add($"evidence[{EvidenceFieldName(field)}]", value);
        form.Add("submit", true);
        try
        {
            await api.PostAsync($"disputes/{Uri.EscapeDataString(providerDisputeId)}", merchant, form,
                PaymentIdempotencyKeys.For(PaymentIdempotencyKeys.Operations.Evidence, providerDisputeId), cancellationToken);
            return DisputeEvidenceSubmission.Submitted;
        }
        catch (StripeApiException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            // Past the deadline, or already submitted or closed.
            logger.LogInformation("Stripe refused evidence for {DisputeId}: {Code}", providerDisputeId, ex.Code);
            return DisputeEvidenceSubmission.Closed;
        }
    }

    /// <summary>Separate charges and transfers (D40): one transfer per seller from the charge.</summary>
    public async Task<string> TransferAsync(string providerPaymentId, IReadOnlyList<string> relatedIds, PaymentSplit split, string reference, string idempotencyKey, CancellationToken cancellationToken)
    {
        var platform = MerchantContext.Platform(ProviderName);
        var charge = relatedIds.FirstOrDefault(r => r.StartsWith("ch_", StringComparison.Ordinal))
            ?? ReadSession(await FetchSessionAsync(platform, providerPaymentId, cancellationToken)).RelatedIds.FirstOrDefault(r => r.StartsWith("ch_", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Stripe payment {providerPaymentId} has no charge to transfer from.");
        var amount = split.ApplicationFee is { } fee ? split.Amount - fee : split.Amount;
        var transfer = await api.PostAsync("transfers", platform, new StripeForm()
            .Add("amount", amount.ToMinorUnits())
            .Add("currency", amount.Currency.ToLowerInvariant())
            .Add("destination", split.AccountId)
            .Add("source_transaction", charge)
            .Add("transfer_group", reference)
            .Add("metadata[spark_reference]", reference)
            .Add("metadata[spark_seller]", split.SellerOrganizationId), idempotencyKey, cancellationToken);
        return StripeJson.String(transfer, "id") ?? throw new InvalidOperationException("Stripe answered a transfer without an id.");
    }

    // ---- reading Stripe objects ------------------------------------------------------------------------------------

    /// <summary>A Checkout Session (optionally with <c>payment_intent</c> and its <c>latest_charge</c> expanded).</summary>
    internal static ProviderPaymentSnapshot ReadSession(JsonElement session)
    {
        var sessionStatus = StripeJson.String(session, "status");
        var paymentStatus = StripeJson.String(session, "payment_status");
        var intent = StripeJson.Path(session, "payment_intent");
        var status = sessionStatus switch
        {
            "expired" => ProviderPaymentStatus.Expired,
            "complete" when paymentStatus is "paid" or "no_payment_required" => ProviderPaymentStatus.Paid,
            "complete" => ProviderPaymentStatus.Pending,
            _ => ProviderPaymentStatus.Open,
        };
        var related = new List<string>();
        ProviderPaymentSnapshot? fromIntent = null;
        if (intent.ValueKind == JsonValueKind.Object)
        {
            fromIntent = ReadPaymentIntent(intent);
            related.AddRange(fromIntent.RelatedIds);
            if (status != ProviderPaymentStatus.Expired)
                status = fromIntent.Status == ProviderPaymentStatus.Open ? status : fromIntent.Status;
        }
        else if (StripeJson.Id(session, "payment_intent") is { } intentId)
        {
            related.Add(intentId);
        }

        return new ProviderPaymentSnapshot
        {
            Provider = ProviderName,
            ProviderPaymentId = StripeJson.String(session, "id") ?? throw new InvalidOperationException("A Stripe session without an id."),
            Status = status,
            Amount = StripeJson.ReadMoney(session, "amount_total") ?? throw new InvalidOperationException("A Stripe session without amount_total."),
            CheckoutUrl = status == ProviderPaymentStatus.Open ? StripeJson.String(session, "url") : null,
            ExpiresAt = StripeJson.Time(session, "expires_at"),
            Reference = StripeJson.String(StripeJson.Path(session, "metadata"), "spark_reference") ?? StripeJson.String(session, "client_reference_id"),
            RelatedIds = related,
            Method = fromIntent?.Method,
            PayerFingerprint = fromIntent?.PayerFingerprint,
            Prepaid = fromIntent?.Prepaid,
            ApplicationFee = fromIntent?.ApplicationFee,
            AmountRefunded = fromIntent?.AmountRefunded,
            PaidAt = fromIntent?.PaidAt,
            UpdatedAt = StripeJson.Time(session, "created"),
        };
    }

    /// <summary>A PaymentIntent (optionally with <c>latest_charge</c> expanded). Its id is the snapshot's id; our record is found through its related ids or our reference.</summary>
    internal static ProviderPaymentSnapshot ReadPaymentIntent(JsonElement intent)
    {
        var id = StripeJson.String(intent, "id") ?? throw new InvalidOperationException("A Stripe PaymentIntent without an id.");
        var status = StripeJson.String(intent, "status") switch
        {
            "succeeded" => ProviderPaymentStatus.Paid,
            "requires_capture" => ProviderPaymentStatus.Authorized,
            "processing" => ProviderPaymentStatus.Pending,
            "canceled" => ProviderPaymentStatus.Canceled,
            _ => ProviderPaymentStatus.Open, // requires_payment_method, requires_confirmation, requires_action
        };
        var charge = StripeJson.Path(intent, "latest_charge");
        var fromCharge = charge.ValueKind == JsonValueKind.Object ? ReadCharge(charge) : null;
        var related = new List<string> { id };
        if (fromCharge is not null)
            related.AddRange(fromCharge.RelatedIds.Where(r => r != id));
        else if (StripeJson.Id(intent, "latest_charge") is { } chargeId)
            related.Add(chargeId);

        return new ProviderPaymentSnapshot
        {
            Provider = ProviderName,
            ProviderPaymentId = id,
            Status = status,
            Amount = StripeJson.ReadMoney(intent, "amount") ?? throw new InvalidOperationException("A Stripe PaymentIntent without an amount."),
            Reference = StripeJson.String(StripeJson.Path(intent, "metadata"), "spark_reference"),
            RelatedIds = related,
            Method = fromCharge?.Method,
            PayerFingerprint = fromCharge?.PayerFingerprint,
            Prepaid = fromCharge?.Prepaid,
            ApplicationFee = fromCharge?.ApplicationFee
                ?? (StripeJson.Long(intent, "application_fee_amount") is { } fee && StripeJson.String(intent, "currency") is { } currency ? Money.FromMinorUnits(fee, currency.ToUpperInvariant()) : null),
            AmountRefunded = fromCharge?.AmountRefunded,
            PaidAt = status == ProviderPaymentStatus.Paid ? fromCharge?.PaidAt ?? StripeJson.Time(intent, "created") : null,
            UpdatedAt = StripeJson.Time(intent, "created"),
        };
    }

    /// <summary>A Charge: the card fingerprint and funding (D29), the application fee and its <c>fee_…</c> id (GB4).</summary>
    internal static ProviderPaymentSnapshot ReadCharge(JsonElement charge)
    {
        var id = StripeJson.String(charge, "id") ?? throw new InvalidOperationException("A Stripe charge without an id.");
        var intent = StripeJson.Id(charge, "payment_intent");
        var card = StripeJson.Path(charge, "payment_method_details", "card");
        var related = new List<string>();
        if (intent is not null)
            related.Add(intent);
        related.Add(id);
        if (StripeJson.Id(charge, "application_fee") is { } feeId)
            related.Add(feeId);
        var captured = StripeJson.Bool(charge, "captured");
        var status = StripeJson.String(charge, "status") switch
        {
            "succeeded" when captured => ProviderPaymentStatus.Paid,
            "succeeded" => ProviderPaymentStatus.Authorized,
            "failed" => ProviderPaymentStatus.Failed,
            _ => ProviderPaymentStatus.Pending,
        };
        return new ProviderPaymentSnapshot
        {
            Provider = ProviderName,
            ProviderPaymentId = intent ?? id,
            Status = status,
            Amount = StripeJson.ReadMoney(charge, "amount") ?? throw new InvalidOperationException("A Stripe charge without an amount."),
            AmountRefunded = StripeJson.ReadMoney(charge, "amount_refunded"),
            Reference = StripeJson.String(StripeJson.Path(charge, "metadata"), "spark_reference"),
            RelatedIds = related,
            Method = StripeJson.String(StripeJson.Path(charge, "payment_method_details"), "type") switch
            {
                null => null,
                "sepa_debit" => "sepadebit",
                "customer_balance" => "banktransfer",
                var other => other,
            },
            PayerFingerprint = StripeJson.String(card, "fingerprint"),
            Prepaid = StripeJson.String(card, "funding") is { } funding ? funding == "prepaid" : null,
            ApplicationFee = StripeJson.ReadMoney(charge, "application_fee_amount"),
            PaidAt = captured ? StripeJson.Time(charge, "created") : null,
            UpdatedAt = StripeJson.Time(charge, "created"),
        };
    }

    internal static ProviderRefundSnapshot ReadRefund(JsonElement refund, string? paymentId)
    {
        var status = StripeJson.String(refund, "status");
        var failure = StripeJson.String(refund, "failure_reason");
        return new ProviderRefundSnapshot
        {
            Provider = ProviderName,
            ProviderRefundId = StripeJson.String(refund, "id"),
            ProviderPaymentId = StripeJson.Id(refund, "payment_intent") ?? StripeJson.Id(refund, "charge") ?? paymentId
                ?? throw new InvalidOperationException("A Stripe refund without a payment."),
            Status = status switch
            {
                "succeeded" => ProviderRefundStatus.Succeeded,
                "failed" => ProviderRefundStatus.Failed,
                "canceled" => ProviderRefundStatus.Canceled,
                _ => ProviderRefundStatus.Pending, // pending, requires_action
            },
            Amount = StripeJson.ReadMoney(refund, "amount") ?? throw new InvalidOperationException("A Stripe refund without an amount."),
            Reference = StripeJson.String(StripeJson.Path(refund, "metadata"), "spark_reference"),
            FailureReason = status != "failed" ? null : failure switch
            {
                "insufficient_funds" => RefundFailureReason.InsufficientBalance,
                "lost_or_stolen_card" or "expired_or_canceled_card" => RefundFailureReason.DestinationUnavailable,
                "charge_for_pending_refund_disputed" => RefundFailureReason.Disputed,
                _ => RefundFailureReason.Other,
            },
            FailureMessage = failure,
            UpdatedAt = StripeJson.Time(refund, "created"),
        };
    }

    /// <summary>A Dispute (<see href="https://docs.stripe.com/api/disputes/object">object</see>).</summary>
    internal static ProviderDisputeSnapshot ReadDispute(JsonElement dispute, string eventType)
    {
        var transactions = StripeJson.Array(dispute, "balance_transactions").ToList();
        var withdrawn = eventType == "charge.dispute.funds_withdrawn" || transactions.Any(t => StripeJson.Long(t, "amount") < 0);
        var reinstated = eventType == "charge.dispute.funds_reinstated" || transactions.Any(t => StripeJson.Long(t, "amount") > 0);
        var currency = StripeJson.String(dispute, "currency")?.ToUpperInvariant() ?? "EUR";
        var fee = transactions.Where(t => StripeJson.Long(t, "amount") < 0).Sum(t => StripeJson.Long(t, "fee") ?? 0);
        return new ProviderDisputeSnapshot
        {
            Provider = ProviderName,
            ProviderDisputeId = StripeJson.String(dispute, "id") ?? throw new InvalidOperationException("A Stripe dispute without an id."),
            ProviderPaymentId = StripeJson.Id(dispute, "payment_intent") ?? StripeJson.Id(dispute, "charge")
                ?? throw new InvalidOperationException("A Stripe dispute without a payment."),
            Status = StripeJson.String(dispute, "status") switch
            {
                "warning_needs_response" or "warning_under_review" => DisputeStatus.Warning,
                "warning_closed" => DisputeStatus.WarningClosed,
                "under_review" => DisputeStatus.UnderReview,
                "won" => DisputeStatus.Won,
                "lost" => DisputeStatus.Lost,
                _ => DisputeStatus.NeedsResponse,
            },
            Amount = StripeJson.ReadMoney(dispute, "amount") ?? throw new InvalidOperationException("A Stripe dispute without an amount."),
            Reason = StripeJson.String(dispute, "reason"),
            EvidenceDueBy = StripeJson.Time(StripeJson.Path(dispute, "evidence_details"), "due_by"),
            FundsWithdrawn = withdrawn,
            FundsReinstated = reinstated,
            Fee = fee > 0 ? Money.FromMinorUnits(fee, currency) : null,
            CreatedAt = StripeJson.Time(dispute, "created"),
        };
    }

    /// <summary>A connected Account (<see href="https://docs.stripe.com/api/accounts/object">object</see>) as a KYC state (D21).</summary>
    internal static ConnectedAccountSnapshot ReadAccount(JsonElement account)
    {
        var requirements = StripeJson.Path(account, "requirements");
        var disabled = StripeJson.String(requirements, "disabled_reason");
        var charges = StripeJson.Bool(account, "charges_enabled");
        var payouts = StripeJson.Bool(account, "payouts_enabled");
        return new ConnectedAccountSnapshot
        {
            Provider = ProviderName,
            AccountId = StripeJson.String(account, "id") ?? throw new InvalidOperationException("A Stripe account without an id."),
            Kyc = disabled is not null && disabled.StartsWith("rejected", StringComparison.Ordinal) ? KycStatus.Rejected
                : charges && payouts ? KycStatus.Verified
                : KycStatus.Pending,
            ChargesEnabled = charges,
            PayoutsEnabled = payouts,
            Requirements = StripeJson.Array(requirements, "currently_due").Concat(StripeJson.Array(requirements, "past_due"))
                .Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!).Distinct().ToList(),
            DisabledReason = disabled,
            Name = StripeJson.String(StripeJson.Path(account, "business_profile"), "name") ?? StripeJson.String(StripeJson.Path(account, "settings", "dashboard"), "display_name"),
        };
    }

    internal static string EvidenceFieldName(DisputeEvidenceField field) => field switch
    {
        DisputeEvidenceField.CustomerName => "customer_name",
        DisputeEvidenceField.CustomerEmail => "customer_email_address",
        DisputeEvidenceField.CustomerPurchaseIp => "customer_purchase_ip",
        DisputeEvidenceField.BillingAddress => "billing_address",
        DisputeEvidenceField.ProductDescription => "product_description",
        DisputeEvidenceField.ServiceDate => "service_date",
        DisputeEvidenceField.AccessActivityLog => "access_activity_log",
        DisputeEvidenceField.ShippingTrackingNumber => "shipping_tracking_number",
        DisputeEvidenceField.ShippingCarrier => "shipping_carrier",
        DisputeEvidenceField.ShippingDate => "shipping_date",
        DisputeEvidenceField.RefundPolicyDisclosure => "refund_policy_disclosure",
        DisputeEvidenceField.CancellationPolicyDisclosure => "cancellation_policy_disclosure",
        DisputeEvidenceField.RefundRefusalExplanation => "refund_refusal_explanation",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    /// <summary>The language Stripe Checkout takes (<c>nl</c>, <c>fr</c>, <c>en-GB</c>…); <c>auto</c> when none is given.</summary>
    internal static string StripeLocale(string? locale)
    {
        if (string.IsNullOrEmpty(locale))
            return "auto";
        var normalized = locale.Replace('_', '-');
        string[] regional = ["en-GB", "fr-CA", "es-419", "pt-BR", "zh-HK", "zh-TW"];
        if (regional.FirstOrDefault(r => string.Equals(r, normalized, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact;
        var language = normalized.Split('-')[0].ToLowerInvariant();
        string[] languages = ["bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fil", "fr", "hr", "hu", "id", "it", "ja", "ko", "lt", "lv", "ms", "mt", "nb", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "th", "tr", "vi", "zh"];
        return languages.Contains(language) ? language : "auto";
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
