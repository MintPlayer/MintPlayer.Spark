namespace MintPlayer.Spark.WaitingRoom;

/// <summary>
/// The admission-token seam (commerce PRD §4.12 "Admission token"; research §6.10 item 1): what a hold, a checkout or any
/// host route uses to refuse requests that did not come through the waiting room. <c>MintPlayer.Spark.Commerce.WaitingRoom</c>
/// adapts it to Checkout's admission gate; a host route uses <see cref="WaitingRoomEndpointExtensions.RequireWaitingRoomAdmission"/>.
/// </summary>
/// <remarks>
/// <para>
/// A token is sealed by Spark's Data Protection key ring and carries its id (<c>jti</c>), room, phase (the room's gate),
/// entry, subject (the identity it was issued to) and expiry. It is bound to that identity, room and phase, and lives
/// <see cref="WaitingRoomAdmissionSettings.TokenLifetime"/> (at least the hold plus the payment window).
/// </para>
/// <para>
/// <b>Single use.</b> <see cref="ConsumeAsync"/> claims the compare-exchange key <c>waitingroom/tokens/{jti}</c> for its
/// <c>consumer</c> (the basket or hold that spends it), under lazy expiry (X12). Of any number of concurrent consumers
/// exactly one wins; the same consumer retrying is answered as before (idempotent); any other is refused as
/// <see cref="WaitingRoomAdmissionStatus.Spent"/>, and the replay is recorded as deterministic evidence against the
/// presenting identity.
/// </para>
/// </remarks>
public interface IWaitingRoomAdmissions
{
    /// <summary>Whether a room gates <paramref name="phase"/> now (it exists and has not ended), so a token is required.</summary>
    Task<bool> IsGatedAsync(string phase, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a token without spending it: sealed by this application, not expired, for <paramref name="phase"/>, issued to
    /// the identity (<paramref name="identityKey"/>, default the current request's; a visitor's token stays valid for the
    /// account the visitor signed in to), and not spent.
    /// </summary>
    Task<WaitingRoomAdmissionCheck> ValidateAsync(string? token, string phase, string? identityKey = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and spends the token for <paramref name="consumer"/> (a basket id, a hold id). Idempotent for the same
    /// consumer; refused as <see cref="WaitingRoomAdmissionStatus.Spent"/> for any other.
    /// </summary>
    Task<WaitingRoomAdmissionCheck> ConsumeAsync(string? token, string phase, string consumer, string? identityKey = null, CancellationToken cancellationToken = default);
}

/// <summary>Why a token is (not) accepted.</summary>
public enum WaitingRoomAdmissionStatus
{
    /// <summary>Accepted (or, from <see cref="IWaitingRoomAdmissions.ConsumeAsync"/>, spent by this consumer).</summary>
    Valid,
    /// <summary>No token.</summary>
    Missing,
    /// <summary>Not sealed by this application, or tampered with.</summary>
    Forged,
    /// <summary>Past its expiry.</summary>
    Expired,
    /// <summary>For another phase (gate).</summary>
    WrongPhase,
    /// <summary>Issued to another identity.</summary>
    WrongIdentity,
    /// <summary>Already spent by another consumer (a replay).</summary>
    Spent,
    /// <summary>A poisoned token (deceptive measure): it validated, and is refused when it is spent.</summary>
    Poisoned,
}

/// <summary>The outcome of a check.</summary>
public sealed record WaitingRoomAdmissionCheck(
    WaitingRoomAdmissionStatus Status,
    string? RoomId = null,
    string? EntryId = null,
    string? Jti = null,
    DateTimeOffset? ExpiresAt = null)
{
    public bool IsValid => Status == WaitingRoomAdmissionStatus.Valid;
}
