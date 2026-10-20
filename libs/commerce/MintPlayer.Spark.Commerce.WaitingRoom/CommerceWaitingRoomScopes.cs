using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;

namespace MintPlayer.Spark.Commerce.WaitingRoom;

/// <summary>
/// The admission scopes the waiting room owns, and the room of a sale phase. A scope names a room's gate:
/// <list type="bullet">
/// <item><c>ticketing-phase:{phaseId}</c> (<see cref="TicketScopes.Admission"/>, what Ticketing.Events puts on a queued
/// phase's items): the gate is the phase id;</item>
/// <item><c>waiting-room:{gate}</c>: any other room a host binds to a gate (a shop drop, a hidden collection).</item>
/// </list>
/// </summary>
public static class CommerceWaitingRoomScopes
{
    public const string TicketingPhasePrefix = "ticketing-phase:";
    public const string GatePrefix = "waiting-room:";

    /// <summary>The scope of a room bound to <paramref name="gate"/> that is not a ticketing phase.</summary>
    public static string ForGate(string gate) => GatePrefix + gate;

    /// <summary>The room gate a scope names; false for a scope the waiting room does not own.</summary>
    public static bool TryGetGate(string? scope, [NotNullWhen(true)] out string? gate)
    {
        gate = scope switch
        {
            { } s when s.StartsWith(TicketingPhasePrefix, StringComparison.Ordinal) => s[TicketingPhasePrefix.Length..],
            { } s when s.StartsWith(GatePrefix, StringComparison.Ordinal) => s[GatePrefix.Length..],
            _ => null,
        };
        if (gate is { Length: > 0 })
            return true;
        gate = null;
        return false;
    }

    /// <summary>
    /// The key of a phase's automatic room: <c>ticketing-</c>, the phase id in the key alphabet (lower-case letters,
    /// digits, <c>.</c>, <c>_</c>, <c>-</c>), and a short hash of the exact id, so two ids that fold to the same text
    /// never share a room. At most 100 characters (the waiting room's key rule).
    /// </summary>
    public static string RoomKey(string phaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phaseId);
        var folded = new StringBuilder(phaseId.Length);
        foreach (var c in phaseId.ToLowerInvariant())
        {
            var kept = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' ? c : '-';
            if (kept != '-' || (folded.Length > 0 && folded[^1] != '-'))
                folded.Append(kept);
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(phaseId)))[..8];
        var body = folded.ToString().Trim('-', '.', '_');
        if (body.Length > 80)
            body = body[..80].TrimEnd('-', '.', '_');
        return body.Length == 0 ? $"ticketing-{hash}" : $"ticketing-{body}-{hash}";
    }
}
