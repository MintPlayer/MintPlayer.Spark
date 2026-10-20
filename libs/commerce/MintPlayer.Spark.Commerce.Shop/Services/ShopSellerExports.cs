using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using MintPlayer.Spark.Commerce.Shop.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Shop.Services;

/// <summary>
/// GB14 (PRD §4.15): the seller's export of its shop orders, kind <c>shop-orders</c>: one row per order line of the
/// seller's own <see cref="ShopOrder"/>s, with what shipped and where to (the seller is the controller of that data for
/// its own sales). Buyer contact details are in Checkout's <c>buyers</c> export. Commerce checks the caller's role in
/// the seller and audits the export; the source scopes by seller itself.
/// </summary>
internal sealed class ShopOrdersExport(IDocumentStore store) : ISellerExportSource
{
    public string Kind => "shop-orders";

    public IReadOnlyList<string> Columns { get; } =
    [
        "order", "placedAt", "state", "fulfilment", "method", "carrier", "shipToName", "shipToPostalCode", "shipToCity", "shipToCountry",
        "line", "sku", "item", "quantity", "unitPrice", "currency", "cancelled", "shippedQuantity", "shippedAt", "deliveredAt",
    ];

    public async IAsyncEnumerable<IReadOnlyList<string?>> GetRowsAsync(string organizationId, DateTimeOffset? from, DateTimeOffset? to, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Expression<Func<ShopOrder, bool>> after = o => o.CreatedAt >= from!.Value, before = o => o.CreatedAt < to!.Value;
        await foreach (var order in ShopSellerExportPages.ReadAsync<ShopOrder>(store, o => o.Id, o => o.OrganizationId == organizationId, from is null ? null : after, to is null ? null : before, cancellationToken))
        {
            if (!string.Equals(order.OrganizationId, organizationId, StringComparison.Ordinal))
                continue; // never widen: the scope is checked again on the document itself
            foreach (var line in order.Lines)
                yield return
                [
                    order.OrderNumber, ShopSellerExportPages.At(order.CreatedAt), order.State.ToString(), order.Fulfilment.ToString(), order.Method, order.Carrier,
                    order.ShipTo?.Name, order.ShipTo?.PostalCode, order.ShipTo?.City, order.ShipTo?.Country,
                    line.LineId, line.Sku, line.Title?.GetValue("en") ?? line.ItemId, ShopSellerExportPages.Number(line.Quantity),
                    line.UnitPrice?.Amount.ToString(CultureInfo.InvariantCulture), order.Currency, line.Cancelled ? "true" : "false",
                    ShopSellerExportPages.Number(line.ShippedQuantity), ShopSellerExportPages.At(line.ShippedAt), ShopSellerExportPages.At(line.DeliveredAt),
                ];
        }
    }
}

/// <summary>
/// GB14 (PRD §4.15): the seller's export of the reviews of its own products, kind <c>shop-reviews</c> (GC5): every
/// status, so the seller sees what was published, pending and rejected (and why). The author's account id is the
/// platform's and is not exported; the display name the buyer chose is.
/// </summary>
internal sealed class ShopReviewsExport(IDocumentStore store) : ISellerExportSource
{
    public string Kind => "shop-reviews";

    public IReadOnlyList<string> Columns { get; } =
    [
        "review", "product", "variant", "order", "line", "postedAt", "displayName", "rating", "title", "body", "language",
        "incentivised", "status", "decidedAt", "rejectionReason",
    ];

    public async IAsyncEnumerable<IReadOnlyList<string?>> GetRowsAsync(string organizationId, DateTimeOffset? from, DateTimeOffset? to, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Expression<Func<ShopProductReview, bool>> after = r => r.PostedAt >= from!.Value, before = r => r.PostedAt < to!.Value;
        await foreach (var review in ShopSellerExportPages.ReadAsync<ShopProductReview>(store, r => r.Id, r => r.OrganizationId == organizationId, from is null ? null : after, to is null ? null : before, cancellationToken))
        {
            if (!string.Equals(review.OrganizationId, organizationId, StringComparison.Ordinal))
                continue;
            yield return
            [
                review.Id, review.ProductId, review.VariantId, review.OrderId, review.LineId, ShopSellerExportPages.At(review.PostedAt), review.DisplayName,
                ShopSellerExportPages.Number(review.Rating), review.Title, review.Body, review.Language,
                review.Incentivised ? "true" : "false", review.Status.ToString(), ShopSellerExportPages.At(review.DecidedAt), review.RejectionReason?.ToString(),
            ];
        }
    }
}

/// <summary>Reads a seller's documents page by page (by id, non-stale), one short session per page, as the attendee export does.</summary>
internal static class ShopSellerExportPages
{
    private const int PageSize = 256;

    public static async IAsyncEnumerable<T> ReadAsync<T>(IDocumentStore store, Expression<Func<T, string?>> id, Expression<Func<T, bool>> scope, Expression<Func<T, bool>>? after, Expression<Func<T, bool>>? before,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var skip = 0;
        while (true)
        {
            List<T> page;
            using (var session = store.OpenAsyncSession())
            {
                var query = session.Query<T>().Customize(c => c.WaitForNonStaleResults()).Where(scope);
                if (after is not null)
                    query = query.Where(after);
                if (before is not null)
                    query = query.Where(before);
                page = await query.OrderBy(id).Skip(skip).Take(PageSize).ToListAsync(cancellationToken);
            }
            foreach (var item in page)
                yield return item;
            if (page.Count < PageSize)
                yield break;
            skip += page.Count;
        }
    }

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string? At(DateTimeOffset? value) => value?.ToString("o", CultureInfo.InvariantCulture);
}
