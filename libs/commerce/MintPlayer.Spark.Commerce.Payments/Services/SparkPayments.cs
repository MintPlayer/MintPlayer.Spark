using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Payments.Services;

/// <summary>Maps one idempotency key to the payment it created. Id <c>PaymentAttempts/{key}</c>.</summary>
internal sealed class PaymentAttempt
{
    public string? Id { get; set; }
    public string PaymentId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public static string IdFor(string key) => "PaymentAttempts/" + key;
}

/// <inheritdoc cref="ISparkPayments"/>
internal sealed class SparkPayments(
    IDocumentStore store,
    PaymentProviderRegistry registry,
    IPaymentMerchantResolver merchants,
    PaymentEventProcessor processor,
    PaymentUrls urls,
    IServiceProvider services,
    IOptionsMonitor<SparkCommercePaymentsOptions> options,
    IOptions<SparkCommerceOptions> commerce,
    TimeProvider time,
    ILogger<SparkPayments> logger) : ISparkPayments
{
    public string DefaultProvider => registry.DefaultName;

    public IReadOnlyList<PaymentMethodInfo> GetMethods(string? provider = null) => registry.Methods(registry.Get(provider));

    public IReadOnlyList<PaymentMethodInfo> GetOfferableMethods(PaymentMethodQuery query, string? provider = null)
        => PaymentMethodCatalog.Offerable(GetMethods(provider), query);

    public async Task<ProviderPayment> CreatePaymentAsync(PaymentCreation creation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creation);
        ArgumentException.ThrowIfNullOrWhiteSpace(creation.Reference);
        var provider = registry.Get(creation.Provider);
        var key = PaymentIdempotencyKeys.For(PaymentIdempotencyKeys.Operations.Create, $"{provider.Name}|{creation.Reference}", creation.Attempt);

        using var session = store.OpenAsyncSession();
        if (await session.LoadAsync<PaymentAttempt>(PaymentAttempt.IdFor(key), cancellationToken) is { } attempt
            && await session.LoadAsync<ProviderPayment>(attempt.PaymentId, cancellationToken) is { } existing)
            return existing;

        var connected = commerce.Value.Merchant == MerchantMode.ConnectedSellers;
        var seller = connected ? creation.SellerOrganizationId : null;
        if (seller is not null)
            await RequireChargeableAsync(session, provider.Name, seller, cancellationToken);
        var merchant = await merchants.ForSellerAsync(provider.Name, seller, cancellationToken);

        var method = creation.Method is null ? null : registry.Methods(provider).FirstOrDefault(m => string.Equals(m.Id, creation.Method, StringComparison.OrdinalIgnoreCase))
            ?? throw new SparkValidationException("commercePayments.unknownMethod");
        if (method is { Enabled: false })
            throw new SparkValidationException("commercePayments.unknownMethod");
        if (creation.Capture == PaymentCaptureMode.Manual && method is { SupportsAuthorization: false })
            throw new SparkValidationException("commercePayments.noAuthorization");

        var splits = new List<PaymentSplit>();
        if (creation.Splits.Count > 0)
        {
            if (!connected)
                throw new SparkValidationException("commercePayments.splitNeedsConnectedSellers");
            if (!provider.Capabilities.SupportsSplit || method is { SupportsSplit: false })
                throw new SparkValidationException("commercePayments.splitUnsupported");
            foreach (var split in creation.Splits)
            {
                var account = await RequireChargeableAsync(session, provider.Name, split.SellerOrganizationId, cancellationToken);
                splits.Add(new PaymentSplit(split.SellerOrganizationId, account.AccountId!, split.Amount, split.ApplicationFee ?? options.CurrentValue.PlatformFee.For(split.Amount)));
            }
        }

        var applicationFee = seller is null ? null : creation.ApplicationFee ?? options.CurrentValue.PlatformFee.For(creation.Amount);
        var metadata = new Dictionary<string, string>(creation.Metadata) { ["spark_reference"] = creation.Reference };

        var snapshot = await provider.CreatePaymentAsync(new CreatePaymentRequest
        {
            Merchant = merchant,
            Reference = creation.Reference,
            Amount = creation.Amount,
            Description = creation.Description,
            Method = method?.ProviderMethod,
            AllowedMethods = creation.AllowedMethods
                .Select(id => registry.Methods(provider).FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))?.ProviderMethod)
                .OfType<string>().Distinct().ToList(),
            Capture = creation.Capture,
            ExpiresAt = creation.ExpiresAt,
            RedirectUrl = urls.Absolute(creation.RedirectUrl),
            CancelUrl = creation.CancelUrl is null ? null : urls.Absolute(creation.CancelUrl),
            WebhookUrl = TryWebhookUrl(provider.Name),
            ApplicationFee = applicationFee,
            Splits = splits,
            IdempotencyKey = key,
            Locale = creation.Locale,
            CustomerEmail = creation.CustomerEmail,
            Metadata = metadata,
        }, cancellationToken);

        var now = time.GetUtcNow().UtcDateTime;
        var payment = new ProviderPayment
        {
            Id = ProviderPayment.IdFor(provider.Name, snapshot.ProviderPaymentId),
            Provider = provider.Name,
            ProviderPaymentId = snapshot.ProviderPaymentId,
            RelatedIds = snapshot.RelatedIds.ToList(),
            Reference = creation.Reference,
            SellerOrganizationId = seller,
            AccountId = merchant.AccountId,
            Amount = creation.Amount,
            Status = snapshot.Status,
            Method = snapshot.Method ?? creation.Method,
            Capture = creation.Capture,
            ExpiresAtUtc = snapshot.ExpiresAt?.UtcDateTime,
            CheckoutUrl = snapshot.CheckoutUrl,
            ApplicationFee = applicationFee,
            Splits = splits.Select(s => new PaymentSplitRecord { SellerOrganizationId = s.SellerOrganizationId, AccountId = s.AccountId, Amount = s.Amount, ApplicationFee = s.ApplicationFee }).ToList(),
            IdempotencyKey = key,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        try
        {
            // Stored before the checkout URL is handed out, so no webhook can arrive for a payment we do not know.
            await session.StoreAsync(payment, payment.Id, cancellationToken);
            await session.StoreAsync(new PaymentAttempt { Id = PaymentAttempt.IdFor(key), PaymentId = payment.Id, CreatedAtUtc = now }, PaymentAttempt.IdFor(key), cancellationToken);
            await processor.AuditAsync(session, PaymentEventProcessor.AuditActions.PaymentCreated, payment.Id, seller, payment.Amount, creation.Reference, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Payment {PaymentId} was created at {Provider} but could not be stored; cancelling it.", snapshot.ProviderPaymentId, provider.Name);
            try
            {
                await provider.CancelPaymentAsync(merchant, snapshot.ProviderPaymentId, PaymentIdempotencyKeys.For(PaymentIdempotencyKeys.Operations.Cancel, payment.Id), CancellationToken.None);
            }
            catch (Exception cancelError)
            {
                logger.LogError(cancelError, "Payment {PaymentId} at {Provider} could not be cancelled either.", snapshot.ProviderPaymentId, provider.Name);
            }
            throw;
        }
        return payment;
    }

    /// <summary>Connected mode: the seller's account must be linked, verified and able to take charges (D21).</summary>
    private static async Task<SellerPaymentAccount> RequireChargeableAsync(IAsyncDocumentSession session, string provider, string sellerOrganizationId, CancellationToken cancellationToken)
    {
        var account = await session.LoadAsync<SellerPaymentAccount>(SellerPaymentAccount.IdFor(provider, sellerOrganizationId), cancellationToken);
        if (account?.AccountId is null)
            throw new SparkValidationException("commercePayments.sellerNotLinked");
        if (account.Kyc != KycStatus.Verified || !account.ChargesEnabled)
            throw new SparkValidationException("commercePayments.sellerNotVerified");
        return account;
    }

    private string? TryWebhookUrl(string provider)
    {
        try { return urls.Webhook(provider); }
        catch (InvalidOperationException) { return null; }
    }

    public async Task<ProviderPayment?> GetPaymentAsync(string paymentId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        ProviderPayment? payment;
        using (var session = store.OpenAsyncSession())
            payment = await session.LoadAsync<ProviderPayment>(paymentId, cancellationToken);
        if (payment is null || !refresh)
            return payment;

        var provider = registry.Get(payment.Provider);
        var merchant = await MerchantOfAsync(payment, cancellationToken);
        var snapshot = await provider.GetPaymentAsync(merchant, payment.ProviderPaymentId, cancellationToken);
        await processor.ProcessAsync([new PaymentChangedEvent(provider.Name, $"poll:{payment.ProviderPaymentId}:{snapshot.Status}", time.GetUtcNow(), snapshot)], cancellationToken);
        using var reread = store.OpenAsyncSession();
        return await reread.LoadAsync<ProviderPayment>(paymentId, cancellationToken);
    }

    public Task<ProviderPayment> CancelAsync(string paymentId, CancellationToken cancellationToken = default)
        => ActAsync(paymentId, PaymentIdempotencyKeys.Operations.Cancel, (p, m, id, key) => p.CancelPaymentAsync(m, id, key, cancellationToken), markCancel: true, cancellationToken);

    public Task<ProviderPayment> CaptureAsync(string paymentId, Money? amount = null, CancellationToken cancellationToken = default)
        => ActAsync(paymentId, PaymentIdempotencyKeys.Operations.Capture, (p, m, id, key) => p.CaptureAsync(m, id, amount, key, cancellationToken), markCancel: false, cancellationToken);

    public Task<ProviderPayment> VoidAsync(string paymentId, CancellationToken cancellationToken = default)
        => ActAsync(paymentId, PaymentIdempotencyKeys.Operations.Void, (p, m, id, key) => p.VoidAsync(m, id, key, cancellationToken), markCancel: true, cancellationToken);

    private async Task<ProviderPayment> ActAsync(
        string paymentId,
        string operation,
        Func<IPaymentProvider, MerchantContext, string, string, Task<ProviderPaymentSnapshot>> call,
        bool markCancel,
        CancellationToken cancellationToken)
    {
        ProviderPayment payment;
        using (var session = store.OpenAsyncSession())
        {
            payment = await session.LoadAsync<ProviderPayment>(paymentId, cancellationToken)
                ?? throw new KeyNotFoundException($"No payment '{paymentId}'.");
            if (markCancel && !payment.CancelRequested)
            {
                payment.CancelRequested = true;
                await session.SaveChangesAsync(cancellationToken);
            }
        }

        var provider = registry.Get(payment.Provider);
        var merchant = await MerchantOfAsync(payment, cancellationToken);
        var snapshot = await call(provider, merchant, payment.ProviderPaymentId, PaymentIdempotencyKeys.For(operation, payment.Id!));
        await processor.ProcessAsync([new PaymentChangedEvent(provider.Name, $"{operation}:{payment.ProviderPaymentId}:{snapshot.Status}", time.GetUtcNow(), snapshot)], cancellationToken);

        using var reread = store.OpenAsyncSession();
        return (await reread.LoadAsync<ProviderPayment>(paymentId, cancellationToken))!;
    }

    private async Task<MerchantContext> MerchantOfAsync(ProviderPayment payment, CancellationToken cancellationToken)
        => payment.SellerOrganizationId is null || payment.AccountId is null
            ? MerchantContext.Platform(payment.Provider)
            : await merchants.ForPaymentAsync(payment.Provider, payment.ProviderPaymentId, cancellationToken) ?? MerchantContext.Platform(payment.Provider);

    public async Task<ProviderPaymentRefund> RefundAsync(string paymentId, Money amount, string reference, bool refundApplicationFee = true, int attempt = 0, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (amount.ToMinorUnits() <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "A refund is a positive amount.");

        using var session = store.OpenAsyncSession();
        var payment = await session.LoadAsync<ProviderPayment>(paymentId, cancellationToken)
            ?? throw new KeyNotFoundException($"No payment '{paymentId}'.");
        var key = PaymentIdempotencyKeys.For(PaymentIdempotencyKeys.Operations.Refund, $"{paymentId}|{reference}", attempt);

        // Store-side idempotency: the same reference and attempt answers the recorded refund, without a provider call.
        var recorded = payment.Refunds.FirstOrDefault(r => r.Reference == reference);
        if (recorded is not null && (recorded.IdempotencyKey == key || recorded.Status != ProviderRefundStatus.Failed))
            return recorded;

        if (payment.Status != ProviderPaymentStatus.Paid)
            throw new SparkValidationException("commercePayments.notPaid");
        var alreadyRefunded = payment.Refunds.Where(r => r.Status != ProviderRefundStatus.Failed && r.Status != ProviderRefundStatus.Canceled && r.Reference != reference)
            .Sum(r => r.Amount.ToMinorUnits());
        if (alreadyRefunded + amount.ToMinorUnits() > payment.Amount.ToMinorUnits())
            throw new SparkValidationException("commercePayments.refundExceedsPayment");

        var provider = registry.Get(payment.Provider);
        var merchant = await MerchantOfAsync(payment, cancellationToken);
        var refundsFee = refundApplicationFee && payment.ApplicationFee is not null;
        var snapshot = await provider.RefundAsync(new RefundRequest
        {
            Merchant = merchant,
            ProviderPaymentId = payment.ProviderPaymentId,
            Amount = amount,
            Reference = reference,
            IdempotencyKey = key,
            Description = reference,
            RefundApplicationFee = refundsFee,
        }, cancellationToken);

        var now = time.GetUtcNow().UtcDateTime;
        ProviderRefundStatus? previous = recorded?.Status;
        var refund = recorded ?? new ProviderPaymentRefund { Reference = reference, RequestedAtUtc = now };
        if (recorded is null)
            payment.Refunds.Add(refund);
        refund.ProviderRefundId = snapshot.ProviderRefundId;
        refund.Amount = amount;
        refund.Status = snapshot.Status;
        refund.FailureReason = snapshot.FailureReason;
        refund.FailureMessage = snapshot.FailureMessage;
        refund.IdempotencyKey = key;
        refund.ApplicationFeeRefunded = refundsFee;
        refund.UpdatedAtUtc = now;
        payment.UpdatedAtUtc = now;

        await processor.AfterRefundChangeAsync(session, payment, refund, time.GetUtcNow(), cancellationToken);
        await processor.AuditAsync(session, PaymentEventProcessor.AuditActions.RefundRequested, payment.Id!, payment.SellerOrganizationId, amount, $"{reference}: {refund.Status}", cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        foreach (var recipient in services.GetServices<IPaymentEventRecipient>())
            await recipient.OnRefundChangedAsync(new RefundChangeNotice(payment, refund, previous), cancellationToken);
        return refund;
    }

    public Task ProcessEventsAsync(IReadOnlyList<PaymentProviderEvent> events, CancellationToken cancellationToken = default)
        => processor.ProcessAsync(events, cancellationToken);

    public async Task<DisputeEvidencePack> AssembleEvidenceAsync(string disputeId, CancellationToken cancellationToken = default)
    {
        PaymentDispute dispute;
        using (var session = store.OpenAsyncSession())
            dispute = await session.LoadAsync<PaymentDispute>(disputeId, cancellationToken) ?? throw new KeyNotFoundException($"No dispute '{disputeId}'.");

        var request = new DisputeEvidenceRequest(dispute.Id!, dispute.Provider, dispute.ProviderPaymentId, dispute.Reference, dispute.SellerOrganizationId);
        var sections = new List<DisputeEvidenceSection>
        {
            new("commerce-payments.dispute", "Dispute",
            [
                $"Provider: {dispute.Provider}, dispute {dispute.ProviderDisputeId}, payment {dispute.ProviderPaymentId}",
                $"Amount: {dispute.Amount.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)} {dispute.Amount.Currency}",
                $"Reason: {dispute.ReasonText ?? dispute.Reason ?? "-"}",
                $"Opened: {dispute.OpenedAtUtc:u}",
            ]),
        };
        foreach (var contributor in services.GetServices<IDisputeEvidenceContributor>())
        {
            if (await contributor.ContributeAsync(request, cancellationToken) is { } section)
                sections.Add(section);
        }

        var fields = new Dictionary<DisputeEvidenceField, string>();
        foreach (var section in sections)
            foreach (var (field, value) in section.Fields ?? new Dictionary<DisputeEvidenceField, string>())
                fields.TryAdd(field, value);

        return new DisputeEvidencePack
        {
            DisputeId = dispute.Id!,
            OrderReference = dispute.Reference,
            AssembledAt = time.GetUtcNow(),
            Sections = sections,
            Fields = fields,
        };
    }

    public async Task<DisputeEvidenceSubmission> SubmitEvidenceAsync(string disputeId, string actingUserId, CancellationToken cancellationToken = default)
    {
        var pack = await AssembleEvidenceAsync(disputeId, cancellationToken);
        using var session = store.OpenAsyncSession();
        var dispute = (await session.LoadAsync<PaymentDispute>(disputeId, cancellationToken))!;
        if (dispute.IsClosed || dispute.Status == DisputeStatus.UnderReview)
            return DisputeEvidenceSubmission.Closed;

        var provider = registry.Get(dispute.Provider);
        if (!provider.Capabilities.DisputeEvidenceByApi)
            return DisputeEvidenceSubmission.ManualOnly;

        var merchant = dispute.SellerOrganizationId is null || dispute.AccountId is null
            ? MerchantContext.Platform(dispute.Provider)
            : await merchants.ForSellerAsync(dispute.Provider, dispute.SellerOrganizationId, cancellationToken);
        var result = await provider.SubmitDisputeEvidenceAsync(merchant, dispute.ProviderDisputeId, pack, cancellationToken);
        if (result == DisputeEvidenceSubmission.Submitted)
        {
            var previous = dispute.Status;
            dispute.EvidenceSubmittedAtUtc = time.GetUtcNow().UtcDateTime;
            dispute.EvidenceSubmittedBy = actingUserId;
            dispute.Status = DisputeStatus.UnderReview;
            dispute.UpdatedAtUtc = dispute.EvidenceSubmittedAtUtc.Value;
            await processor.AuditAsync(session, PaymentEventProcessor.AuditActions.DisputeStatus, dispute.Id!, dispute.SellerOrganizationId, dispute.Amount, "evidence submitted", cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
            await processor.AnnounceDisputeAsync(dispute, previous, cancellationToken);
        }
        return result;
    }

    public async Task<PaymentDispute> AcceptDisputeAsync(string disputeId, string actingUserId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var dispute = await session.LoadAsync<PaymentDispute>(disputeId, cancellationToken) ?? throw new KeyNotFoundException($"No dispute '{disputeId}'.");
        if (dispute.IsClosed)
            return dispute;
        var previous = dispute.Status;
        dispute.Status = DisputeStatus.Lost;
        dispute.AcceptedBy = actingUserId;
        dispute.ClosedAtUtc = time.GetUtcNow().UtcDateTime;
        dispute.UpdatedAtUtc = dispute.ClosedAtUtc.Value;
        await processor.AuditAsync(session, PaymentEventProcessor.AuditActions.DisputeStatus, dispute.Id!, dispute.SellerOrganizationId, dispute.Amount, $"{previous} -> Lost (accepted)", cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        await processor.AnnounceDisputeAsync(dispute, previous, cancellationToken);
        return dispute;
    }

    /// <summary>
    /// Closes as lost every chargeback a provider will never decide (no evidence API, no deadline) once it is older than
    /// <see cref="DisputeOptions.ChargebackFinalAfter"/> and was not reversed. Run by <c>ChargebackFinalityJob</c>.
    /// </summary>
    internal async Task<int> CloseStaleChargebacksAsync(CancellationToken cancellationToken)
    {
        var manualProviders = registry.All.Where(p => !p.Capabilities.DisputeEvidenceByApi).Select(p => p.Name).ToArray();
        if (manualProviders.Length == 0)
            return 0;
        var cutoff = time.GetUtcNow().UtcDateTime - options.CurrentValue.Disputes.ChargebackFinalAfter;

        List<PaymentDispute> stale;
        using (var session = store.OpenAsyncSession())
            stale = await session.Query<PaymentDispute>()
                .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
                .Where(d => d.Provider.In(manualProviders) && d.Status == DisputeStatus.NeedsResponse && d.OpenedAtUtc < cutoff)
                .Take(256)
                .ToListAsync(cancellationToken);

        foreach (var dispute in stale)
            await AcceptDisputeAsync(dispute.Id!, "system:finality", cancellationToken);
        return stale.Count;
    }
}
