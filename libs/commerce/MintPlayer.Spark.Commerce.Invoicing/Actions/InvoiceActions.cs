using System.Linq.Expressions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Commerce.Invoicing.Models;
using MintPlayer.Spark.Commerce.Invoicing.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Commerce.Invoicing.Actions;

// Invoices are written by the library only, once (§4.15 "never altered"): only QueryRead is granted, and the rows are
// narrowed by InvoicingAccess (the buyer, the seller's Owners/Admins/finance, the platform organization).

public partial class InvoiceActions : DefaultPersistentObjectActions<Invoice>
{
    [Inject] private readonly InvoicingAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>Invoices</c>, under the row rule.</summary>
    public IQueryable<Invoice> Invoices() => session.Query<Invoice>();

    public override async Task<Expression<Func<Invoice, bool>>?> GetRowFilterAsync(string action)
        => await access.InvoiceFilterAsync();
}

public partial class InvoiceTransmissionActions : DefaultPersistentObjectActions<InvoiceTransmission>
{
    [Inject] private readonly InvoicingAccess access;
    [Inject] private readonly IAsyncDocumentSession session;

    /// <summary>The query <c>InvoiceTransmissions</c> (the Peppol queue), under the row rule.</summary>
    public IQueryable<InvoiceTransmission> InvoiceTransmissions() => session.Query<InvoiceTransmission>();

    public override async Task<Expression<Func<InvoiceTransmission, bool>>?> GetRowFilterAsync(string action)
        => await access.TransmissionFilterAsync();
}
