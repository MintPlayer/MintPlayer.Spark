using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Commerce.Payments;
using MintPlayer.Spark.Cron;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Linq;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

/// <summary>
/// Fixes an order's refund terms when it is placed (D20: a later policy change never touches a sold order). Creates the
/// ledger in its own session; a ledger made meanwhile by another path wins.
/// </summary>
internal sealed class RefundTermsRecipient(IDocumentStore store, RefundEngine engine, ILogger<RefundTermsRecipient> logger) : IOrderEventRecipient
{
    public async Task OnOrderEventAsync(OrderEvent orderEvent, CancellationToken cancellationToken)
    {
        if (orderEvent.Kind != OrderEventKind.Placed || orderEvent.Order.Id is null)
            return;
        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        if (await session.Advanced.ExistsAsync(CheckoutRefundLedger.IdFor(orderEvent.Order.Id), cancellationToken))
            return;
        await engine.LedgerAsync(session, orderEvent.Order, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            logger.LogDebug("The refund ledger of order {OrderId} was created meanwhile.", orderEvent.Order.Id);
        }
    }
}

/// <summary>
/// A provider refund changed (webhook queue, poll): the refund in the ledger follows, a mass refund's counters with it;
/// a paid refund raises <c>RefundIssued</c>. Checkout's own payment recipient moves the order's state from the same notice.
/// </summary>
internal sealed class RefundPaymentRecipient(OrderLifecycle lifecycle, RefundEngine engine, RefundService refunds) : IPaymentEventRecipient
{
    public async Task OnRefundChangedAsync(RefundChangeNotice notice, CancellationToken cancellationToken)
    {
        if (!RefundEngine.TryParseReference(notice.Refund.Reference, out var orderId, out var refundId))
            return;
        var order = await lifecycle.UpdateAsync(orderId, async (session, order, raised) =>
        {
            var ledger = await session.LoadAsync<CheckoutRefundLedger>(CheckoutRefundLedger.IdFor(orderId), cancellationToken);
            if (ledger?.Refunds.FirstOrDefault(r => r.RefundId == refundId) is not { } refund)
                return;
            await engine.ApplyProviderAsync(session, order, ledger, refund, notice.Refund, raised, moveOrder: false, cancellationToken);
        }, cancellationToken);
        await refunds.MarkIssuedDeliveredAsync(order, cancellationToken);
    }
}

/// <summary>Runs every collecting or running mass refund (one batch each, D20). A failing run resumes next time.</summary>
internal sealed class MassRefundJob(IDocumentStore store, IServiceProvider services, ILogger<MassRefundJob> logger) : ISparkCronJob
{
    public static string CronSchedule => "*/10 * * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        List<string> due;
        using (var session = store.OpenAsyncSession())
        {
            due = await session.Query<CheckoutMassRefund>()
                .Where(m => m.Status == MassRefundStatus.Collecting || m.Status == MassRefundStatus.Running)
                .Select(m => m.Id!)
                .Take(20)
                .ToListAsync(cancellationToken);
        }
        foreach (var massId in due)
        {
            try
            {
                using var scope = services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MassRefunds>().RunAsync(massId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Mass refund {MassRefundId} stopped this run.", massId);
            }
        }
    }
}

/// <summary>
/// The refund sweep: re-delivers <c>RefundIssued</c> that was saved but not marked delivered (a crash after the save), and
/// warns about refunds and withheld withdrawals past their due date (CRD Art. 13(1): 14 days from the withdrawal).
/// </summary>
internal sealed class RefundSweepJob(IDocumentStore store, IServiceProvider services, TimeProvider clock, ILogger<RefundSweepJob> logger) : ISparkCronJob
{
    public static string CronSchedule => "0 * * * * *";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var settledBefore = now.AddMinutes(-1);
        List<CheckoutRefundLedger> undelivered;
        List<CheckoutRefundLedger> overdue;
        using (var session = store.OpenAsyncSession())
        {
            // LINQ: values are query parameters, never spliced into RQL.
            undelivered = await session.Query<CheckoutRefundLedger>()
                .Where(l => l.Refunds.Any(r => r.IssuedAt != null && r.IssuedAt < settledBefore && r.IssuedDeliveredAt == null))
                .Take(100).ToListAsync(cancellationToken);
            overdue = await session.Query<CheckoutRefundLedger>()
                .Where(l => l.Returns.Any(r => r.Status == ReturnStatus.AwaitingGoods && r.RefundDueBy != null && r.RefundDueBy < now)
                    || l.Refunds.Any(r => r.DueBy != null && r.DueBy < now && r.IssuedAt == null))
                .Take(100).ToListAsync(cancellationToken);
        }

        foreach (var ledger in undelivered)
        {
            try
            {
                using var scope = services.CreateScope();
                var events = scope.ServiceProvider.GetRequiredService<OrderEvents>();
                var refunds = scope.ServiceProvider.GetRequiredService<RefundService>();
                CheckoutOrder? order;
                using (var session = store.OpenAsyncSession())
                    order = await session.LoadAsync<CheckoutOrder>(ledger.OrderId, cancellationToken);
                if (order is null)
                    continue;
                foreach (var refund in ledger.Refunds.Where(r => r.IssuedAt is not null && r.IssuedDeliveredAt is null))
                    await events.PublishAsync(new OrderEvent(order, OrderEventKind.RefundIssued, null, refund.RefundId) { Refund = RefundEngine.Issued(order, ledger, refund) }, cancellationToken);
                await refunds.MarkIssuedDeliveredAsync(order, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Re-delivering RefundIssued of order {OrderId} failed.", ledger.OrderId);
            }
        }

        foreach (var ledger in overdue)
            logger.LogWarning("Order {OrderNumber} has a refund past its due date (CRD Art. 13(1)); a withheld withdrawal needs the goods registered or the refund paid.", ledger.OrderNumber);
    }
}
