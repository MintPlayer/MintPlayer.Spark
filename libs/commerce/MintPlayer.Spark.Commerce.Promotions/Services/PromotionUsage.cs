using Microsoft.Extensions.Options;
using MintPlayer.Spark.Claims;
using MintPlayer.Spark.Commerce.Promotions.Models;

namespace MintPlayer.Spark.Commerce.Promotions.Services;

/// <summary>One use of a code: by an order (keyed by its placement) or an admission, held until it is converted or lapses.</summary>
internal sealed record PromotionUse(string Owner, DateTime? ExpiresAtUtc);

/// <summary>A code's uses, in one compare-exchange value (X12): a usage limit is a counter many orders share.</summary>
internal sealed record PromotionUses(string Owner, List<PromotionUse> Uses)
{
    public int LiveCount(DateTime nowUtc) => Uses.Count(u => u.ExpiresAtUtc is null || u.ExpiresAtUtc > nowUtc);
}

/// <summary>The outcome of a usage reservation.</summary>
internal enum UsageResult { Reserved, Exhausted, AlreadyUsedByBuyer, Conflict }

/// <summary>
/// Usage limits and once-per-buyer rules (§4.6), exactly, over compare-exchange (X12): a code's uses are one counter
/// value guarded by its index, and a buyer's use of a code is a single-owner hold. A use is reserved when the order is
/// placed (with a deadline, so an abandoned placement lapses on its own), extended to the payment deadline, made
/// permanent on confirmation and released when the order ends unpaid.
/// </summary>
internal sealed class PromotionUsage(ISparkClaims claims, TimeProvider clock, IOptionsMonitor<SparkCommercePromotionsOptions> options)
{
    private const string SystemOwner = "commerce-promotions";

    internal static readonly SparkClaimKind<PromotionUses> UsesKind = new("commerce-promotions/uses/", v => v.Owner)
    {
        // The counter itself never expires; its entries do (judged when counting).
    };

    internal static readonly SparkClaimKind<SparkClaimHold> BuyerKind = SparkClaimKinds.Holds("commerce-promotions/buyers/");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>The live uses of a code now (reserved and converted).</summary>
    public async Task<int> CountAsync(PromotionCode code, CancellationToken cancellationToken)
    {
        var claim = await claims.GetAsync(UsesKind, UsesKind.Key(code.Code), cancellationToken);
        return claim?.Value.LiveCount(Now) ?? 0;
    }

    /// <summary>Whether a use is left for <paramref name="owner"/>: under the limit, or <paramref name="owner"/> holds one already.</summary>
    public async Task<bool> HasRoomAsync(PromotionCode code, string owner, CancellationToken cancellationToken)
    {
        if (code.MaxUses is not { } max)
            return true;
        var claim = await claims.GetAsync(UsesKind, UsesKind.Key(code.Code), cancellationToken);
        if (claim is null)
            return max > 0;
        var now = Now;
        var live = claim.Value.Uses.Where(u => u.ExpiresAtUtc is null || u.ExpiresAtUtc > now).ToList();
        return live.Count < max || live.Any(u => u.Owner == owner);
    }

