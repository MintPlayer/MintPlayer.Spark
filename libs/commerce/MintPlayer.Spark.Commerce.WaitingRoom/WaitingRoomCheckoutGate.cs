using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.WaitingRoom;

namespace MintPlayer.Spark.Commerce.WaitingRoom;

/// <summary>The refusal reasons of the waiting-room gate (the <c>subject</c> of Checkout's <c>admissionRequired</c>).</summary>
public static class WaitingRoomRefusals
{
    public const string Missing = "waitingRoomTokenMissing";
    public const string Forged = "waitingRoomTokenForged";
    public const string Expired = "waitingRoomTokenExpired";
    public const string WrongPhase = "waitingRoomTokenWrongPhase";
    public const string WrongIdentity = "waitingRoomTokenWrongIdentity";
    public const string Spent = "waitingRoomTokenSpent";
    public const string Poisoned = "waitingRoomTokenRefused";
    public const string NoBuyer = "waitingRoomNoBuyer";

    internal static string For(WaitingRoomAdmissionStatus status) => status switch
    {
        WaitingRoomAdmissionStatus.Missing => Missing,
        WaitingRoomAdmissionStatus.Forged => Forged,
        WaitingRoomAdmissionStatus.Expired => Expired,
        WaitingRoomAdmissionStatus.WrongPhase => WrongPhase,
        WaitingRoomAdmissionStatus.WrongIdentity => WrongIdentity,
        WaitingRoomAdmissionStatus.Spent => Spent,
        // A poisoned token is refused without saying why (the deceptive measure must not tell the bot).
        _ => Poisoned,
    };
}

/// <summary>
/// <see cref="IWaitingRoomAdmissions"/> as Checkout's <see cref="ICheckoutAdmissionGate"/> (§4.12): owns the scopes
/// <c>ticketing-phase:{phaseId}</c> and <c>waiting-room:{gate}</c>, and admits a line when the buyer presents a token the
/// room issued to them for that gate.
/// </summary>
/// <remarks>
/// <para>
/// The consumer of a token is the buyer's identity (<c>user:{id}</c>, or <c>visitor:{id}</c> for a guest), so one
/// admission covers every line the admitted person adds while it lives (several ticket types, a quantity change, the
/// claim at "Pay"); the per-buyer limit (D16) bounds what they take. Anyone else presenting the same token is refused as
/// spent, and the replay is recorded as evidence against them (the waiting room's single-use rule).
/// </para>
/// <para>
/// A check without consuming (early feedback, the first pass over several scopes) asks the room whether the token is
/// valid; a token this buyer already spent is valid again for them, so it re-asks as the same consumer, which the room
/// answers idempotently.
/// </para>
/// </remarks>
internal sealed class WaitingRoomCheckoutGate(IWaitingRoomAdmissions admissions) : ICheckoutAdmissionGate
{
    public bool Handles(string scope) => CommerceWaitingRoomScopes.TryGetGate(scope, out _);

    public async Task<AdmissionDecision> CheckAsync(AdmissionCheck check, CancellationToken cancellationToken)
    {
        if (!CommerceWaitingRoomScopes.TryGetGate(check.Scope, out var gate))
            return AdmissionDecision.Refuse(WaitingRoomRefusals.WrongPhase);
        if (check.Token is not { Length: > 0 })
            return AdmissionDecision.Refuse(WaitingRoomRefusals.Missing);
        var identity = check.UserId is { Length: > 0 } user ? "user:" + user
            : check.VisitorId is { Length: > 0 } visitor ? "visitor:" + visitor
            : null;
        if (identity is null)
            return AdmissionDecision.Refuse(WaitingRoomRefusals.NoBuyer);

        WaitingRoomAdmissionCheck result;
        if (check.Consume)
            result = await admissions.ConsumeAsync(check.Token, gate, consumer: identity, identityKey: identity, cancellationToken);
        else
        {
            result = await admissions.ValidateAsync(check.Token, gate, identity, cancellationToken);
            if (result.Status == WaitingRoomAdmissionStatus.Spent)
                result = await admissions.ConsumeAsync(check.Token, gate, consumer: identity, identityKey: identity, cancellationToken);
        }
        return result.IsValid ? AdmissionDecision.Admit : AdmissionDecision.Refuse(WaitingRoomRefusals.For(result.Status));
    }
}
