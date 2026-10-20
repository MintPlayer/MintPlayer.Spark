using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Warehouse.Outbound.Models;
using MintPlayer.Spark.Warehouse.Outbound.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Warehouse.Outbound.CustomActions;

/// <summary>The supervisor actions Outbound ships (PRD §4.7: manual release and holds; batch takeover). Names carry the library's prefix.</summary>
public static class OutboundActionNames
{
    public const string ReleaseOrder = "WarehouseReleaseOrder";
    public const string HoldOrder = "WarehouseHoldOrder";
    public const string UnholdOrder = "WarehouseUnholdOrder";
    public const string TakeOverBatches = "WarehouseTakeOverBatches";
}

internal static class OutboundActionTargets
{
    /// <summary>The order on the page, or the selected rows of the grid.</summary>
    public static IReadOnlyList<string> Of(CustomActionArgs args)
        => args.Parent?.Id is { Length: > 0 } parent
            ? [parent]
            : args.SelectedItems.Select(i => i.Id).OfType<string>().Where(id => id.Length > 0).Distinct().ToList();
}

/// <summary>Releases the orders now (manual release mode, or ahead of the next wave).</summary>
internal sealed partial class WarehouseReleaseOrderAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IOutboundRelease release;
    [Inject] private readonly ISparkCurrentUser currentUser;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        var report = await release.ReleaseAsync(OutboundActionTargets.Of(args), userId: currentUser.Id, cancellationToken: cancellationToken);
        if (report.Waiting.Count == 0)
            manager.Client.Notify(manager.GetTranslatedMessage("warehouseOutbound.released", report.Released.Count), NotificationKind.Success);
        else
            manager.Client.Notify(manager.GetTranslatedMessage("warehouseOutbound.notReleased",
                string.Join(", ", report.Waiting.Select(w => manager.GetTranslatedMessage($"warehouseOutbound.block.{w.Value}")))), NotificationKind.Warning);
    }
}

internal sealed partial class WarehouseHoldOrderAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IOutboundRelease release;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        foreach (var id in OutboundActionTargets.Of(args))
            await release.HoldAsync(id, true, "supervisor", cancellationToken);
        manager.Client.Notify(manager.GetTranslatedMessage("warehouseOutbound.held"), NotificationKind.Success);
    }
}

internal sealed partial class WarehouseUnholdOrderAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IOutboundRelease release;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        foreach (var id in OutboundActionTargets.Of(args))
            await release.HoldAsync(id, false, null, cancellationToken);
        manager.Client.Notify(manager.GetTranslatedMessage("warehouseOutbound.unheld"), NotificationKind.Success);
    }
}

/// <summary>A supervisor takes the order's pick batches over (PRD §4.7: supervisor takeover), forcing past a live lease.</summary>
internal sealed partial class WarehouseTakeOverBatchesAction : ICustomAction
{
    [Inject] private readonly IManager manager;
    [Inject] private readonly IOutboundPicking picking;
    [Inject] private readonly ISparkCurrentUser currentUser;
    [Inject] private readonly IDocumentStore store;

    public async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken = default)
    {
        if (currentUser.Id is not { Length: > 0 } userId)
            return;
        var taken = 0;
        foreach (var id in OutboundActionTargets.Of(args))
        {
            using var session = store.OpenAsyncSession();
            var order = await session.LoadAsync<OutboundOrder>(id, cancellationToken);
            foreach (var groupId in order?.GroupIds ?? [])
            {
                if (await picking.TakeOverBatchAsync(groupId, userId, force: true, cancellationToken) is BatchClaimOutcome.Claimed or BatchClaimOutcome.Renewed)
                    taken++;
            }
        }
        manager.Client.Notify(manager.GetTranslatedMessage("warehouseOutbound.takenOver", taken), NotificationKind.Success);
    }
}