    /// <summary>The usage owner of a placement: its lowest line id, which checkout keeps from basket to order line.</summary>
    public static string OwnerForLines(IEnumerable<string> lineIds)
        => "lines:" + lineIds.Order(StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// The once-per-buyer key: the account when signed in; for a guest, the e-mail address when known (it survives a
    /// cleared cookie and is what the order stores), else the visitor identity (X1). Null for an unknown buyer.
    /// </summary>
    public static string? BuyerKey(string? userId, string? email = null, string? visitorId = null)
        => userId is { Length: > 0 } ? "user:" + userId
            : email?.Trim() is { Length: > 0 } address ? "email:" + address.ToLowerInvariant()
            : visitorId is { Length: > 0 } ? "visitor:" + visitorId
            : null;

    /// <summary>Whether <paramref name="buyerKey"/> holds a live use of the code by another owner.</summary>
    public async Task<bool> UsedByBuyerAsync(PromotionCode code, string buyerKey, string? exceptOwner, CancellationToken cancellationToken)
    {
        var hold = await claims.GetAsync(BuyerKind, BuyerKind.Key($"{code.Code}/{PromotionHashes.Short(buyerKey)}"), cancellationToken);
        return hold is not null && (exceptOwner is null || !string.Equals(hold.Value.Owner, exceptOwner, StringComparison.Ordinal));
    }

    /// <summary>
    /// Reserves one use for <paramref name="owner"/> until <paramref name="expiresAt"/>: idempotent by owner (a retried
    /// placement keeps its use). Refused when the limit is reached or the buyer used the code already.
    /// </summary>
    public async Task<UsageResult> ReserveAsync(PromotionCode code, string owner, string? buyerKey, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var until = expiresAt.UtcDateTime;
        if (code.OncePerBuyer && buyerKey is not null)
        {
            var buyer = await claims.ReserveHoldAsync(BuyerKind, BuyerKind.Key($"{code.Code}/{PromotionHashes.Short(buyerKey)}"), owner, until, cancellationToken: cancellationToken);
            if (buyer.Status == SparkClaimStatus.Taken)
                return UsageResult.AlreadyUsedByBuyer;
            if (!buyer.Holds)
                return UsageResult.Conflict;
        }
        if (code.MaxUses is not { } max)
            return UsageResult.Reserved;

        var result = await ChangeAsync(code, uses =>
        {
            var now = Now;
            var live = uses.Where(u => u.ExpiresAtUtc is null || u.ExpiresAtUtc > now).ToList();
            var mine = live.FindIndex(u => u.Owner == owner);
            if (mine >= 0)
            {
                // Idempotent: keep the later deadline.
                if (live[mine].ExpiresAtUtc is { } existing && existing < until)
                    live[mine] = live[mine] with { ExpiresAtUtc = until };
                return (live, UsageResult.Reserved);
            }
            if (live.Count >= max)
                return (null, UsageResult.Exhausted);
            live.Add(new PromotionUse(owner, until));
            return (live, UsageResult.Reserved);
        }, cancellationToken);
        if (result != UsageResult.Reserved && code.OncePerBuyer && buyerKey is not null)
            await claims.ReleaseAsync(BuyerKind, BuyerKind.Key($"{code.Code}/{PromotionHashes.Short(buyerKey)}"), owner, cancellationToken);
        return result;
    }

    /// <summary>Moves <paramref name="owner"/>'s use to a new deadline (the order's payment deadline); null makes it permanent (confirmed).</summary>
    public async Task SetDeadlineAsync(PromotionCode code, string owner, string? buyerKey, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var until = expiresAt?.UtcDateTime;
        if (code.OncePerBuyer && buyerKey is not null)
            await claims.UpdateAsync(BuyerKind, BuyerKind.Key($"{code.Code}/{PromotionHashes.Short(buyerKey)}"), owner,
                hold => hold with { ExpiresAtUtc = until }, cancellationToken);
        if (code.MaxUses is null)
            return;
        await ChangeAsync(code, uses =>
        {
            var index = uses.FindIndex(u => u.Owner == owner);
            if (index < 0)
            {
                // A use that lapsed before the order was confirmed (a late payment): take it again only if the limit allows.
                var now = Now;
                var live = uses.Where(u => u.ExpiresAtUtc is null || u.ExpiresAtUtc > now).ToList();
                if (live.Count >= code.MaxUses)
                    return (null, UsageResult.Exhausted);
                live.Add(new PromotionUse(owner, until));
                return (live, UsageResult.Reserved);
            }
            uses[index] = uses[index] with { ExpiresAtUtc = until };
            return (uses, UsageResult.Reserved);
        }, cancellationToken);
    }

    /// <summary>Gives <paramref name="owner"/>'s use back (the order ended unpaid, or a refund when configured).</summary>
    public async Task ReleaseAsync(PromotionCode code, string owner, string? buyerKey, CancellationToken cancellationToken)
    {
        if (code.OncePerBuyer && buyerKey is not null)
            await claims.ReleaseAsync(BuyerKind, BuyerKind.Key($"{code.Code}/{PromotionHashes.Short(buyerKey)}"), owner, cancellationToken);
        if (code.MaxUses is null)
            return;
        await ChangeAsync(code, uses =>
        {
            var removed = uses.RemoveAll(u => u.Owner == owner);
            return removed == 0 ? (null, UsageResult.Reserved) : (uses, UsageResult.Reserved);
        }, cancellationToken);
    }

    /// <summary>Applies <paramref name="change"/> to the counter with retries: create when absent, update guarded by its index.</summary>
    private async Task<UsageResult> ChangeAsync(PromotionCode code, Func<List<PromotionUse>, (List<PromotionUse>? Uses, UsageResult Result)> change, CancellationToken cancellationToken)
    {
        var key = UsesKind.Key(code.Code);
        for (var attempt = 0; attempt < Math.Max(1, options.CurrentValue.MaxAttempts); attempt++)
        {
            var current = await claims.GetAsync(UsesKind, key, cancellationToken);
            UsageResult outcome = UsageResult.Reserved;
            SparkClaimResult<PromotionUses> written;
            if (current is null)
            {
                written = await claims.ReserveAsync(UsesKind, key, SystemOwner, _ =>
                {
                    var (uses, result) = change([]);
                    outcome = result;
                    return uses is null ? null : new PromotionUses(SystemOwner, uses);
                }, cancellationToken: cancellationToken);
            }
            else
            {
                written = await claims.UpdateAsync(UsesKind, key, SystemOwner, value =>
                {
                    var (uses, result) = change([.. value.Uses]);
                    outcome = result;
                    return uses is null ? null : value with { Uses = uses };
                }, cancellationToken);
            }
            // AlreadyYours on a create: another writer created the counter meanwhile, and the create function never ran.
            if (written.Status is SparkClaimStatus.Conflict or SparkClaimStatus.Taken or SparkClaimStatus.AlreadyYours or SparkClaimStatus.NotYours)
                continue;
            return outcome;
        }
        return UsageResult.Conflict;
    }
}
