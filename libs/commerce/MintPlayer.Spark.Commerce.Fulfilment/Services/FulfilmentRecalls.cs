using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Fulfilment.Models;
using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.MailManager;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Fulfilment.Services;

/// <summary>
/// GA7: the shop's half of a recall trace (warehouse WM7 traces lot → shipment). The shop holds the warehouse's
/// <c>shipments</c> replicas with their lots, so it resolves a lot to its fulfilment orders, those to its own orders and
/// buyers, and mails each buyer the recall notice once (Regulation (EU) 2023/988 Art. 35(1): "all affected consumers that
/// can be identified are notified directly and without undue delay"; checked against EUR-Lex 02023R0988-20260529,
/// 2026-10-20). ⚠️ Art. 36(2) fixes the notice's content: the headline "Product safety recall", a picture, name and
/// brand, identification numbers, when, where and by whom it was sold, the hazard, the action (including to stop using
/// it), the Art. 37 remedies, a free phone number or online service, and encouragement to share. The host's mail
/// template must carry all of it; <see cref="FulfilmentRecallNotice"/> does not hold every element.
/// </summary>
internal sealed class FulfilmentRecalls(
    IDocumentStore store,
    IServiceProvider services,
    IOptionsMonitor<SparkCommerceFulfilmentOptions> options,
    TimeProvider clock,
    ILogger<FulfilmentRecalls> logger)
{
    public async Task<IReadOnlyList<FulfilmentRecallBuyer>> TraceAsync(string sku, string lotCode, string? ownerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentException.ThrowIfNullOrWhiteSpace(lotCode);
        var shop = options.CurrentValue.ShopName;

        // The units per fulfilment order that came from the lot. LINQ: values are query parameters, never spliced into RQL;
        // the per-line match (SKU and lot on the same line) is checked here, since the index flattens the lines.
        var units = new Dictionary<string, decimal>(StringComparer.Ordinal);
        const int pageSize = 512;
        for (var skip = 0; ; skip += pageSize)
        {
            using var session = store.OpenAsyncSession();
            // A recall must not miss a shipment because an index lags: wait for it to be current.
            var page = await session.Query<FulfilmentShipment>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
                .Where(s => s.Lines.Any(l => l.Lots.Any(x => x.LotCode == lotCode)))
                .OrderBy(s => s.Id)
                .Skip(skip).Take(pageSize)
                .ToListAsync(cancellationToken);
            foreach (var shipment in page)
            {
                if (ownerId is not null && shipment.OwnerId != ownerId)
                    continue;
                var quantity = shipment.Lines.Where(l => l.Sku == sku).SelectMany(l => l.Lots).Where(l => l.LotCode == lotCode).Sum(l => l.Quantity);
                if (quantity > 0)
                    units[shipment.FulfilmentOrderId] = units.GetValueOrDefault(shipment.FulfilmentOrderId) + quantity;
            }
            if (page.Count < pageSize)
                break;
        }

        var byLink = units.Select(kv => (LinkId: FulfilmentIds.LinkOfOrder(shop, kv.Key), FulfilmentOrderId: kv.Key, Units: kv.Value))
            .Where(x => x.LinkId is not null).GroupBy(x => x.LinkId!).ToList();
        var buyers = new List<FulfilmentRecallBuyer>();
        using (var session = store.OpenAsyncSession())
        {
            foreach (var group in byLink)
            {
                var link = await session.Include<FulfilmentLink>(l => l.OrderId).LoadAsync<FulfilmentLink>(group.Key, cancellationToken);
                if (link is null)
                    continue;
                var order = await session.LoadAsync<CheckoutOrder>(link.OrderId, cancellationToken);
                if (order is null)
                    continue;
                string? email = order.BuyerEmail, name = order.BuyerName, culture = null;
                if (order.BuyerUserId is { } userId && await session.LoadAsync<SparkUser>(userId, cancellationToken) is { } user)
                {
                    email ??= user.Email;
                    name ??= user.UserName;
                    culture = user.PreferredCulture;
                }
                buyers.Add(new FulfilmentRecallBuyer(order.Id!, order.Number, link.SellerOrganizationId, group.Select(x => x.FulfilmentOrderId).ToList(),
                    group.Sum(x => x.Units), email, name, culture));
            }
        }
        return buyers;
    }

    public async Task<FulfilmentRecallResult> SendAsync(FulfilmentRecallNotice notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentException.ThrowIfNullOrWhiteSpace(notice.RecallId);
        var buyers = await TraceAsync(notice.Sku, notice.LotCode, notice.OwnerId, cancellationToken);
        if (services.GetService<ISparkMailer>() is not { } mailer)
            throw new InvalidOperationException("Recall notices need MailManager (spark.AddMailManager()).");

        var now = clock.GetUtcNow();
        var recallId = FulfilmentIds.Recall(notice.RecallId);
        using var session = store.OpenAsyncSession();
        var record = await session.LoadAsync<FulfilmentRecall>(recallId, cancellationToken);
        if (record is null)
        {
            record = new FulfilmentRecall { Id = recallId, Sku = notice.Sku, LotCode = notice.LotCode, OwnerId = notice.OwnerId, CreatedAt = now };
            await session.StoreAsync(record, recallId, cancellationToken);
        }

        var mailed = 0;
        var unreachable = new List<string>();
        foreach (var buyer in buyers)
        {
            if (record.NotifiedOrderIds.Contains(buyer.OrderId))
                continue;
            if (string.IsNullOrWhiteSpace(buyer.Email))
            {
                unreachable.Add(buyer.OrderNumber);
                continue;
            }
            var languages = buyer.Culture is { Length: > 0 } c ? new[] { c } : [];
            await mailer.SendAsync(new SparkMailRequest
            {
                Template = options.CurrentValue.RecallMailTemplate,
                To = buyer.Email,
                ToName = buyer.Name,
                Culture = buyer.Culture,
                Data = new
                {
                    number = buyer.OrderNumber,
                    buyer_name = buyer.Name,
                    product = notice.ProductName?.Resolve(languages) ?? notice.Sku,
                    sku = notice.Sku,
                    lot = notice.LotCode,
                    units = buyer.Units,
                    hazard = notice.Hazard?.Resolve(languages),
                    action = notice.Action?.Resolve(languages),
                    contact = notice.Contact,
                },
                // One notice per recall and order, even when the send is repeated.
                DeduplicationKey = $"fulfilment:recall:{notice.RecallId}:{buyer.OrderId}",
            }, cancellationToken);
            record.NotifiedOrderIds.Add(buyer.OrderId);
            mailed++;
        }
        record.UpdatedAt = now;
        await session.SaveChangesAsync(cancellationToken);
        if (unreachable.Count > 0)
            logger.LogWarning("Recall {RecallId}: no e-mail address for orders {Orders}; inform these buyers another way.", notice.RecallId, string.Join(", ", unreachable));
        return new FulfilmentRecallResult(buyers.Count, mailed, unreachable);
    }
}
