using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Payments.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Payments.Services;

/// <summary>
/// Imports settlements (GB4) through each adapter's <see cref="ISettlementSource"/> and matches every line to our
/// records: a payment line to a <see cref="ProviderPayment"/> (gross equal to the payment amount), a refund line to its
/// refund, a chargeback to a <see cref="PaymentDispute"/>, an application fee to the fee ledger. Unmatched lines and
/// amount differences are queued as <see cref="ReconciliationItem"/>s for <c>finance</c>. Each line is imported once
/// (natural id); a cursor per account remembers how far the import got.
/// </summary>
internal sealed class SettlementReconciler(
    IDocumentStore store,
    PaymentProviderRegistry registry,
    IPaymentMerchantResolver merchants,
    IOptionsMonitor<SparkCommercePaymentsOptions> options,
    IOptions<SparkCommerceOptions> commerce,
    TimeProvider time,
    ILogger<SettlementReconciler> logger)
{
    /// <summary>Imports every source for the platform account and, in connected mode, every linked seller.</summary>
    public async Task<int> ImportAllAsync(CancellationToken cancellationToken)
    {
        var imported = 0;
        foreach (var source in registry.SettlementSources)
        {
            var accounts = new List<MerchantContext> { MerchantContext.Platform(source.Provider) };
            if (commerce.Value.Merchant == MerchantMode.ConnectedSellers && options.CurrentValue.Settlements.IncludeConnectedAccounts)
            {
                List<SellerPaymentAccount> linked;
                using (var session = store.OpenAsyncSession())
                    linked = await session.Query<SellerPaymentAccount>()
                        .Where(a => a.Provider == source.Provider && a.AccountId != null)
                        .Take(1024)
                        .ToListAsync(cancellationToken);
                foreach (var account in linked)
                {
                    try { accounts.Add(await merchants.ForSellerAsync(source.Provider, account.SellerOrganizationId, cancellationToken)); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Settlements of seller {Seller} at {Provider} are skipped.", account.SellerOrganizationId, source.Provider);
                    }
                }
            }

            foreach (var merchant in accounts)
            {
                try { imported += await ImportAsync(source, merchant, cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Settlement import of {Merchant} failed; it is retried on the next run.", merchant);
                }
            }
        }
        return imported;
    }

    /// <summary>Imports one account's new settlements; returns the number of lines stored.</summary>
    public async Task<int> ImportAsync(ISettlementSource source, MerchantContext merchant, CancellationToken cancellationToken)
    {
        var cursorId = SettlementImportCursor.IdFor(source.Provider, merchant.AccountId);
        DateTimeOffset since;
        using (var session = store.OpenAsyncSession())
        {
            var cursor = await session.LoadAsync<SettlementImportCursor>(cursorId, cancellationToken);
            since = cursor is null
                ? time.GetUtcNow() - options.CurrentValue.Settlements.FirstImportLookback
                : new DateTimeOffset(DateTime.SpecifyKind(cursor.LastCreatedAtUtc, DateTimeKind.Utc));
        }

        var stored = 0;
        await foreach (var settlement in source.GetSettlementsAsync(merchant, since, cancellationToken))
        {
            using var session = store.OpenAsyncSession();
            foreach (var line in settlement.Lines)
                stored += await ReconcileLineAsync(session, source.Provider, merchant, settlement, line, cancellationToken) ? 1 : 0;

            var cursor = await session.LoadAsync<SettlementImportCursor>(cursorId, cancellationToken) ?? new SettlementImportCursor { Id = cursorId };
            if (settlement.CreatedAt.UtcDateTime > cursor.LastCreatedAtUtc)
                cursor.LastCreatedAtUtc = settlement.CreatedAt.UtcDateTime;
            cursor.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
            await session.StoreAsync(cursor, cursorId, cancellationToken);
            // One settlement per save: a crash halfway re-imports at most this settlement, whose lines are idempotent.
            await session.SaveChangesAsync(cancellationToken);
        }
        return stored;
    }

    /// <summary>Stores and matches one line in <paramref name="session"/>; false when it was imported before.</summary>
    internal async Task<bool> ReconcileLineAsync(IAsyncDocumentSession session, string provider, MerchantContext merchant, ProviderSettlement settlement, ProviderSettlementLine line, CancellationToken cancellationToken)
    {
        var id = SettlementLine.IdFor(provider, line.LineId);
        if (await session.LoadAsync<SettlementLine>(id, cancellationToken) is not null)
            return false;

        var record = new SettlementLine
        {
            Id = id,
            Provider = provider,
            AccountId = merchant.AccountId,
            SellerOrganizationId = merchant.SellerOrganizationId,
            SettlementId = settlement.SettlementId,
            SettlementReference = settlement.Reference,
            LineId = line.LineId,
            Kind = line.Kind,
            SourceId = line.SourceId,
            Gross = line.Gross,
            Fee = line.Fee,
            Net = line.Net,
            OccurredAtUtc = line.OccurredAt.UtcDateTime,
            Description = line.Description,
            ImportedAtUtc = time.GetUtcNow().UtcDateTime,
        };
        (record.Match, record.MatchedDocumentId, record.ExpectedAmount) = await MatchAsync(session, provider, line, cancellationToken);
        await session.StoreAsync(record, id, cancellationToken);

        if (record.Match is SettlementMatch.Unmatched or SettlementMatch.AmountMismatch)
        {
            var itemId = ReconciliationItem.IdFor(provider, "line-" + line.LineId);
            await session.StoreAsync(new ReconciliationItem
            {
                Id = itemId,
                Provider = provider,
                SellerOrganizationId = merchant.SellerOrganizationId,
                Problem = record.Match == SettlementMatch.Unmatched ? ReconciliationProblem.Unmatched : ReconciliationProblem.AmountMismatch,
                Summary = record.Match == SettlementMatch.Unmatched
                    ? $"{line.Kind} {line.SourceId ?? line.LineId} in settlement {settlement.SettlementId} matches nothing recorded."
                    : $"{line.Kind} {line.SourceId} in settlement {settlement.SettlementId} is {Format(line.Gross)}; recorded {Format(record.ExpectedAmount!)}.",
                SettlementId = settlement.SettlementId,
                SettlementLineId = id,
                SourceId = line.SourceId,
                DocumentId = record.MatchedDocumentId,
                Amount = line.Gross,
                ExpectedAmount = record.ExpectedAmount,
                CreatedAtUtc = time.GetUtcNow().UtcDateTime,
            }, itemId, cancellationToken);
        }
        return true;
    }

    private static string Format(Money money) => $"{money.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)} {money.Currency}";

    /// <summary>The match of one line. Signs are ignored: providers book refunds and debits as negative lines.</summary>
    internal static async Task<(SettlementMatch Match, string? DocumentId, Money? Expected)> MatchAsync(IAsyncDocumentSession session, string provider, ProviderSettlementLine line, CancellationToken cancellationToken)
    {
        switch (line.Kind)
        {
            case SettlementLineKind.Payout:
            case SettlementLineKind.ProviderFee:
            case SettlementLineKind.Transfer:
                return (SettlementMatch.NotApplicable, null, null);
        }
        if (line.SourceId is not { Length: > 0 } source)
            return (line.Kind == SettlementLineKind.Other ? SettlementMatch.NotApplicable : SettlementMatch.Unmatched, null, null);

        switch (line.Kind)
        {
            case SettlementLineKind.Payment:
            {
                var payment = await PaymentEventProcessor.FindPaymentAsync(session, provider, source, null, cancellationToken);
                if (payment is null)
                    return (SettlementMatch.Unmatched, null, null);
                return (SameAbs(payment.Amount, line.Gross) ? SettlementMatch.Matched : SettlementMatch.AmountMismatch, payment.Id, payment.Amount);
            }
            case SettlementLineKind.Refund:
            {
                var payment = await session.Query<ProviderPayment>()
                    .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
                    .Where(p => p.Provider == provider && p.Refunds.Any(r => r.ProviderRefundId == source))
                    .FirstOrDefaultAsync(cancellationToken);
                var refund = payment?.Refunds.FirstOrDefault(r => r.ProviderRefundId == source);
                if (payment is null || refund is null)
                    return (SettlementMatch.Unmatched, null, null);
                return (SameAbs(refund.Amount, line.Gross) ? SettlementMatch.Matched : SettlementMatch.AmountMismatch, payment.Id, refund.Amount);
            }
            case SettlementLineKind.Chargeback:
            case SettlementLineKind.ChargebackReversal:
            {
                var dispute = await session.LoadAsync<PaymentDispute>(PaymentDispute.IdFor(provider, source), cancellationToken);
                if (dispute is null)
                    return (SettlementMatch.Unmatched, null, null);
                return (SameAbs(dispute.Amount, line.Gross) ? SettlementMatch.Matched : SettlementMatch.AmountMismatch, dispute.Id, dispute.Amount);
            }
            case SettlementLineKind.ApplicationFee:
            case SettlementLineKind.ApplicationFeeRefund:
            {
                var kind = line.Kind == SettlementLineKind.ApplicationFee ? PlatformFeeEntryKind.ApplicationFee : PlatformFeeEntryKind.ApplicationFeeRefund;
                var entry = await session.LoadAsync<PlatformFeeEntry>(PlatformFeeEntry.IdFor(kind, provider, source), cancellationToken);
                if (entry is null)
                {
                    // Stripe's application-fee line names the fee object (fee_…), the ledger books the payment: look it up by payment.
                    var payment = await PaymentEventProcessor.FindPaymentAsync(session, provider, source, null, cancellationToken);
                    entry = payment is null ? null : await session.LoadAsync<PlatformFeeEntry>(PlatformFeeEntry.IdFor(kind, provider, payment.ProviderPaymentId), cancellationToken);
                }
                if (entry is null)
                    return (SettlementMatch.Unmatched, null, null);
                return (SameAbs(entry.Amount, line.Gross) ? SettlementMatch.Matched : SettlementMatch.AmountMismatch, entry.Id, entry.Amount);
            }
            default:
                return (SettlementMatch.NotApplicable, null, null);
        }
    }

    private static bool SameAbs(Money recorded, Money line)
        => recorded.Currency == line.Currency && Math.Abs(recorded.ToMinorUnits()) == Math.Abs(line.ToMinorUnits());

    /// <summary>Resolves a queue item with a note (the <c>ResolveReconciliation</c> action).</summary>
    public async Task<bool> ResolveAsync(string itemId, string note, string actingUserId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var item = await session.LoadAsync<ReconciliationItem>(itemId, cancellationToken);
        if (item is null || item.Status == ReconciliationStatus.Resolved)
            return false;
        item.Status = ReconciliationStatus.Resolved;
        item.ResolvedBy = actingUserId;
        item.ResolvedAtUtc = time.GetUtcNow().UtcDateTime;
        item.ResolutionNote = note;
        await session.SaveChangesAsync(cancellationToken);
        return true;
    }
}
