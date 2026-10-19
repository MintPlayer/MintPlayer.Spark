using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Commerce.Payments.Mollie;

/// <summary>
/// Mollie payments (commerce PRD §4.5, D6) on the v2 Payments API
/// (<see href="https://docs.mollie.com/reference/create-payment">create payment</see>). Webhooks carry only the
/// payment id (<c>id=tr_…</c>, form-encoded) and are not signed: the payment is fetched, with the credentials it was
/// created with, and only what Mollie returns is believed (X5 <see cref="FetchByIdStrategy"/>). Refunds and chargebacks
/// are embedded in the fetched payment, so one webhook yields every change.
/// </summary>
internal sealed class MolliePaymentProvider(MollieApi api, ILogger<MolliePaymentProvider> logger) : IPaymentProvider
{
    public const string ProviderName = "mollie";

    public string Name => ProviderName;

    public PaymentProviderCapabilities Capabilities => new(
        ConnectOnboarding: true,
        ApplicationFees: true,
        SupportsSplit: api.Options.SplitPayments,
        // Mollie takes chargeback disputes in its dashboard only; the evidence pack is a download.
        DisputeEvidenceByApi: false,
        Settlements: true);

    /// <summary>
    /// The catalog (research §4.1, Mollie's payment-method docs): the time a created payment stays payable. Mollie advises
    /// against predicting expiry and to wait for the webhook; these values only decide which methods a held channel may
    /// offer (D3). A Mollie payment is cancelable only for some methods, so none expires on demand.
    /// </summary>
    public IReadOnlyList<PaymentMethodInfo> Methods
    {
        get
        {
            var split = api.Options.SplitPayments;
            return
            [
                new(ProviderName, "card", "creditcard", TimeSpan.FromMinutes(30), false, false, true, FingerprintTiming.AfterPayment, split),
                new(ProviderName, "ideal", "ideal", TimeSpan.FromMinutes(15), false, false, false, FingerprintTiming.None, split),
                new(ProviderName, "bancontact", "bancontact", TimeSpan.FromHours(1), false, false, false, FingerprintTiming.None, split),
                new(ProviderName, "kbc", "kbc", TimeSpan.FromHours(1), false, false, false, FingerprintTiming.None, false),
                new(ProviderName, "belfius", "belfius", TimeSpan.FromHours(1), false, false, false, FingerprintTiming.None, false),
                new(ProviderName, "paypal", "paypal", TimeSpan.FromHours(6), false, false, false, FingerprintTiming.None, false),
                new(ProviderName, "klarna", "klarna", TimeSpan.FromHours(48), false, false, true, FingerprintTiming.None, false),
                new(ProviderName, "banktransfer", "banktransfer", TimeSpan.FromDays(12), false, true, false, FingerprintTiming.None, false),
            ];
        }
    }

    public async Task<ProviderPaymentSnapshot> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        var metadata = new JsonObject();
        foreach (var (key, value) in request.Metadata)
            metadata[key] = value;

        var body = new JsonObject
        {
            ["amount"] = MollieJson.Amount(request.Amount),
            ["description"] = Truncate(request.Description, 255),
            ["redirectUrl"] = request.RedirectUrl,
            ["metadata"] = metadata,
        };
        if (request.CancelUrl is not null)
            body["cancelUrl"] = request.CancelUrl;
        if (request.WebhookUrl is not null)
            body["webhookUrl"] = request.WebhookUrl;
        if (request.Method is not null)
            body["method"] = request.Method;
        else if (request.AllowedMethods.Count > 0)
            body["method"] = new JsonArray(request.AllowedMethods.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
        if (request.Capture == PaymentCaptureMode.Manual)
            body["captureMode"] = "manual";
        if (MollieLocale(request.Locale) is { } locale)
            body["locale"] = locale;
        if (request.CustomerEmail is not null)
            body["billingEmail"] = request.CustomerEmail;

        if (request.Merchant.IsConnected)
        {
            // OAuth: the payment is created on the seller's profile; the platform's fee is an application fee.
            if (request.Merchant.ProfileId is { } profileId)
                body["profileId"] = profileId;
            if (request.ApplicationFee is { } fee)
                body["applicationFee"] = new JsonObject { ["amount"] = MollieJson.Amount(fee), ["description"] = Truncate("Platform fee " + request.Reference, 255) };
        }
        if (request.Splits.Count > 0)
        {
            // Mollie routing (split payments): each share is routed to a connected organization.
            var routing = new JsonArray();
            foreach (var split in request.Splits)
            {
                var amount = split.ApplicationFee is { } splitFee ? split.Amount - splitFee : split.Amount;
                routing.Add(new JsonObject
                {
                    ["amount"] = MollieJson.Amount(amount),
                    ["destination"] = new JsonObject { ["type"] = "organization", ["organizationId"] = split.AccountId },
                });
            }
            body["routing"] = routing;
        }

        var payment = await api.PostAsync("payments", request.Merchant, body, request.IdempotencyKey, cancellationToken);
        return ReadPayment(payment);
    }

