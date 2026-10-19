using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Promotions.Contracts;
using MintPlayer.Spark.Commerce.Promotions.Models;
using MintPlayer.Spark.Commerce.Promotions.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Promotions;

/// <summary>
/// Codes (§4.6, D18): percentage or fixed discounts, unlock and presale codes, capacity-blocking codes. The admin
/// endpoints call it after checking the caller manages the code's seller; a host may call it from its own seeding.
/// </summary>
public interface ISparkPromotionCodes
{
    /// <summary>Creates a code, or updates an existing one of the same seller. A capacity-blocking code moves its units out of public sale now.</summary>
    /// <exception cref="PromotionException">An invalid code or effect, a taken code, an item that cannot be blocked.</exception>
    Task<PromotionCode> SaveAsync(SparkPromotionCodeRequest request, string? actorId, CancellationToken cancellationToken = default);

    /// <summary>Stops a code; a capacity-blocking code's spare units go back to public sale.</summary>
    Task<PromotionCode?> DeactivateAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>The code by its text (any case), or null.</summary>
    Task<PromotionCode?> FindAsync(string code, CancellationToken cancellationToken = default);
}

internal sealed class PromotionCodes(
    IDocumentStore store,
    PromotionBlocks blocks,
    ICommerceAudit audit,
    TimeProvider clock) : ISparkPromotionCodes
{
    public async Task<PromotionCode?> FindAsync(string code, CancellationToken cancellationToken = default)
    {
        if (PromotionRules.NormalizeCode(code) is not { } normalized)
            return null;
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<PromotionCode>(PromotionCode.IdFor(normalized), cancellationToken);
    }

    public async Task<PromotionCode> SaveAsync(SparkPromotionCodeRequest request, string? actorId, CancellationToken cancellationToken = default)
    {
        var normalized = PromotionRules.NormalizeCode(request.Code) ?? throw new PromotionException(PromotionRefusals.CodeInvalid, request.Code);
        if (!Enum.TryParse<PromotionEffect>(request.Effect, ignoreCase: true, out var effect))
            throw new PromotionException(PromotionRefusals.InvalidRequest, "effect");
        Validate(request, effect);

        var now = clock.GetUtcNow();
        var id = PromotionCode.IdFor(normalized);
        PromotionCode code;
        bool created;
        using (var session = store.OpenAsyncSession())
        {
            var existing = await session.LoadAsync<PromotionCode>(id, cancellationToken);
            if (existing is not null && (!string.Equals(existing.OrganizationId, request.OrganizationId, StringComparison.Ordinal) || existing.Effect != effect))
                throw new PromotionException(PromotionRefusals.CodeTaken, normalized); // another seller's code, or a change of effect
            created = existing is null;
            code = existing ?? new PromotionCode { Id = id, Code = normalized, OrganizationId = request.OrganizationId, Effect = effect, CreatedAt = now, CreatedBy = actorId };
            code.PercentBasisPoints = effect == PromotionEffect.PercentageOff ? request.PercentBasisPoints : null;
            code.Amount = effect == PromotionEffect.FixedAmountOff ? request.Amount : null;
            code.ItemIds = [.. request.ItemIds ?? []];
            code.Scopes = [.. request.Scopes ?? []];
            code.MinimumOrderTotal = request.MinimumOrderTotal;
            code.ValidFrom = request.ValidFrom;
            code.ValidUntil = request.ValidUntil;
            code.MaxUses = request.MaxUses;
            code.OncePerBuyer = request.OncePerBuyer;
            code.MaxQuantityPerOrder = request.MaxQuantityPerOrder;
            code.Active = request.Active;
            code.UpdatedAt = now;
            if (created)
                await session.StoreAsync(code, changeVector: string.Empty, id, cancellationToken); // a concurrent create of the same code fails
            await audit.RecordAsync(session, new CommerceAuditEntry
            {
                Action = PromotionAuditActions.CodeSaved,
                SubjectId = id,
                OrganizationId = code.OrganizationId,
                After = code.Amount,
                Reason = effect.ToString(),
                ActorId = actorId,
            }, cancellationToken);
            await session.SaveChangesAsync(cancellationToken);
        }

        if (effect == PromotionEffect.CapacityBlock)
            await EnsureBlockAsync(code, request.BlockQuantity ?? 0, cancellationToken);
        return await FindAsync(normalized, cancellationToken) ?? code;
    }

    /// <summary>A block code's block: created on first save; a larger quantity later grows it (it never shrinks below what it sold).</summary>
    private async Task EnsureBlockAsync(PromotionCode code, int quantity, CancellationToken cancellationToken)
    {
        var itemId = code.ItemIds[0];
        if (code.BlockId is null)
        {
            var block = await blocks.CreateAsync(code.Id!, code.OrganizationId, itemId, quantity, code.ValidUntil, cancellationToken);
            using var session = store.OpenAsyncSession();
            var stored = await session.LoadAsync<PromotionCode>(code.Id!, cancellationToken);
            stored!.BlockId = block.Id;
            await session.SaveChangesAsync(cancellationToken);
            if (block.Capacity < quantity)
                throw new PromotionException(PromotionRefusals.SoldOut, itemId); // the code exists, with what could be blocked
            return;
        }
        var existing = await blocks.LoadAsync(code.BlockId, cancellationToken);
        if (existing is null)
            return;
        if (quantity > existing.Capacity && !await blocks.GrowAsync(existing.Id!, quantity - existing.Capacity, cancellationToken))
            throw new PromotionException(PromotionRefusals.SoldOut, itemId);
        if (quantity < existing.Capacity)
            await blocks.ReturnSpareAsync(existing.Id!, existing.Capacity - quantity, cancellationToken);
        // The block closes with the code.
        using (var session = store.OpenAsyncSession())
        {
            var block = await session.LoadAsync<PromotionBlock>(existing.Id!, cancellationToken);
            if (block is not null && block.ClosesAt != code.ValidUntil)
            {
                block.ClosesAt = code.ValidUntil;
                await session.SaveChangesAsync(cancellationToken);
            }
        }
    }

    public async Task<PromotionCode?> DeactivateAsync(string code, CancellationToken cancellationToken = default)
    {
        if (PromotionRules.NormalizeCode(code) is not { } normalized)
            return null;
        string? blockId;
        PromotionCode? stored;
        using (var session = store.OpenAsyncSession())
        {
            stored = await session.LoadAsync<PromotionCode>(PromotionCode.IdFor(normalized), cancellationToken);
            if (stored is null)
                return null;
            stored.Active = false;
            stored.UpdatedAt = clock.GetUtcNow();
            blockId = stored.WaitingListEntryId is null ? stored.BlockId : null; // a list's block belongs to the list
            await session.SaveChangesAsync(cancellationToken);
        }
        if (blockId is not null)
            await blocks.CloseAsync(blockId, cancellationToken);
        return stored;
    }

    internal static void Validate(SparkPromotionCodeRequest request, PromotionEffect effect)
    {
        if (request.ValidFrom is { } from && request.ValidUntil is { } until && until <= from)
            throw new PromotionException(PromotionRefusals.InvalidRequest, "window");
        if (request.MaxUses is < 1 || request.MaxQuantityPerOrder is < 1)
            throw new PromotionException(PromotionRefusals.InvalidRequest, "limits");
        switch (effect)
        {
            case PromotionEffect.PercentageOff when request.PercentBasisPoints is not (> 0 and <= 10_000):
                throw new PromotionException(PromotionRefusals.InvalidRequest, "percentBasisPoints");
            case PromotionEffect.FixedAmountOff when request.Amount is null || request.Amount.ToMinorUnits() <= 0:
                throw new PromotionException(PromotionRefusals.InvalidRequest, "amount");
            case PromotionEffect.Unlock when (request.Scopes?.Count ?? 0) == 0 && (request.ItemIds?.Count ?? 0) == 0:
                throw new PromotionException(PromotionRefusals.InvalidRequest, "scopes");
            case PromotionEffect.CapacityBlock when request.ItemIds is not { Count: 1 } || request.BlockQuantity is not > 0 || request.ValidUntil is null:
                // One item, a quantity, and an end: the block closes with the code and returns its spare units.
                throw new PromotionException(PromotionRefusals.InvalidRequest, "block");
        }
    }
}
