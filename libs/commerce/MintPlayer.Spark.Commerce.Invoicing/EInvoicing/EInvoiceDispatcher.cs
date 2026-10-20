using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Commerce.Invoicing.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Invoicing.EInvoicing;

/// <summary>
/// Sends business invoices and credit notes over Peppol (§4.8): writes the UBL, validates it (every
/// <see cref="IEInvoiceValidator"/>), finds the receiver, hands it to the access point, and records the outcome on an
/// <see cref="InvoiceTransmission"/> (the invoice itself is never touched). A transmission is claimed with optimistic
/// concurrency before each attempt, so two hosts never send one document twice; transient failures retry with backoff
/// up to <see cref="PeppolOptions.MaxAttempts"/>.
/// </summary>
internal sealed class EInvoiceDispatcher(
    IDocumentStore store,
    IEnumerable<IPeppolAccessPoint> accessPoints,
    IEnumerable<IEInvoiceValidator> validators,
    IOptionsMonitor<SparkCommerceInvoicingOptions> options,
    TimeProvider time,
    ILogger<EInvoiceDispatcher> logger)
{
    public IPeppolAccessPoint? AccessPoint
    {
        get
        {
            var all = accessPoints.ToList();
            if (options.CurrentValue.Peppol.Provider is { Length: > 0 } name)
                return all.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            return all.Count == 1 ? all[0] : null;
        }
    }

    /// <summary>Whether <paramref name="invoice"/> goes over Peppol: a business document of an issuer that has an access point.</summary>
    public bool Applies(Invoice invoice)
        => options.CurrentValue.Peppol.SendBusinessInvoices && invoice.Business && invoice.Kind != InvoiceKind.Receipt && AccessPoint is not null;

    /// <summary>Creates the transmission (once) and makes the first attempt.</summary>
    public async Task QueueAsync(Invoice invoice, CancellationToken cancellationToken)
    {
        if (!Applies(invoice) || invoice.Id is null)
            return;
        var id = InvoiceTransmission.IdFor(invoice.Id);
        using (var session = store.OpenAsyncSession())
        {
            if (await session.LoadAsync<InvoiceTransmission>(id, cancellationToken) is not null)
                return;
            await session.StoreAsync(new InvoiceTransmission
            {
                Id = id, InvoiceId = invoice.Id, InvoiceNumber = invoice.Number, SellerOrganizationId = invoice.SellerOrganizationId,
                Provider = AccessPoint!.Name, Status = InvoiceTransmissionStatus.Pending, UpdatedAt = time.GetUtcNow(), NextAttemptAt = time.GetUtcNow(),
            }, changeVector: string.Empty, id, cancellationToken);
            try { await session.SaveChangesAsync(cancellationToken); }
            catch (ConcurrencyException) { return; }
        }
        await TrySendAsync(id, cancellationToken);
    }

    /// <summary>The retry job: every pending transmission whose next attempt is due.</summary>
    public async Task RetryPendingAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        List<string> due;
        using (var session = store.OpenAsyncSession())
        {
            due = await session.Query<InvoiceTransmission>()
                .Where(t => t.Status == InvoiceTransmissionStatus.Pending && t.NextAttemptAt <= now)
                .Select(t => t.Id!)
                .Take(200)
                .ToListAsync(cancellationToken);
        }
        foreach (var id in due)
            await TrySendAsync(id, cancellationToken);
    }

    /// <summary>One attempt. Returns the transmission's state after it.</summary>
    public async Task<InvoiceTransmission?> TrySendAsync(string transmissionId, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue.Peppol;
        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        var transmission = await session.LoadAsync<InvoiceTransmission>(transmissionId, cancellationToken);
        if (transmission is null || transmission.Status != InvoiceTransmissionStatus.Pending)
            return transmission;
        var invoice = await session.LoadAsync<Invoice>(transmission.InvoiceId, cancellationToken);
        var accessPoint = accessPoints.FirstOrDefault(a => string.Equals(a.Name, transmission.Provider, StringComparison.OrdinalIgnoreCase));
        var now = time.GetUtcNow();
        transmission.UpdatedAt = now;

        if (invoice is null || accessPoint is null)
        {
            transmission.Status = InvoiceTransmissionStatus.Failed;
            transmission.LastError = invoice is null ? "The invoice no longer exists." : $"The access point '{transmission.Provider}' is not registered.";
            return await SaveAsync();
        }

        // Claim the attempt first: a concurrent sender's save fails here, before anything is sent.
        transmission.Attempts++;
        transmission.NextAttemptAt = now + Backoff(transmission.Attempts);
        try { await session.SaveChangesAsync(cancellationToken); }
        catch (ConcurrencyException) { return null; }

        PeppolParticipant sender;
        if (PeppolParticipant.TryParse(invoice.Issuer.PeppolId, out var configuredSender))
            sender = configuredSender;
        else if (PeppolParticipant.FromBelgianVatId(invoice.Issuer.VatId) is { } derivedSender)
            sender = derivedSender;
        else
        {
            transmission.Status = InvoiceTransmissionStatus.Failed;
            transmission.LastError = "The issuer has no Peppol participant id (Spark:Commerce:Invoicing:Platform:PeppolId or the seller's invoicing profile).";
            return await SaveAsync();
        }

        if (!PeppolParticipant.TryParse(invoice.Buyer.PeppolId, out var receiver))
        {
            if (PeppolParticipant.FromBelgianVatId(invoice.Buyer.VatId) is { } derived)
                receiver = derived;
            else
            {
                transmission.Status = InvoiceTransmissionStatus.NotReachable;
                transmission.LastError = "The buyer has no Peppol participant id.";
                return await SaveAsync();
            }
        }
        transmission.ReceiverId = receiver.ToString();
        var documentType = invoice.IsCredit ? UblWriter.CreditNoteDocumentType : UblWriter.InvoiceDocumentType;

        var ubl = UblWriter.Write(invoice);
        var violations = new List<EInvoiceRuleViolation>();
        foreach (var validator in validators)
            violations.AddRange(await validator.ValidateAsync(ubl, cancellationToken));
        if (violations.Count > 0)
        {
            transmission.Status = InvoiceTransmissionStatus.Failed;
            transmission.ValidationErrors = violations.Select(v => $"{v.Rule}: {v.Message}").ToList();
            transmission.LastError = "The UBL breaks EN 16931 rules; it was not sent.";
            return await SaveAsync();
        }

        try
        {
            if (settings.DiscoverReceivers)
            {
                var lookup = await accessPoint.LookupAsync(receiver, documentType, cancellationToken);
                if (!lookup.CanReceive)
                {
                    transmission.Status = InvoiceTransmissionStatus.NotReachable;
                    transmission.LastError = lookup.Error ?? "The receiver is not registered on Peppol for this document type.";
                    return await SaveAsync();
                }
            }
            var result = await accessPoint.SendAsync(new PeppolSendRequest(invoice.Id!, invoice.Number, documentType, ubl, sender, receiver, invoice.IssuerKey), cancellationToken);
            if (result.Accepted)
            {
                transmission.Status = InvoiceTransmissionStatus.Sent;
                transmission.ProviderReference = result.ProviderReference;
                transmission.LastError = null;
            }
            else
            {
                transmission.LastError = result.Error;
                if (!result.Retryable || transmission.Attempts >= settings.MaxAttempts)
                    transmission.Status = InvoiceTransmissionStatus.Failed;
            }
        }
        catch (Exception ex) when (ex is PeppolException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Peppol transmission of {Invoice} failed (attempt {Attempt}): {Error}", invoice.Number, transmission.Attempts, ex.Message);
            transmission.LastError = ex.Message;
            if (transmission.Attempts >= settings.MaxAttempts)
                transmission.Status = InvoiceTransmissionStatus.Failed;
        }
        return await SaveAsync();

        async Task<InvoiceTransmission?> SaveAsync()
        {
            transmission.UpdatedAt = time.GetUtcNow();
            try { await session.SaveChangesAsync(cancellationToken); }
            catch (ConcurrencyException) { return null; }
            return transmission;
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMinutes(Math.Min(24 * 60, 5 * Math.Pow(2, Math.Max(0, attempt - 1))));
}
