using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Commerce.Invoicing.EInvoicing;
using MintPlayer.Spark.Commerce.Invoicing.Models;
using MintPlayer.Spark.Commerce.Payments.Models;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Invoicing.Jobs;

/// <summary>Retries pending Peppol transmissions whose next attempt is due.</summary>
internal sealed partial class InvoiceTransmissionRetryJob : ISparkCronJob
{
    /// <summary>The shipped default; <c>Spark:Commerce:Invoicing:TransmissionRetrySchedule</c> overrides it.</summary>
    public static string CronSchedule => "*/10 * * * *";

    [Inject] private readonly EInvoiceDispatcher dispatcher;

    public Task RunAsync(CancellationToken cancellationToken) => dispatcher.RetryPendingAsync(cancellationToken);
}

/// <summary>
/// On the first of the month, the platform invoices every seller the previous month's fees (§4.8, GB4 fee ledger).
/// Idempotent per seller and month (the document id), so a re-run issues nothing new.
/// </summary>
internal sealed partial class FeeInvoiceJob : ISparkCronJob
{
    /// <summary>The shipped default; <c>Spark:Commerce:Invoicing:FeeInvoices:Schedule</c> overrides it.</summary>
    public static string CronSchedule => "11 3 1 * *";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ISparkInvoicing invoicing;
    [Inject] private readonly IOptionsMonitor<SparkCommerceInvoicingOptions> options;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<FeeInvoiceJob> logger;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!options.CurrentValue.FeeInvoices.Enabled)
            return;
        var now = time.GetUtcNow().UtcDateTime;
        var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
        var end = month.AddMonths(1);
        List<string?> sellers;
        using (var session = store.OpenAsyncSession())
        {
            sellers = await session.Query<PlatformFeeEntry>()
                .Where(e => e.SellerOrganizationId != null && e.OccurredAtUtc >= month && e.OccurredAtUtc < end)
                .Select(e => e.SellerOrganizationId)
                .Take(10_000)
                .ToListAsync(cancellationToken);
        }
        foreach (var seller in sellers.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            try { await invoicing.IssueFeeInvoiceAsync(seller, month.Year, month.Month, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The fee invoice of seller {Seller} for {Year}-{Month} could not be issued.", seller, month.Year, month.Month);
            }
        }
    }
}

/// <summary>
/// Retention (§4.8, §4.15): an invoice or credit note is kept for the period Commerce resolves for
/// <c>invoices</c> (never below the legal floor of the platform's country; Belgium: 10 years per the research, to be
/// confirmed, D37), counted from its issue. When it ends, the document is <b>deleted whole</b> with its credit state,
/// transmission and billing details; it is never anonymised or altered before.
/// </summary>
internal sealed partial class InvoiceRetentionJob : ISparkCronJob
{
    /// <summary>The shipped default; <c>Spark:Commerce:Invoicing:RetentionSchedule</c> overrides it.</summary>
    public static string CronSchedule => "23 4 * * *";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ICommerceRetention retention;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<InvoiceRetentionJob> logger;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var period = retention.GetPeriod(CommerceDocumentTypes.Invoices);
        if (period <= TimeSpan.Zero)
            return;
        var cutoff = time.GetUtcNow() - period;
        for (var batch = 0; batch < 50; batch++)
        {
            List<Invoice> expired;
            using (var query = store.OpenAsyncSession())
            {
                expired = await query.Query<Invoice>()
                    .Where(i => i.IssuedAt < cutoff)
                    .Take(100)
                    .ToListAsync(cancellationToken);
            }
            if (expired.Count == 0)
                return;
            // The documents were written in cluster-wide transactions (with atomic guards): delete them the same way.
            // Loaded in the session first, so the delete is checked against each document's atomic guard.
            using (var session = store.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions { TransactionMode = TransactionMode.ClusterWide }))
            {
                foreach (var invoice in expired)
                {
                    if (await session.LoadAsync<Invoice>(invoice.Id!, cancellationToken) is { } loaded)
                        session.Delete(loaded);
                    if (!invoice.IsCredit && await session.LoadAsync<InvoiceCreditState>(InvoiceCreditState.IdFor(invoice.Id!), cancellationToken) is { } state)
                        session.Delete(state);
                }
                await session.SaveChangesAsync(cancellationToken);
            }
            // Written by ordinary sessions: deleted by one.
            using (var plain = store.OpenAsyncSession())
            {
                foreach (var invoice in expired)
                {
                    plain.Delete(InvoiceTransmission.IdFor(invoice.Id!));
                    if (!invoice.IsCredit && invoice.OrderId is { } orderId)
                        plain.Delete(InvoiceBillingDetails.IdFor(orderId));
                }
                await plain.SaveChangesAsync(cancellationToken);
            }
            logger.LogInformation("Retention: {Count} invoices past their period ({Period}) were deleted whole.", expired.Count, period);
        }
    }
}