    public async Task<ProviderPaymentSnapshot> GetPaymentAsync(MerchantContext merchant, string providerPaymentId, CancellationToken cancellationToken)
        => ReadPayment(await FetchAsync(merchant, providerPaymentId, cancellationToken));

    private Task<JsonElement> FetchAsync(MerchantContext merchant, string providerPaymentId, CancellationToken cancellationToken)
        => api.GetAsync($"payments/{Uri.EscapeDataString(providerPaymentId)}?embed=refunds,chargebacks", merchant, cancellationToken);

    public async Task<ProviderPaymentSnapshot> CancelPaymentAsync(MerchantContext merchant, string providerPaymentId, string idempotencyKey, CancellationToken cancellationToken)
    {
        try
        {
            await api.DeleteAsync($"payments/{Uri.EscapeDataString(providerPaymentId)}", merchant, cancellationToken);
        }
        catch (MollieApiException ex) when (ex.Status == HttpStatusCode.UnprocessableEntity)
        {
            // Not cancelable (most methods are not, or it completed meanwhile): the late-payment path decides (D3).
            logger.LogDebug("Mollie payment {PaymentId} is not cancelable: {Detail}", providerPaymentId, ex.Detail);
        }
        return await GetPaymentAsync(merchant, providerPaymentId, cancellationToken);
    }

    public async Task<ProviderPaymentSnapshot> CaptureAsync(MerchantContext merchant, string providerPaymentId, Money? amount, string idempotencyKey, CancellationToken cancellationToken)
    {
        var body = new JsonObject();
        if (amount is not null)
            body["amount"] = MollieJson.Amount(amount);
        await api.PostAsync($"payments/{Uri.EscapeDataString(providerPaymentId)}/captures", merchant, body, idempotencyKey, cancellationToken);
        return await GetPaymentAsync(merchant, providerPaymentId, cancellationToken);
    }

    /// <summary>An authorized payment is released by cancelling it (Mollie: "Cancel payment" applies to authorized payments).</summary>
    public Task<ProviderPaymentSnapshot> VoidAsync(MerchantContext merchant, string providerPaymentId, string idempotencyKey, CancellationToken cancellationToken)
        => CancelPaymentAsync(merchant, providerPaymentId, idempotencyKey, cancellationToken);

