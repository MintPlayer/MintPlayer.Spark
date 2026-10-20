using MintPlayer.Spark.Fulfilment.Contracts;
using MintPlayer.Spark.Replication.Abstractions;
using MintPlayer.Spark.Warehouse.Outbound.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Warehouse.Outbound.Services;

/// <summary>
/// The <c>fulfilment-status</c> publication (PRD §4.3): the warehouse's own <see cref="FulfilmentOrderStatus"/> at
/// <c>{fulfilmentOrderId}/status</c>, written in the same session as every change of the <see cref="OutboundOrder"/> and marked
/// for replication (a raw write: no pipeline interceptor runs). Quantities are cumulative and the state never moves back.
/// </summary>
/// <remarks>
/// How the shop reads it (<c>libs/commerce/MintPlayer.Spark.Commerce.Fulfilment/Services/FulfilmentPlanner.cs</c>,
/// <c>RevisionView</c>): <c>Cancellation = Accepted</c> or <c>State = Cancelled</c> means nothing more ships; a line's
/// <c>Cancelled</c> without an accepted cancellation is a cancelled remainder, which the shop refunds by itself (D45). So a
/// remainder cancelled under <see cref="FulfilmentBackorderPolicy.CancelRemainder"/> is published with the order
/// <see cref="FulfilmentOrderState.Shipped"/> ("every line shipped, backordered-and-cancelled, or cancelled"), never
/// <see cref="FulfilmentOrderState.Cancelled"/>, which would hide it from the refund.
/// </remarks>
public static class OutboundStatus
{
    public static string IdFor(string fulfilmentOrderId) => fulfilmentOrderId + "/status";

    /// <summary>The published state of an internal state; null for none yet.</summary>
    public static FulfilmentOrderState Map(OutboundOrder order) => order.State switch
    {
        OutboundState.Rejected => FulfilmentOrderState.Rejected,
        OutboundState.Cancelled => FulfilmentOrderState.Cancelled,
        OutboundState.Shipped => FulfilmentOrderState.Shipped,
        OutboundState.Packed => FulfilmentOrderState.Packed,
        OutboundState.Picked or OutboundState.Packing => FulfilmentOrderState.Picked,
        OutboundState.Picking => FulfilmentOrderState.Picking,
        // Waiting for a backorder after a partial shipment is still work in progress.
        _ => order.Lines.Any(l => l.Shipped > 0 || l.Picks.Count > 0) ? FulfilmentOrderState.Picking : FulfilmentOrderState.Accepted,
    };

    /// <summary>Writes (or updates) the published status of <paramref name="order"/> in <paramref name="session"/>.</summary>
    public static async Task WriteAsync(IAsyncDocumentSession session, OutboundOrder order, IReplicaPublisher publisher, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var id = IdFor(order.FulfilmentOrderId);
        var status = await session.LoadAsync<FulfilmentOrderStatus>(id, cancellationToken);
        if (status is null)
        {
            status = new FulfilmentOrderStatus { Id = id };
            await session.StoreAsync(status, id, cancellationToken);
        }

        var state = Map(order);
        // Forward-only (§4.3): terminal states always win; otherwise never step back.
        if (state is FulfilmentOrderState.Rejected or FulfilmentOrderState.Cancelled or FulfilmentOrderState.Shipped
            || status.State is FulfilmentOrderState.Accepted || state > status.State)
            status.State = state;

        status.FulfilmentOrderId = order.FulfilmentOrderId;
        status.OwnerId = order.ExternalOwnerId;
        status.RejectionReason = order.RejectionReason;
        status.RejectionDetail = order.RejectionDetail;
        status.Lines = order.Lines.Select(l => new FulfilmentOrderLineStatus
        {
            LineId = l.LineId,
            Shipped = l.Shipped,
            Backordered = l.Backordered,
            Cancelled = l.Cancelled,
        }).ToList();
        status.Cancellation = order.CancellationAnswer;
        status.CancellationRefusalReason = order.CancellationRefusalReason;
        status.UpdatedAt = now;
        await publisher.MarkChangedAsync(session, typeof(FulfilmentOrderStatus), id, cancellationToken);
    }
}

/// <summary>Sessions for the outbound documents: optimistic concurrency on every write (two writers conflict, one retries).</summary>
internal static class OutboundSessions
{
    public static IAsyncDocumentSession Open(IDocumentStore store)
    {
        var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        return session;
    }

    /// <summary>Runs <paramref name="body"/> in a fresh session until it commits without a conflict (at most <paramref name="attempts"/>).</summary>
    public static async Task<T> RetryAsync<T>(IDocumentStore store, Func<IAsyncDocumentSession, Task<(T Result, bool Save)>> body, CancellationToken cancellationToken, int attempts = 8)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var session = Open(store);
            var (result, save) = await body(session);
            if (!save)
                return result;
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return result;
            }
            catch (Raven.Client.Exceptions.ConcurrencyException) when (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1, 4 * attempt)), cancellationToken);
            }
        }
    }
}
