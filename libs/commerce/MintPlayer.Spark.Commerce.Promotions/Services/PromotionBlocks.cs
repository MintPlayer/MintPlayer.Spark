using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Promotions.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Promotions.Services;

/// <summary>
/// Capacity blocks (§4.6 "block capacity"; the waiting list's reserve). Units move out of public sale by a converted
/// inventory claim on the item (so the public quotas count them as sold) and into the block's own quota, from which only
/// the block's codes sell. Closing returns the units nobody bought (<see cref="ISparkInventory.ReturnAsync"/>, keyed, so
/// exactly once, I4).
/// </summary>
/// <remarks>
/// Every change is two steps against two systems (the inventory engine and the block document). The step is written to
/// <see cref="PromotionBlock.Pending"/> first and finished idempotently (claim id, return id, absolute capacity), so a
/// crash in between leaves a step the reconciliation finishes, never units counted twice. Between the steps units are at
/// worst neither public nor in the block: never oversold (I1).
/// </remarks>
internal sealed class PromotionBlocks(
    IDocumentStore store,
    ISparkInventory inventory,
    PromotionItems items,
    TimeProvider clock,
    ILogger<PromotionBlocks> logger)
{
    /// <summary>The hold a block claim lives under before it is converted (seconds are enough: it converts at once).</summary>
    private static readonly TimeSpan ClaimHold = TimeSpan.FromMinutes(5);

    /// <summary>Creates a block of <paramref name="itemId"/> for <paramref name="ownerId"/> and moves up to <paramref name="quantity"/> units into it.</summary>
    /// <exception cref="PromotionException">The item cannot be found, has numbered units (seats), or is unlimited.</exception>
    public async Task<PromotionBlock> CreateAsync(string ownerId, string? organizationId, string itemId, int quantity, DateTimeOffset? closesAt, CancellationToken cancellationToken)
    {
        var item = await items.ResolveAsync(itemId, 1, cancellationToken) ?? throw new PromotionException(PromotionRefusals.ItemNotFound, itemId);
        if (item.HasNumberedUnits)
            throw new PromotionException(PromotionRefusals.NumberedUnits, itemId); // seats are blocked with a seat hold, not a quota
        if (item.Capacity.IsEmpty)
            throw new PromotionException(PromotionRefusals.InvalidRequest, itemId); // an unlimited item has nothing to block

        var guid = Guid.NewGuid().ToString("N");
        var block = new PromotionBlock
        {
            Id = "PromotionBlocks/" + guid,
            OwnerId = ownerId,
            OrganizationId = organizationId,
            ItemId = itemId,
            QuotaId = "promotions/blocks/" + guid,
            ClosesAt = closesAt,
            CreatedAt = clock.GetUtcNow(),
        };
        await inventory.CreateQuotaAsync(block.QuotaId, 0, 1, cancellationToken);
        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(block, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }
        if (quantity > 0)
            await GrowAsync(block.Id, quantity, cancellationToken);
        return await LoadAsync(block.Id, cancellationToken) ?? block;
    }

    public async Task<PromotionBlock?> LoadAsync(string blockId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<PromotionBlock>(blockId, cancellationToken);
    }

    /// <summary>Units the block can still sell (its quota's capacity minus sold and held).</summary>
    public async Task<QuotaAvailability> AvailabilityAsync(PromotionBlock block, CancellationToken cancellationToken)
        => await inventory.GetAvailabilityAsync(block.QuotaId, cancellationToken);

    /// <summary>Moves <paramref name="quantity"/> more units from public sale into the block. False when the public has fewer left.</summary>
    public async Task<bool> GrowAsync(string blockId, int quantity, CancellationToken cancellationToken)
    {
        if (quantity <= 0)
            return false;
        using (var session = store.OpenAsyncSession())
        {
            var block = await session.LoadAsync<PromotionBlock>(blockId, cancellationToken);
            if (block is null || block.ClosedAt is not null)
                return false;
            if (block.Pending is not null)
            {
                session.Advanced.Clear();
                await FinishPendingAsync(blockId, cancellationToken);
                return await GrowAsync(blockId, quantity, cancellationToken);
            }
            block.Sequence++;
            block.Pending = new PromotionBlockStep
            {
                Kind = "grow",
                Key = $"promotions/block-claims/{blockId.Split('/')[^1]}/{block.Sequence}",
                Quantity = quantity,
                TargetCapacity = block.Capacity + quantity,
            };
            await session.SaveChangesAsync(cancellationToken);
        }
        return await FinishPendingAsync(blockId, cancellationToken);
    }

    /// <summary>
    /// Returns up to <paramref name="max"/> units the block has not sold to public sale (closing, a list's spare). Returns
    /// how many went back. Units sold or held through the block stay.
    /// </summary>
    public async Task<int> ReturnSpareAsync(string blockId, int max, CancellationToken cancellationToken)
    {
        var returned = 0;
        while (returned < max)
        {
            PromotionBlock? block;
            using (var session = store.OpenAsyncSession())
            {
                block = await session.LoadAsync<PromotionBlock>(blockId, cancellationToken);
                if (block is null)
                    return returned;
                if (block.Pending is not null)
                {
                    session.Advanced.Clear();
                    await FinishPendingAsync(blockId, cancellationToken);
                    continue;
                }
                var spare = (await inventory.GetAvailabilityAsync(block.QuotaId, cancellationToken)).Available;
                var claim = block.Claims.FirstOrDefault(c => c.Quantity - c.Returned > 0);
                var take = Math.Min(Math.Min(spare, max - returned), claim is null ? 0 : claim.Quantity - claim.Returned);
                if (take <= 0)
                    return returned;
                block.Sequence++;
                block.Pending = new PromotionBlockStep
                {
                    Kind = "return",
                    Key = $"promotions/block-returns/{blockId.Split('/')[^1]}/{block.Sequence}",
                    Quantity = take,
                    ClaimId = claim!.ClaimId,
                    TargetCapacity = block.Capacity - take,
                };
                await session.SaveChangesAsync(cancellationToken);
            }
            if (!await FinishPendingAsync(blockId, cancellationToken))
                return returned;
            returned += block.Pending!.Quantity;
        }
        return returned;
    }

    /// <summary>Stops a block selling and returns everything it has not sold. Idempotent; called again later it returns what came back since (refunds).</summary>
    public async Task<int> CloseAsync(string blockId, CancellationToken cancellationToken)
    {
        using (var session = store.OpenAsyncSession())
        {
            var block = await session.LoadAsync<PromotionBlock>(blockId, cancellationToken);
            if (block is null)
                return 0;
            if (block.ClosedAt is null)
            {
                block.ClosedAt = clock.GetUtcNow();
                await session.SaveChangesAsync(cancellationToken);
            }
        }
        return await ReturnSpareAsync(blockId, int.MaxValue, cancellationToken);
    }

    /// <summary>Finishes the block's pending step. True when it completed, false when it was dropped (the public was sold out, or a sale got there first).</summary>
    public async Task<bool> FinishPendingAsync(string blockId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var block = await session.LoadAsync<PromotionBlock>(blockId, cancellationToken);
        if (block?.Pending is not { } step)
            return false;

        var ok = step.Kind switch
        {
            "grow" => await FinishGrowAsync(block, step, cancellationToken),
            "return" => await FinishReturnAsync(block, step, cancellationToken),
            _ => false,
        };
        block.Pending = null;
        await session.SaveChangesAsync(cancellationToken);
        return ok;
    }

    private async Task<bool> FinishGrowAsync(PromotionBlock block, PromotionBlockStep step, CancellationToken cancellationToken)
    {
        var claim = await inventory.GetClaimAsync(step.Key, cancellationToken);
        if (claim is null)
        {
            var item = await items.ResolveAsync(block.ItemId, step.Quantity, cancellationToken);
            if (item is null || item.HasNumberedUnits || item.Capacity.IsEmpty)
                return false;
            var held = await inventory.TryClaimAsync(new ClaimRequest(step.Key, block.Id!, clock.GetUtcNow() + ClaimHold,
                item.Capacity.Quotas, Items: item.Capacity.Items), cancellationToken);
            if (!held.Succeeded)
                return false; // the public has fewer units than asked: nothing moved
            claim = await inventory.GetClaimAsync(step.Key, cancellationToken);
        }
        if (claim is null || claim.State == ClaimState.Released)
            return false;
        if (claim.State == ClaimState.Held)
        {
            var converted = await inventory.ConvertAsync(step.Key, cancellationToken: cancellationToken);
            if (!converted.Succeeded)
            {
                logger.LogWarning("Block {BlockId}: converting claim {ClaimId} failed ({Status}).", block.Id, step.Key, converted.Status);
                await inventory.ReleaseAsync(step.Key, ReturnReason.Other, cancellationToken: cancellationToken);
                return false;
            }
        }
        var raised = await inventory.SetCapacityAsync(block.QuotaId, step.TargetCapacity, cancellationToken);
        if (!raised.Succeeded)
        {
            logger.LogError("Block {BlockId}: raising quota {QuotaId} to {Capacity} failed ({Status}).", block.Id, block.QuotaId, step.TargetCapacity, raised.Status);
            return false;
        }
        block.Capacity = step.TargetCapacity;
        if (block.Claims.All(c => c.ClaimId != step.Key))
            block.Claims.Add(new PromotionBlockClaim { ClaimId = step.Key, Quantity = step.Quantity });
        return true;
    }

    private async Task<bool> FinishReturnAsync(PromotionBlock block, PromotionBlockStep step, CancellationToken cancellationToken)
    {
        // Shrink the block first, so nobody buys the units being returned; the engine refuses below sold + held.
        var shrunk = await inventory.SetCapacityAsync(block.QuotaId, step.TargetCapacity, cancellationToken);
        if (!shrunk.Succeeded)
            return false;
        var item = await items.ResolveAsync(block.ItemId, step.Quantity, cancellationToken);
        if (item is null || item.Capacity.IsEmpty)
        {
            logger.LogError("Block {BlockId}: item {ItemId} no longer resolves; {Quantity} units stay out of sale until it does.", block.Id, block.ItemId, step.Quantity);
            await inventory.SetCapacityAsync(block.QuotaId, block.Capacity, cancellationToken);
            return false;
        }
        var result = await inventory.ReturnAsync(step.ClaimId!, new InventoryReturn(step.Key, ReturnReason.Other,
            Items: item.Capacity.Items.Count > 0 ? item.Capacity.Items : null,
            Quotas: item.Capacity.Quotas.Count > 0 ? item.Capacity.Quotas : null), cancellationToken);
        if (!result.Succeeded)
        {
            logger.LogError("Block {BlockId}: returning {Quantity} units from {ClaimId} failed ({Status}).", block.Id, step.Quantity, step.ClaimId, result.Status);
            await inventory.SetCapacityAsync(block.QuotaId, block.Capacity, cancellationToken);
            return false;
        }
        block.Capacity = step.TargetCapacity;
        if (block.Claims.FirstOrDefault(c => c.ClaimId == step.ClaimId) is { } claim)
            claim.Returned += step.Quantity;
        return true;
    }
}