    public async Task<ProviderRefundSnapshot> RefundAsync(RefundRequest request, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["amount"] = MollieJson.Amount(request.Amount),
            ["description"] = Truncate(request.Description ?? request.Reference, 140),
            ["metadata"] = new JsonObject { ["spark_reference"] = request.Reference },
        };
        try
        {
            var refund = await api.PostAsync($"payments/{Uri.EscapeDataString(request.ProviderPaymentId)}/refunds", request.Merchant, body, request.IdempotencyKey, cancellationToken);
            return ReadRefund(refund, request.ProviderPaymentId);
        }
        catch (MollieApiException ex) when (ex.Status == HttpStatusCode.UnprocessableEntity)
        {
            // D20: a connected seller's balance cannot cover the refund. Mollie answers 422 with a detail naming the
            // balance; the exact wording is not documented (doc-derived fixture), so it is matched loosely.
            var insufficient = ex.Detail is { } detail
                && (detail.Contains("balance", StringComparison.OrdinalIgnoreCase) || detail.Contains("insufficient", StringComparison.OrdinalIgnoreCase));
            return new ProviderRefundSnapshot
            {
                Provider = ProviderName,
                ProviderPaymentId = request.ProviderPaymentId,
                Status = ProviderRefundStatus.Failed,
                Amount = request.Amount,
                Reference = request.Reference,
                FailureReason = insufficient ? RefundFailureReason.InsufficientBalance : RefundFailureReason.Other,
                FailureMessage = ex.Detail ?? ex.Title,
            };
        }
    }

    public async Task<IReadOnlyList<PaymentProviderEvent>> ParseWebhookAsync(WebhookDelivery delivery, IPaymentMerchantResolver merchants, CancellationToken cancellationToken)
    {
        // Payment webhooks only (tr_…). Never trust the body beyond the id (FetchByIdStrategy).
        if (delivery.ResourceId is not { } id || !id.StartsWith("tr_", StringComparison.Ordinal))
            return [];
        if (await merchants.ForPaymentAsync(ProviderName, id, cancellationToken) is not { } merchant)
            return [];

        JsonElement payment;
        try { payment = await FetchAsync(merchant, id, cancellationToken); }
        catch (MollieApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return [];
        }
        return EventsOf(payment, delivery.ReceivedAtUtc);
    }

    /// <summary>The events one fetched payment implies: its status, every refund and every chargeback.</summary>
    internal static IReadOnlyList<PaymentProviderEvent> EventsOf(JsonElement payment, DateTimeOffset receivedAt)
    {
        var snapshot = ReadPayment(payment);
        var events = new List<PaymentProviderEvent>
        {
            // Mollie sends one id for every change: the key is the state, never the id alone (X5).
            new PaymentChangedEvent(ProviderName, $"{snapshot.ProviderPaymentId}:{snapshot.Status}:{snapshot.AmountRefunded?.ToMinorUnits()}", snapshot.UpdatedAt ?? receivedAt, snapshot),
        };
        foreach (var refund in MollieJson.Embedded(payment, "refunds"))
        {
            var refundSnapshot = ReadRefund(refund, snapshot.ProviderPaymentId);
            events.Add(new RefundChangedEvent(ProviderName, $"{refundSnapshot.ProviderRefundId}:{refundSnapshot.Status}", refundSnapshot.UpdatedAt ?? receivedAt, refundSnapshot));
        }
        foreach (var chargeback in MollieJson.Embedded(payment, "chargebacks"))
        {
            var dispute = ReadChargeback(chargeback, snapshot.ProviderPaymentId);
            events.Add(new DisputeChangedEvent(ProviderName, $"{dispute.ProviderDisputeId}:{dispute.Status}", dispute.CreatedAt ?? receivedAt, dispute));
        }
        return events;
    }

    public Task<DisputeEvidenceSubmission> SubmitDisputeEvidenceAsync(MerchantContext merchant, string providerDisputeId, DisputeEvidencePack pack, CancellationToken cancellationToken)
        => Task.FromResult(DisputeEvidenceSubmission.ManualOnly);

    /// <summary>A Mollie payment object as a snapshot.</summary>
    internal static ProviderPaymentSnapshot ReadPayment(JsonElement payment)
    {
        var details = MollieJson.Path(payment, "details");
        var status = MollieJson.String(payment, "status") switch
        {
            "open" => ProviderPaymentStatus.Open,
            "pending" => ProviderPaymentStatus.Pending,
            "authorized" => ProviderPaymentStatus.Authorized,
            "paid" => ProviderPaymentStatus.Paid,
            "canceled" => ProviderPaymentStatus.Canceled,
            "expired" => ProviderPaymentStatus.Expired,
            "failed" => ProviderPaymentStatus.Failed,
            var other => throw new InvalidOperationException($"Unknown Mollie payment status '{other}'."),
        };
        return new ProviderPaymentSnapshot
        {
            Provider = ProviderName,
            ProviderPaymentId = MollieJson.String(payment, "id") ?? throw new InvalidOperationException("A Mollie payment without an id."),
            Status = status,
            Amount = MollieJson.ReadMoney(payment, "amount") ?? throw new InvalidOperationException("A Mollie payment without an amount."),
            AmountRefunded = MollieJson.ReadMoney(payment, "amountRefunded"),
            CheckoutUrl = MollieJson.String(MollieJson.Path(payment, "_links", "checkout"), "href"),
            ExpiresAt = MollieJson.Date(payment, "expiresAt"),
            Method = MethodId(MollieJson.String(payment, "method")),
            Reference = MollieJson.String(MollieJson.Path(payment, "metadata"), "spark_reference"),
            PayerFingerprint = MollieJson.String(details, "cardFingerprint"),
            Prepaid = MollieJson.String(details, "cardFunding") is { } funding ? funding == "prepaid" : null,
            ApplicationFee = MollieJson.ReadMoney(MollieJson.Path(payment, "applicationFee"), "amount"),
            PaidAt = MollieJson.Date(payment, "paidAt"),
            UpdatedAt = MollieJson.Date(payment, "paidAt") ?? MollieJson.Date(payment, "canceledAt") ?? MollieJson.Date(payment, "expiredAt")
                ?? MollieJson.Date(payment, "failedAt") ?? MollieJson.Date(payment, "authorizedAt") ?? MollieJson.Date(payment, "createdAt"),
        };
    }

    internal static ProviderRefundSnapshot ReadRefund(JsonElement refund, string paymentId)
    {
        var status = MollieJson.String(refund, "status");
        return new ProviderRefundSnapshot
        {
            Provider = ProviderName,
            ProviderRefundId = MollieJson.String(refund, "id"),
            ProviderPaymentId = MollieJson.String(refund, "paymentId") ?? paymentId,
            Status = status switch
            {
                "refunded" => ProviderRefundStatus.Succeeded,
                "failed" => ProviderRefundStatus.Failed,
                "canceled" => ProviderRefundStatus.Canceled,
                _ => ProviderRefundStatus.Pending, // queued, pending, processing
            },
            Amount = MollieJson.ReadMoney(refund, "amount") ?? throw new InvalidOperationException("A Mollie refund without an amount."),
            Reference = MollieJson.String(MollieJson.Path(refund, "metadata"), "spark_reference"),
            FailureReason = status == "failed" ? RefundFailureReason.Other : null,
            UpdatedAt = MollieJson.Date(refund, "createdAt"),
        };
    }

    /// <summary>
    /// A Mollie chargeback (<see href="https://docs.mollie.com/reference/get-chargeback">get chargeback</see>): its funds
    /// are withdrawn when it is created; <c>reversedAt</c> means it was reversed (won). Mollie gives no deadline and takes
    /// no evidence by API.
    /// </summary>
    internal static ProviderDisputeSnapshot ReadChargeback(JsonElement chargeback, string paymentId)
    {
        var reversed = MollieJson.Date(chargeback, "reversedAt") is not null;
        var reason = MollieJson.Path(chargeback, "reason");
        var settlementAmount = MollieJson.ReadMoney(chargeback, "settlementAmount");
        var amount = MollieJson.ReadMoney(chargeback, "amount") ?? throw new InvalidOperationException("A Mollie chargeback without an amount.");
        return new ProviderDisputeSnapshot
        {
            Provider = ProviderName,
            ProviderDisputeId = MollieJson.String(chargeback, "id") ?? throw new InvalidOperationException("A Mollie chargeback without an id."),
            ProviderPaymentId = MollieJson.String(chargeback, "paymentId") ?? paymentId,
            Status = reversed ? DisputeStatus.Won : DisputeStatus.NeedsResponse,
            Amount = amount,
            Reason = MollieJson.String(reason, "code"),
            ReasonText = MollieJson.String(reason, "description"),
            FundsWithdrawn = true,
            FundsReinstated = reversed,
            // settlementAmount is what was taken from the balance (negative); beyond the amount, it is the fee.
            Fee = settlementAmount is { } taken && Math.Abs(taken.ToMinorUnits()) > amount.ToMinorUnits() && taken.Currency == amount.Currency
                ? Money.FromMinorUnits(Math.Abs(taken.ToMinorUnits()) - amount.ToMinorUnits(), amount.Currency)
                : null,
            CreatedAt = MollieJson.Date(chargeback, "createdAt"),
        };
    }

    /// <summary>Mollie's method name as the catalog id.</summary>
    internal static string? MethodId(string? mollieMethod) => mollieMethod switch
    {
        null => null,
        "creditcard" => "card",
        _ => mollieMethod,
    };

    /// <summary><c>nl-BE</c> → <c>nl_BE</c>, for the locales Mollie supports; null otherwise (Mollie picks).</summary>
    internal static string? MollieLocale(string? locale)
    {
        if (string.IsNullOrEmpty(locale))
            return null;
        var normalized = locale.Replace('-', '_');
        string[] supported = ["en_US", "en_GB", "nl_NL", "nl_BE", "fr_FR", "fr_BE", "de_DE", "de_AT", "de_CH", "es_ES", "ca_ES", "pt_PT", "it_IT", "nb_NO", "sv_SE", "fi_FI", "da_DK", "is_IS", "hu_HU", "pl_PL", "lv_LV", "lt_LT"];
        return supported.FirstOrDefault(s => string.Equals(s, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
