using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Contracts;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Documents.Linq;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

/// <summary>
/// Mass refunds (D20, spike S15 reduced per round 4): an event is cancelled, every remaining unit of its items is refunded.
/// <list type="bullet">
/// <item><b>Batched and resumable:</b> the selection is stored once in chunks; a cursor walks it; each run handles a batch.</item>
/// <item><b>Idempotent:</b> an order's refund id is derived from the mass refund's id, its provider reference and inventory
/// return ids from that; the order, its ledger, the counters and the cursor are saved in one session. A crash between the
/// provider call and the save repeats the call with the same reference on resume (no double refund).</item>
/// <item><b>Rate limited:</b> at most <see cref="CheckoutMassRefundOptions.MaxRefundsPerSecond"/> provider calls.</item>
/// <item><b>Insufficient balance</b> (connected sellers): the refund fails, is counted, and finance retries the failed ones
/// after a top-up or settles them by hand.</item>
/// </list>
/// The same machinery mails the keep-or-refund notice of a reschedule window.
/// </summary>
internal sealed class MassRefunds(
    IDocumentStore store,
    OrderLifecycle lifecycle,
    RefundEngine engine,
    RefundService refunds,
    RefundMails mails,
    OrderAccess access,
    ICommerceAudit audit,
    IOptionsMonitor<SparkCommerceCheckoutOptions> options,
    TimeProvider clock,
    ILogger<MassRefunds> logger)
{
    private CheckoutMassRefundOptions Options => options.CurrentValue.Refunds.MassRefunds;

    private DateTimeOffset Now => clock.GetUtcNow();

    // ---- staff ---------------------------------------------------------------------------------------------------

    /// <summary>Starts a mass refund for the items of <paramref name="request"/>'s seller (finance of the seller, or of the platform).</summary>
    public async Task<SparkMassRefundView> StartAsync(SparkMassRefundRequest request, CancellationToken cancellationToken)
    {
        var actor = await RequireFinanceAsync(request.OrganizationId, cancellationToken);
        var items = request.ItemIds.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal).ToList();
        if (items.Count == 0)
            throw CheckoutException.Of(CheckoutRefusals.ItemNotFound);
        var mass = new CheckoutMassRefund
        {
            Mode = MassRefundMode.Refund, Status = MassRefundStatus.Collecting, OrganizationId = request.OrganizationId, ItemIds = items,
            Reason = request.Reason, RefundFees = request.RefundFees, CreatedBy = actor, CreatedAt = Now,
        };
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(mass, "CheckoutMassRefunds/", cancellationToken);
        await audit.RecordAsync(session, new CommerceAuditEntry
        {
            Action = RefundAuditActions.MassRefundStarted, SubjectId = mass.Id!, OrganizationId = request.OrganizationId, ActorId = actor, At = Now, Reason = request.Reason,
            Data = new Dictionary<string, string> { ["items"] = string.Join(",", items) },
        }, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return View(mass);
    }

    /// <summary>Opens a reschedule window (D20) and starts the run that mails its notice to every buyer.</summary>
    public async Task<SparkRescheduleWindowResult> RescheduleAsync(SparkRescheduleWindowRequest request, CancellationToken cancellationToken)
    {
        var actor = await RequireFinanceAsync(request.OrganizationId, cancellationToken);
        var items = request.ItemIds.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal).ToList();
        if (items.Count == 0 || request.ClosesAt <= Now)
            throw CheckoutException.Of(RefundRefusals.WindowClosed);
        using var session = store.OpenAsyncSession();
        var window = new CheckoutRescheduleWindow
        {
            OrganizationId = request.OrganizationId, ItemIds = items, OpensAt = Now, ClosesAt = request.ClosesAt, NewServiceDate = request.NewServiceDate,
            Note = request.Note, CreatedBy = actor,
        };
        await session.StoreAsync(window, "CheckoutRescheduleWindows/", cancellationToken);
        var mass = new CheckoutMassRefund
        {
            Mode = MassRefundMode.RescheduleNotice, Status = MassRefundStatus.Collecting, OrganizationId = request.OrganizationId, ItemIds = items,
            WindowId = window.Id, Reason = request.Note, CreatedBy = actor, CreatedAt = Now,
        };
        await session.StoreAsync(mass, "CheckoutMassRefunds/", cancellationToken);
        window.NoticeMassRefundId = mass.Id;
        await session.SaveChangesAsync(cancellationToken);
        return new SparkRescheduleWindowResult(window.Id!, mass.Id!);
    }

    public async Task<SparkMassRefundView?> GetAsync(string massId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var mass = await session.LoadAsync<CheckoutMassRefund>(massId, cancellationToken);
        if (mass is null)
            return null;
        await RequireFinanceAsync(mass.OrganizationId, cancellationToken);
        return View(mass);
    }

    /// <summary><c>pause</c>, <c>resume</c>, <c>cancel</c>, or <c>retryFailed</c> (a batch of failed refunds, e.g. after a seller topped up).</summary>
    public async Task<SparkMassRefundView> ControlAsync(SparkMassRefundControlRequest request, CancellationToken cancellationToken)
    {
        CheckoutMassRefund? mass;
        using (var probe = store.OpenAsyncSession())
            mass = await probe.LoadAsync<CheckoutMassRefund>(request.MassRefundId, cancellationToken);
        if (mass is null)
            throw CheckoutException.Of(CheckoutRefusals.OrderNotFound);
        await RequireFinanceAsync(mass.OrganizationId, cancellationToken);

        if (string.Equals(request.Action, "retryFailed", StringComparison.OrdinalIgnoreCase))
        {
            await RetryFailedAsync(mass.Id!, cancellationToken);
        }
        else
        {
            using var session = store.OpenAsyncSession();
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            mass = (await session.LoadAsync<CheckoutMassRefund>(request.MassRefundId, cancellationToken))!;
            mass.Status = request.Action.ToLowerInvariant() switch
            {
                "pause" when mass.Status is MassRefundStatus.Running or MassRefundStatus.Collecting => MassRefundStatus.Paused,
                "resume" when mass.Status == MassRefundStatus.Paused => mass.Collected ? MassRefundStatus.Running : MassRefundStatus.Collecting,
                "cancel" when mass.Status is not MassRefundStatus.Completed => MassRefundStatus.Cancelled,
                _ => throw CheckoutException.Of(CheckoutRefusals.InvalidState, subject: mass.Status.ToString()),
            };
            await session.SaveChangesAsync(cancellationToken);
        }
        using var read = store.OpenAsyncSession();
        return View((await read.LoadAsync<CheckoutMassRefund>(request.MassRefundId, cancellationToken))!);
    }

    private async Task RetryFailedAsync(string massId, CancellationToken cancellationToken)
    {
        List<CheckoutRefundLedger> ledgers;
        using (var session = store.OpenAsyncSession())
        {
            // LINQ: the mass id and the status are parameters, never spliced into RQL.
            ledgers = await session.Query<CheckoutRefundLedger>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                .Where(l => l.Refunds.Any(r => r.MassRefundId == massId && r.Status == RefundStatus.Failed))
                .Take(Math.Max(1, Options.BatchSize))
                .ToListAsync(cancellationToken);
        }
        foreach (var ledger in ledgers)
            foreach (var refund in ledger.Refunds.Where(r => r.MassRefundId == massId && r.Status == RefundStatus.Failed))
            {
                try
                {
                    await refunds.RetryAsync(ledger.OrderId, refund.RefundId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Retrying refund {RefundId} of order {OrderId} failed.", refund.RefundId, ledger.OrderId);
                }
                await ThrottleAsync(cancellationToken);
            }
    }

    private async Task<string> RequireFinanceAsync(string? organizationId, CancellationToken cancellationToken)
    {
        if (access.CallerId is not { } me || !await access.HasRoleAsync(organizationId, refunds.FinanceRole, cancellationToken))
            throw CheckoutException.Of(CheckoutRefusals.Forbidden);
        return me;
    }

    public static SparkMassRefundView View(CheckoutMassRefund m) => new(
        m.Id!, m.Mode.ToString(), m.Status.ToString(), m.Total, m.Counters.Processed, m.Counters.Skipped, m.Counters.Refunds, m.Counters.Pending,
        m.Counters.Succeeded, m.Counters.Failed, m.Counters.InsufficientBalance, m.Counters.ManualRequired, m.Counters.SettledManually, m.Counters.Notified,
        m.Counters.AmountRequested, m.LastError);

    // ---- the worker ------------------------------------------------------------------------------------------------

    /// <summary>One run of one mass refund: collect the selection if needed, then a batch. Stops (to resume next run) on any error.</summary>
    public async Task RunAsync(string massId, CancellationToken cancellationToken)
    {
        try
        {
            if (await LoadAsync(massId, cancellationToken) is { Status: MassRefundStatus.Collecting })
                await CollectAsync(massId, cancellationToken);
            for (var i = 0; i < Math.Max(1, Options.BatchSize); i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mass = await LoadAsync(massId, cancellationToken);
                if (mass is not { Status: MassRefundStatus.Running })
                    return;
                var position = (mass.NextChunk, mass.NextIndex);
                var chunk = await LoadChunkAsync(massId, mass.NextChunk, cancellationToken);
                if (chunk is null || mass.NextIndex >= chunk.OrderIds.Count)
                {
                    // A missing or short chunk: skip past it (never loop on it).
                    await AdvanceOnlyAsync(massId, position, skipped: false, cancellationToken);
                    continue;
                }
                var calledProvider = mass.Mode == MassRefundMode.Refund
                    ? await RefundOrderAsync(massId, chunk.OrderIds[mass.NextIndex], position, cancellationToken)
                    : await NotifyOrderAsync(massId, chunk.OrderIds[mass.NextIndex], position, cancellationToken);
                if (calledProvider)
                    await ThrottleAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider error, a rate limit (429), a crash of a call: nothing of the current order was saved. The next run
            // resumes at the same order; its provider call repeats with the same reference.
            logger.LogWarning(ex, "Mass refund {MassRefundId} stopped; it resumes on the next run.", massId);
            await NoteErrorAsync(massId, ex.Message, cancellationToken);
            throw;
        }
    }

    private async Task<CheckoutMassRefund?> LoadAsync(string massId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<CheckoutMassRefund>(massId, cancellationToken);
    }

    private async Task<CheckoutMassRefundChunk?> LoadChunkAsync(string massId, int n, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<CheckoutMassRefundChunk>(CheckoutMassRefundChunk.IdFor(massId, n), cancellationToken);
    }

    /// <summary>Stores the selection: every order with a line of the items (of the mass refund's seller), in id order.</summary>
    internal async Task CollectAsync(string massId, CancellationToken cancellationToken)
    {
        var mass = (await LoadAsync(massId, cancellationToken))!;
        var items = mass.ItemIds;
        var seller = mass.OrganizationId;
        var ids = new List<string>();
        using (var session = store.OpenAsyncSession())
        {
            // LINQ: the item ids are query parameters, never spliced into RQL. Wait for the index once, then stream it.
            var query = session.Query<CheckoutOrder>().Where(o => o.Lines.Any(l => items.Contains(l.ItemId)));
            await query.Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(60))).Take(0).ToListAsync(cancellationToken);
            await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
            while (await stream.MoveNextAsync())
            {
                var order = stream.Current.Document;
                if (order.Lines.Any(l => items.Contains(l.ItemId) && l.SellerOrganizationId == seller))
                    ids.Add(order.Id ?? stream.Current.Id);
            }
        }
        ids.Sort(StringComparer.Ordinal);
        var size = Math.Max(1, Options.ChunkSize);
        var chunks = ids.Chunk(size).ToList();
        for (var n = 0; n < chunks.Count; n++)
        {
            using var session = store.OpenAsyncSession();
            var id = CheckoutMassRefundChunk.IdFor(massId, n);
            await session.StoreAsync(new CheckoutMassRefundChunk { Id = id, OrderIds = [.. chunks[n]] }, id, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
        using (var session = store.OpenAsyncSession())
        {
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var current = (await session.LoadAsync<CheckoutMassRefund>(massId, cancellationToken))!;
            if (current.Status != MassRefundStatus.Collecting)
                return; // paused or cancelled meanwhile; resume collects again
            current.ChunkCount = chunks.Count;
            current.ChunkSize = size;
            current.Total = ids.Count;
            current.Collected = true;
            current.Status = ids.Count == 0 ? MassRefundStatus.Completed : MassRefundStatus.Running;
            current.CompletedAt = ids.Count == 0 ? Now : null;
            await session.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Moves the cursor one order on; completes the run after the last one.</summary>
    internal void Advance(CheckoutMassRefund mass)
    {
        mass.Counters.Processed++;
        var next = mass.NextChunk * mass.ChunkSize + mass.NextIndex + 1;
        if (next >= mass.Total)
        {
            mass.NextChunk = mass.ChunkCount;
            mass.NextIndex = 0;
            mass.Status = MassRefundStatus.Completed;
            mass.CompletedAt = Now;
        }
        else
        {
            mass.NextChunk = next / mass.ChunkSize;
            mass.NextIndex = next % mass.ChunkSize;
        }
        mass.LastRunAt = Now;
        mass.LastError = null;
    }

    /// <summary>
    /// One order of a refund run: its refund, the ledger, the order, the counters and the cursor in one session. Answers
    /// whether the provider was called (for the rate limit).
    /// </summary>
    private async Task<bool> RefundOrderAsync(string massId, string orderId, (int Chunk, int Index) position, CancellationToken cancellationToken)
    {
        var called = false;
        var handled = false;
        var order = await lifecycle.UpdateAsync(orderId, async (session, o, raised) =>
        {
            called = false;
            handled = false;
            var mass = await session.LoadAsync<CheckoutMassRefund>(massId, cancellationToken);
            if (mass is null || mass.Status != MassRefundStatus.Running || (mass.NextChunk, mass.NextIndex) != position)
                return; // the cursor moved on (another worker, or this order was saved before a crash)
            handled = true;
            Advance(mass);
            var ledger = await engine.LedgerAsync(session, o, cancellationToken);
            var lines = o.Lines
                .Where(l => l.Status != OrderLineStatus.Refused && mass.ItemIds.Contains(l.ItemId) && l.SellerOrganizationId == mass.OrganizationId)
                .Select(l => new RefundLineRequest(l.LineId, RefundEngine.Free(l, ledger.Lines.First(x => x.LineId == l.LineId))))
                .Where(l => l.Quantity > 0)
                .ToList();
            if (!RefundEngine.IsRefundable(o.State) || lines.Count == 0)
            {
                mass.Counters.Skipped++;
                return;
            }
            var fees = mass.RefundFees ?? engine.ChannelRefundsFees(o, lines.Select(l => l.LineId));
            var spec = new RefundSpec(RefundKind.EventCancelled, null, lines, RefundFees: fees, Delivery: DeliveryRefund.Full, IncludeOrderExtras: true);
            var refund = await engine.RefundAsync(session, o, ledger, spec,
                new RefundMeta(RefundEngine.StableId("m", massId), mass.CreatedBy, mass.Reason, MassRefundId: massId), raised, cancellationToken);
            if (refund is null)
            {
                mass.Counters.Skipped++;
                return;
            }
            called = !refund.Amount.IsZero && refund.PaymentId is not null;
            if (mass.Counters.AmountRequested is null || mass.Counters.AmountRequested.Currency == refund.Amount.Currency)
                mass.Counters.AmountRequested = (mass.Counters.AmountRequested ?? Money.Zero(refund.Amount.Currency)) + refund.Amount;
        }, cancellationToken);

        if (order is null)
            await AdvanceOnlyAsync(massId, position, skipped: true, cancellationToken); // the order is gone
        else if (handled)
            await refunds.MarkIssuedDeliveredAsync(order, cancellationToken);
        return called;
    }

    /// <summary>One order of a reschedule notice run: the mail (deduplicated) and the cursor.</summary>
    private async Task<bool> NotifyOrderAsync(string massId, string orderId, (int Chunk, int Index) position, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var mass = await session.LoadAsync<CheckoutMassRefund>(massId, cancellationToken);
        if (mass is null || mass.Status != MassRefundStatus.Running || (mass.NextChunk, mass.NextIndex) != position)
            return false;
        var order = await session.LoadAsync<CheckoutOrder>(orderId, cancellationToken);
        var window = mass.WindowId is { } windowId ? await session.LoadAsync<CheckoutRescheduleWindow>(windowId, cancellationToken) : null;
        Advance(mass);
        if (order is not null && window is not null && RefundEngine.IsRefundable(order.State) && await mails.SendRescheduleNoticeAsync(order, window, cancellationToken))
            mass.Counters.Notified++;
        else
            mass.Counters.Skipped++;
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            // Another worker moved on; the mail was deduplicated.
        }
        return false;
    }

    private async Task AdvanceOnlyAsync(string massId, (int Chunk, int Index) position, bool skipped, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var mass = await session.LoadAsync<CheckoutMassRefund>(massId, cancellationToken);
        if (mass is null || (mass.NextChunk, mass.NextIndex) != position)
            return;
        Advance(mass);
        if (skipped)
            mass.Counters.Skipped++;
        try { await session.SaveChangesAsync(cancellationToken); }
        catch (ConcurrencyException) { }
    }

    private async Task NoteErrorAsync(string massId, string error, CancellationToken cancellationToken)
    {
        try
        {
            using var session = store.OpenAsyncSession();
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var mass = await session.LoadAsync<CheckoutMassRefund>(massId, cancellationToken);
            if (mass is null)
                return;
            mass.LastError = error.Length > 500 ? error[..500] : error;
            mass.LastRunAt = Now;
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Noting the error of mass refund {MassRefundId} failed.", massId);
        }
    }

    private Task ThrottleAsync(CancellationToken cancellationToken)
        => Options.MaxRefundsPerSecond > 0 ? Task.Delay(TimeSpan.FromSeconds(1 / Options.MaxRefundsPerSecond), clock, cancellationToken) : Task.CompletedTask;
}
