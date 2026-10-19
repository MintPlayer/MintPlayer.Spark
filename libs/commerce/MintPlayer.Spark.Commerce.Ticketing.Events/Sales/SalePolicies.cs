using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;

namespace MintPlayer.Spark.Commerce.Ticketing.Events.Sales;

/// <summary>An event's per-sale values after clamping to its channel's bounds (D2).</summary>
/// <param name="Hold">How long a hold lasts.</param>
/// <param name="PerBuyerLimit">The most tickets per buyer; null for no limit.</param>
/// <param name="QueueMode">The waiting-room mode of gated phases; null when the channel allows none.</param>
public sealed record EffectiveSalePolicy(
    TimeSpan Hold,
    int? PerBuyerLimit,
    ETicketLimitScope LimitScope,
    RefundPolicyKind RefundPolicy,
    int RefundDaysBefore,
    int RefundFeeBasisPoints,
    bool Transfer,
    bool FaceValueResale,
    WaitingRoomMode? QueueMode,
    SingleSeatRule? SingleSeatRule);

/// <summary>
/// Clamps a seller's per-sale values to the channel's bounds (D2, §4.2 "stored values are clamped against the bounds
/// when read, so tightening the configuration takes effect immediately"). Pure.
/// </summary>
public static class SalePolicies
{
    public static EffectiveSalePolicy Effective(TicketSalePolicy? chosen, CommerceChannelOptions channel)
    {
        chosen ??= new TicketSalePolicy();
        var hold = channel.Hold.Clamp(chosen.HoldMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null);

        // D16: a seller may lower the channel's limit, never raise it.
        int? limit = (chosen.PerBuyerLimit, channel.PerBuyerLimit) switch
        {
            ({ } own, { } max) => Math.Clamp(own, 1, max),
            ({ } own, null) => Math.Max(1, own),
            (null, var max) => max,
        };

        var allowed = channel.Refunds.Allowed;
        var refund = chosen.RefundPolicy is { } kind && allowed.Contains(kind)
            ? kind
            : allowed.Contains(RefundPolicyKind.None) || allowed.Count == 0 ? RefundPolicyKind.None : allowed[0];
        var daysBefore = channel.Refunds.DaysBefore.Clamp(chosen.RefundDaysBefore);
        var fee = Math.Clamp(chosen.RefundFeeBasisPoints ?? 0, 0, Math.Max(0, channel.Refunds.MaxFeeBasisPoints));

        var modes = channel.WaitingRoom.Modes;
        WaitingRoomMode? queue = chosen.QueueMode is { } mode && modes.Contains(mode)
            ? mode
            : channel.WaitingRoom.Default is { } fallback && modes.Contains(fallback) ? fallback
            : modes.Count > 0 ? modes[0] : null;

        return new EffectiveSalePolicy(
            hold, limit, chosen.LimitScope, refund, daysBefore, fee,
            chosen.Transfer && channel.Resale.Transfer,
            chosen.FaceValueResale && channel.Resale.FaceValueResale,
            queue, chosen.SingleSeatRule);
    }
}

/// <summary>When a phase sells, and its presale code (D17). Pure.</summary>
public static class SaleWindows
{
    /// <summary>Whether <paramref name="phase"/> sells at <paramref name="now"/>: started, and not past its end.</summary>
    public static bool IsOpen(TicketSalePhase phase, DateTimeOffset now)
        => phase.StartsAt <= now && (phase.EndsAt is not { } end || now < end);

    /// <summary>Whether a date can still be sold at <paramref name="now"/>: not cancelled, and not started.</summary>
    public static bool IsSellable(TicketEventDate date, DateTimeOffset now)
        => date.Status != ETicketEventStatus.Cancelled && now < date.StartsAt;

    /// <summary>The dates a phase sells: its own list, or every date of the event.</summary>
    public static IEnumerable<string> PhaseDates(TicketSalePhase phase, IEnumerable<string> eventDateIds)
        => phase.DateIds.Count > 0 ? phase.DateIds : eventDateIds;

    /// <summary>The stored form of a presale code: SHA-256 (hex) of the trimmed, upper-cased code.</summary>
    public static string HashPresaleCode(string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant())));

    /// <summary>Whether <paramref name="code"/> opens a phase whose code hashes to <paramref name="storedHash"/> (constant time). An open phase (no hash) accepts anything.</summary>
    public static bool PresaleAccepted(string? storedHash, string? code)
    {
        if (string.IsNullOrEmpty(storedHash))
            return true;
        if (string.IsNullOrWhiteSpace(code))
            return false;
        var given = Encoding.ASCII.GetBytes(HashPresaleCode(code));
        var stored = Encoding.ASCII.GetBytes(storedHash.ToUpperInvariant());
        return CryptographicOperations.FixedTimeEquals(given, stored);
    }
}
