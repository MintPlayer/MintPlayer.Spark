namespace MintPlayer.Spark.Commerce.Inventory;

/// <summary><see cref="N"/> units from a quota (a bundle passes its multiplied count).</summary>
public sealed record QuotaLine(string QuotaId, int N);

/// <summary>A per-buyer limit: at most <see cref="Max"/> units under <see cref="Key"/> (account, visitor, e-mail, card…).</summary>
public sealed record LimitLine(string Key, int Max, int N);

/// <summary>Everything one claim takes, all or nothing (I2).</summary>
public sealed record ClaimRequest(
    string ClaimId,
    string Owner,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<QuotaLine> Quotas,
    IReadOnlyList<string>? Units = null,
    IReadOnlyList<LimitLine>? Limits = null);

public enum ClaimStatus { Held, SoldOut, UnitTaken, LimitExceeded, Expired, NotFound, InvalidState, Conflict }

/// <summary>The outcome of an engine operation. <see cref="Subject"/> names the quota, unit or limit key that refused it.</summary>
public sealed record ClaimResult(ClaimStatus Status, string? Subject = null)
{
    public bool Succeeded => Status == ClaimStatus.Held;

    public static readonly ClaimResult Ok = new(ClaimStatus.Held);
}

/// <summary>What a quota has left for sale right now: capacity minus sold minus live holds.</summary>
public sealed record QuotaAvailability(string QuotaId, int Capacity, int Sold, int Held, int Available);
