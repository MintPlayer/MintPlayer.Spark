using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Fulfilment.Models;
using MintPlayer.Spark.Commerce.Fulfilment.Services;
using MintPlayer.Spark.Fulfilment.Contracts;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Fulfilment;

/// <summary>
/// The shop side of the fulfilment contracts (commerce PRD §4.9, warehouse PRD §4.3–§4.4), registered by
/// <c>AddCommerceFulfilment()</c>. Everything here also runs on its own (order events, replica notifications, the
/// reconcile); these calls are for staff screens and onboarding.
/// </summary>
public interface ISparkCommerceFulfilment
{
    /// <summary>Re-derives an order's fulfilment now (normally done by the order events and the reconcile).</summary>
    Task SyncAsync(string orderId, CancellationToken cancellationToken = default);

    /// <summary>The shop's bookkeeping of one seller's part of an order at the warehouse, or null.</summary>
    Task<FulfilmentLink?> GetAsync(string orderId, string? sellerOrganizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Staff saw the attention flag: a rejected order no longer blocks a new one (fix the cause, e.g. publish the missing
    /// SKU, then acknowledge, and the order is published again), a refused cancellation is no longer flagged.
    /// </summary>
    Task AcknowledgeAsync(string orderId, string? sellerOrganizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ships the rest of an order to another address (warehouse §4.3 "Edits after acceptance"): an edit is a cancellation
    /// plus a new order, accepted only while the warehouse can still cancel (before a label exists).
    /// </summary>
    Task ChangeAddressAsync(string orderId, string? sellerOrganizationId, FulfilmentAddress address, CancellationToken cancellationToken = default);

    /// <summary>Publishes the catalog items of these variants now (normally done by the catalog sweep and before an order).</summary>
    Task PublishCatalogAsync(IEnumerable<string> variantIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// §4.4 rule 2, at onboarding: the units of a variant this shop sold and shipped itself before the warehouse took over.
    /// <c>sold</c> counts them and the warehouse's <c>ShippedTotal</c> does not, so the capacity adds them back.
    /// </summary>
    Task SetBaselineAsync(string variantId, int unitsShippedBeforeIntegration, CancellationToken cancellationToken = default);

    /// <summary>GA7: the buyers who received units of a lot (from the warehouse's shipments with their lots).</summary>
    Task<IReadOnlyList<FulfilmentRecallBuyer>> TraceRecallAsync(string sku, string lotCode, string? ownerId = null, CancellationToken cancellationToken = default);

    /// <summary>GA7: mails the recall notice to every traced buyer, once per recall and order.</summary>
    Task<FulfilmentRecallResult> SendRecallNoticeAsync(FulfilmentRecallNotice notice, CancellationToken cancellationToken = default);
}

/// <summary>A buyer who received units of a recalled lot.</summary>
/// <param name="FulfilmentOrderIds">The fulfilment orders the lot left in.</param>
/// <param name="Units">Units of the lot shipped to this buyer.</param>
public sealed record FulfilmentRecallBuyer(
    string OrderId,
    string OrderNumber,
    string? SellerOrganizationId,
    IReadOnlyList<string> FulfilmentOrderIds,
    decimal Units,
    string? Email,
    string? Name,
    string? Culture);

/// <summary>A recall notice (GA7).</summary>
/// <param name="RecallId">Stable per recall: a repeated send mails nobody twice.</param>
/// <param name="ProductName">The product as the buyer knows it, per language.</param>
/// <param name="Hazard">The risk, per language.</param>
/// <param name="Action">What the buyer should do (stop using it, return it, the remedy offered), per language.</param>
/// <param name="Contact">Where buyers get more information.</param>
public sealed record FulfilmentRecallNotice(
    string RecallId,
    string Sku,
    string LotCode,
    string? OwnerId = null,
    TranslatedString? ProductName = null,
    TranslatedString? Hazard = null,
    TranslatedString? Action = null,
    string? Contact = null);

/// <summary>What a recall send did.</summary>
/// <param name="Traced">Buyers traced.</param>
/// <param name="Mailed">Notices mailed by this call (earlier sends excluded).</param>
/// <param name="Unreachable">Order numbers without an e-mail address: inform these buyers another way.</param>
public sealed record FulfilmentRecallResult(int Traced, int Mailed, IReadOnlyList<string> Unreachable);

internal sealed class SparkCommerceFulfilment(
    IDocumentStore store,
    FulfilmentSync sync,
    FulfilmentCatalog catalog,
    FulfilmentStock stock,
    FulfilmentRecalls recalls,
    Microsoft.Extensions.Options.IOptionsMonitor<SparkCommerceFulfilmentOptions> options) : ISparkCommerceFulfilment
{
    public Task SyncAsync(string orderId, CancellationToken cancellationToken = default) => sync.SyncOrderAsync(orderId, cancellationToken);

    public async Task<FulfilmentLink?> GetAsync(string orderId, string? sellerOrganizationId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var order = await session.LoadAsync<Checkout.Models.CheckoutOrder>(orderId, cancellationToken);
        return order is null ? null : await session.LoadAsync<FulfilmentLink>(FulfilmentIds.Link(options.CurrentValue.ShopName, order.Number, sellerOrganizationId), cancellationToken);
    }

    public Task AcknowledgeAsync(string orderId, string? sellerOrganizationId, CancellationToken cancellationToken = default)
        => EditAsync(orderId, sellerOrganizationId, link =>
        {
            foreach (var revision in link.Revisions)
                revision.Acknowledged = true;
            link.Attention = null;
        }, cancellationToken);

    public Task ChangeAddressAsync(string orderId, string? sellerOrganizationId, FulfilmentAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        return EditAsync(orderId, sellerOrganizationId, link => link.ShipToOverride = address, cancellationToken);
    }

    private async Task EditAsync(string orderId, string? seller, Action<FulfilmentLink> edit, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var session = store.OpenAsyncSession();
                session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
                var order = await session.LoadAsync<Checkout.Models.CheckoutOrder>(orderId, cancellationToken)
                    ?? throw new InvalidOperationException($"Order '{orderId}' does not exist.");
                var link = await session.LoadAsync<FulfilmentLink>(FulfilmentIds.Link(options.CurrentValue.ShopName, order.Number, seller), cancellationToken)
                    ?? throw new InvalidOperationException($"Order '{order.Number}' was not handed to the warehouse.");
                edit(link);
                link.Closed = false;
                await session.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (ConcurrencyException) when (attempt < 3)
            {
            }
        }
        await sync.SyncAsync(orderId, seller, cancellationToken);
    }

    public Task PublishCatalogAsync(IEnumerable<string> variantIds, CancellationToken cancellationToken = default) => catalog.PublishAsync(variantIds, cancellationToken);

    public Task SetBaselineAsync(string variantId, int unitsShippedBeforeIntegration, CancellationToken cancellationToken = default)
        => stock.SetBaselineAsync(variantId, unitsShippedBeforeIntegration, cancellationToken);

    public Task<IReadOnlyList<FulfilmentRecallBuyer>> TraceRecallAsync(string sku, string lotCode, string? ownerId = null, CancellationToken cancellationToken = default)
        => recalls.TraceAsync(sku, lotCode, ownerId, cancellationToken);

    public Task<FulfilmentRecallResult> SendRecallNoticeAsync(FulfilmentRecallNotice notice, CancellationToken cancellationToken = default)
        => recalls.SendAsync(notice, cancellationToken);
}
