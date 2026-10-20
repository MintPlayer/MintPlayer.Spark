using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.WaitingRoom;

namespace MintPlayer.Spark.Commerce.WaitingRoom;

/// <summary>
/// <c>Spark:Commerce:WaitingRoom</c>. Code given to <c>AddCommerceWaitingRoom(o => …)</c> runs first; the section is bound
/// after it (configuration wins, §4.2).
/// </summary>
public sealed class SparkCommerceWaitingRoomOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:WaitingRoom";

    /// <summary>
    /// Every sale phase with <c>QueueEnabled</c> gets its waiting room (key <see cref="CommerceWaitingRoomScopes.RoomKey"/>,
    /// gate the phase id) when the phase or its event is saved, and the room ends when the queue is switched off. Default
    /// true; off, the host saves the rooms itself (<c>ISparkWaitingRooms.SaveRoomAsync</c>).
    /// </summary>
    public bool AutomaticPhaseRooms { get; set; } = true;

    /// <summary>How fast an automatic room admits (a new room starts with these; an existing room keeps its own).</summary>
    public WaitingRoomAdmissionSettings Admission { get; set; } = new();

    /// <summary>
    /// Where an admitted visitor of an automatic room goes; <c>{token}</c> is replaced by the admission token,
    /// <c>{phaseId}</c> and <c>{eventId}</c> by the phase and its event. Null: the room page offers no link (the client
    /// reads the token from the private stream).
    /// </summary>
    public string? AdmittedUrl { get; set; }
}
