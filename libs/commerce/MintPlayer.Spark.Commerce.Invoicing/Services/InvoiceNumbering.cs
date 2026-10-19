using System.Globalization;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.Commerce.Invoicing.Models;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Invoicing.Services;

/// <summary>
/// Gap-free numbering per issuer, series and year (§4.8, D19), over the claims primitive (X12): the counter is a
/// compare-exchange value, and the document that uses a number is written in the <b>same cluster-wide unit of work</b>
/// that moves the counter. Either both commit or neither does, so a number exists exactly when its document does: no
/// gaps after a crash, no duplicates under concurrency (a concurrent issuer's commit fails and is retried with the next
/// number). The document id is derived from what caused it, so a retry after a lost answer finds the issued document
/// instead of numbering it twice.
/// </summary>
internal sealed class InvoiceNumbering(ISparkClaims claims, IOptionsMonitor<SparkCommerceInvoicingOptions> options)
{
    private const string Owner = "commerce-invoicing";

    internal static readonly SparkClaimKind<InvoiceCounter> Counters = new("commerce-invoicing/numbers/", c => c.Owner);

    /// <summary>How often a commit lost to a concurrent issuer is retried.</summary>
    internal const int MaxAttempts = 64;

    public string Prefix(InvoiceKind kind) => kind switch
    {
        InvoiceKind.Receipt => options.CurrentValue.Series.Receipt,
        InvoiceKind.CreditNote => options.CurrentValue.Series.CreditNote,
        InvoiceKind.FeeInvoice => options.CurrentValue.Series.FeeInvoice,
        InvoiceKind.FeeCreditNote => options.CurrentValue.Series.FeeCreditNote,
        _ => options.CurrentValue.Series.Invoice,
    };

    /// <summary>The counter key of an issuer, series and year. Issuer keys are organization ids or <c>platform</c>.</summary>
    internal static string CounterName(string issuerKey, string series, int year)
        => $"{Invoice.Hash(issuerKey)}/{series}/{year.ToString(CultureInfo.InvariantCulture)}";

    public string Format(string series, int year, long sequence)
        => $"{series}{year.ToString(CultureInfo.InvariantCulture)}-{sequence.ToString(new string('0', Math.Clamp(options.CurrentValue.Series.Digits, 1, 12)), CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Issues one document. <paramref name="build"/> runs inside the unit of work (it may load and store further
    /// documents through the session, such as the distance-sales total) and returns the document without its number,
    /// or null to issue nothing. Returns the stored document (<c>Created</c>), or the one issued earlier under the same id.
    /// </summary>
    public async Task<(Invoice? Invoice, bool Created)> IssueAsync(
        string invoiceId,
        string issuerKey,
        InvoiceKind kind,
        int year,
        Func<IAsyncDocumentSession, Task<Invoice?>> build,
        CancellationToken cancellationToken)
    {
        var series = Prefix(kind);
        var key = Counters.Key(CounterName(issuerKey, series, year));
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var uow = claims.BeginUnitOfWork(atomicDocumentWrites: true);
            var existing = await uow.Session.LoadAsync<Invoice>(invoiceId, cancellationToken);
            if (existing is not null)
                return (existing, false);

            var invoice = await build(uow.Session);
            if (invoice is null)
                return (null, false);

            var next = await uow.UpdateAsync(Counters, key, Owner, c => c with { Last = c.Last + 1 }, cancellationToken);
            if (next.Status == SparkClaimStatus.NotYours)
                next = await uow.ReserveAsync(Counters, key, Owner, previous => previous is null ? new InvoiceCounter(Owner, 1) : null, cancellationToken: cancellationToken);
            if (!next.Holds || next.Claim is null)
                throw new InvalidOperationException($"The invoice counter '{key}' is held by another owner.");

            invoice.Id = invoiceId;
            invoice.Kind = kind;
            invoice.IssuerKey = issuerKey;
            invoice.Series = series;
            invoice.Year = year;
            invoice.Sequence = next.Claim.Value.Last;
            invoice.Number = Format(series, year, invoice.Sequence);
            invoice.ContentHash = "";
            invoice.ContentHash = InvoiceHash.Compute(invoice);
            await uow.Session.StoreAsync(invoice, invoiceId, cancellationToken);

            // CommitAsync answers false when a key or guarded document it read changed meanwhile (a concurrent issuer).
            if (await uow.CommitAsync(cancellationToken))
                return (invoice, true);
        }
        throw new InvalidOperationException($"Could not number invoice '{invoiceId}' after {MaxAttempts} attempts: the issuer's counter keeps changing.");
    }
}

/// <summary>The SHA-256 of an invoice as issued (§4.15 "never altered": a later change is detectable).</summary>
internal static class InvoiceHash
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string Compute(Invoice invoice)
    {
        var saved = invoice.ContentHash;
        invoice.ContentHash = "";
        try
        {
            var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(invoice, Json);
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        }
        finally
        {
            invoice.ContentHash = saved;
        }
    }

    public static bool IsIntact(Invoice invoice) => invoice.ContentHash.Length > 0 && Compute(invoice) == invoice.ContentHash;
}
