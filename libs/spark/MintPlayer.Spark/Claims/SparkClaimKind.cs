namespace MintPlayer.Spark.Claims;

/// <summary>
/// One family of claims (commerce PRD X12): the compare-exchange key prefix they live under, and how to read a
/// stored value — who owns it and when it expires. Declared once (usually a static field) and handed to every
/// <see cref="ISparkClaims"/> call, so the stored value keeps whatever shape the owner of the family chose.
/// </summary>
/// <typeparam name="TValue">The compare-exchange value. A record, or a plain string when the value is the owner.</typeparam>
/// <remarks>
/// <para>
/// <b>Lazy expiry.</b> Nothing deletes an expired claim. A claim is live while it has no expiry, or until
/// <c>ExpiresAtUtc + </c><see cref="Grace"/>; past that it counts as absent, and the next reservation takes it
/// over, guarded by the index it read, so of two contenders exactly one wins.
/// </para>
/// <para>
/// <b>Abandoned claims.</b> A claim that never expires can still be dead: the document that owns it was deleted
/// without releasing it. <see cref="IsAbandonedAsync"/> lets the family say so (for example "the owner document no
/// longer exists"), and an abandoned claim is taken over like an expired one.
/// </para>
/// </remarks>
public sealed class SparkClaimKind<TValue> where TValue : class
{
    /// <param name="prefix">The key prefix, ending in <c>/</c> (<c>emails/</c>, <c>commerce/seats/</c>).</param>
    /// <param name="ownerOf">The owner a stored value names. Null or empty means nobody can prove ownership.</param>
    public SparkClaimKind(string prefix, Func<TValue, string?> ownerOf)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        ArgumentNullException.ThrowIfNull(ownerOf);
        if (!prefix.EndsWith('/'))
            throw new ArgumentException($"A claim key prefix ends in '/': '{prefix}'.", nameof(prefix));
        Prefix = prefix;
        OwnerOf = ownerOf;
    }

    /// <summary>The compare-exchange key prefix, ending in <c>/</c>.</summary>
    public string Prefix { get; }

    /// <summary>The owner a stored value names.</summary>
    public Func<TValue, string?> OwnerOf { get; }

    /// <summary>When a stored value expires (UTC), or null for never. Unset: claims of this kind never expire.</summary>
    public Func<TValue, DateTime?>? ExpiresAtUtcOf { get; init; }

    /// <summary>How long past its expiry a claim still counts as live, for clock skew between hosts. Default none.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.Zero;

    /// <summary>How owners compare. Default ordinal.</summary>
    public StringComparer OwnerComparer { get; init; } = StringComparer.Ordinal;

    /// <summary>
    /// Whether a live claim is dead anyway (its owner document is gone), so it may be taken over. Consulted only for a
    /// claim held by another owner that has not expired. Unset: never.
    /// </summary>
    public Func<SparkClaim<TValue>, CancellationToken, Task<bool>>? IsAbandonedAsync { get; init; }

    /// <summary>The key of the claim named <paramref name="name"/>: the prefix plus the name, verbatim.</summary>
    public SparkClaimKey Key(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Any(char.IsControl))
            throw new ArgumentException("A claim name holds no control characters.", nameof(name));
        return new SparkClaimKey(Prefix + name);
    }

    /// <summary>Whether <paramref name="value"/> names <paramref name="owner"/>.</summary>
    public bool IsOwnedBy(TValue value, string owner) => OwnerComparer.Equals(OwnerOf(value) ?? string.Empty, owner);

    /// <summary>Whether <paramref name="claim"/> is still live at <paramref name="nowUtc"/>, by its expiry alone.</summary>
    public bool IsLiveAt(SparkClaim<TValue> claim, DateTime nowUtc)
        => ExpiresAtUtcOf?.Invoke(claim.Value) is not { } expires || nowUtc < expires + Grace;
}

/// <summary>A compare-exchange key of a claim, made by <see cref="SparkClaimKind{TValue}.Key"/>.</summary>
public readonly record struct SparkClaimKey(string Value)
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A stored claim: its key, its value and the compare-exchange index every guarded write compares against.</summary>
public sealed record SparkClaim<TValue>(SparkClaimKey Key, TValue Value, long Index) where TValue : class;

/// <summary>What a claim operation did.</summary>
public enum SparkClaimStatus
{
    /// <summary>Written: a new claim, or the takeover of an expired or abandoned one.</summary>
    Reserved,

    /// <summary>The claim was already the caller's; nothing written (reservations are idempotent by owner).</summary>
    AlreadyYours,

    /// <summary>Another owner holds a live claim.</summary>
    Taken,

    /// <summary>The caller's value function returned null: it chose not to write.</summary>
    Declined,

    /// <summary>The claim changed between the read and the guarded write; nothing written.</summary>
    Conflict,

    /// <summary>Updated in place by its owner (a renewal, a conversion).</summary>
    Updated,

    /// <summary>Deleted by its owner.</summary>
    Released,

    /// <summary>The claim is absent, or another owner's: an update or a release refused.</summary>
    NotYours,
}

/// <summary>
/// The outcome of a claim operation. <see cref="Claim"/> is the claim as it stands after the operation when
/// something was written or found the caller's, and the claim that refused the caller otherwise (null when absent).
/// </summary>
public readonly record struct SparkClaimResult<TValue>(SparkClaimStatus Status, SparkClaim<TValue>? Claim) where TValue : class
{
    /// <summary>The caller holds the claim afterwards: <see cref="SparkClaimStatus.Reserved"/>, <see cref="SparkClaimStatus.AlreadyYours"/> or <see cref="SparkClaimStatus.Updated"/>.</summary>
    public bool Holds => Status is SparkClaimStatus.Reserved or SparkClaimStatus.AlreadyYours or SparkClaimStatus.Updated;
}

/// <summary>
/// The value of a plain hold (<see cref="SparkClaimKinds.Holds"/>): an owner, an optional expiry and optional data.
/// What new families (seats, per-buyer limits, single-use tokens, one entry per identity) store.
/// </summary>
/// <param name="Owner">Who holds it: an order, a visitor, a user, a document id. Reserving again with the same owner is idempotent.</param>
/// <param name="ExpiresAtUtc">When the hold lapses, or null once converted (permanent).</param>
/// <param name="Data">Optional payload for the family's own use.</param>
public sealed record SparkClaimHold(string Owner, DateTime? ExpiresAtUtc, string? Data = null);

/// <summary>Ready-made claim kinds.</summary>
public static class SparkClaimKinds
{
    /// <summary>Holds (<see cref="SparkClaimHold"/>) under <paramref name="prefix"/>, expiring at their <c>ExpiresAtUtc</c> plus <paramref name="grace"/>.</summary>
    public static SparkClaimKind<SparkClaimHold> Holds(string prefix, TimeSpan grace = default) => new(prefix, hold => hold.Owner)
    {
        ExpiresAtUtcOf = hold => hold.ExpiresAtUtc,
        Grace = grace,
    };
}
