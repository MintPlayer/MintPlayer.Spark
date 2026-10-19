using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Commerce.Ticketing.Events.Models;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.WaitingRoom;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.WaitingRoom;

/// <summary>
/// The automatic waiting room of a queued sale phase (§4.11 "a phase that covers several dates has one waiting room",
/// §4.12): the room <see cref="CommerceWaitingRoomScopes.RoomKey"/> with the phase id as its gate, the phase's schedule
/// (<c>QueueOpensAt</c>, <c>StartsAt</c>, <c>EndsAt</c>), the event's effective queue mode (the seller's choice clamped
/// to the channel's allowed modes, D2/D10), sign-in for the fair modes, and the phase as its capacity key
/// (<see cref="TicketPhaseWaitingRoomCapacity"/>).
/// </summary>
public interface ISparkCommerceWaitingRooms
{
    /// <summary>
    /// Creates or updates the room of <paramref name="phaseId"/> from the phase, or ends it when the phase is no longer
    /// queued. Idempotent; a host calls it after writing phases outside the persistent-object pipeline.
    /// </summary>
    /// <returns>The room key, or null when the phase has no room.</returns>
    Task<string?> SyncPhaseAsync(string phaseId, CancellationToken cancellationToken = default);

    /// <summary><see cref="SyncPhaseAsync"/> for every phase of <paramref name="eventId"/>.</summary>
    Task SyncEventAsync(string eventId, CancellationToken cancellationToken = default);
}

internal sealed class TicketPhaseRooms(
    IDocumentStore store,
    ISparkWaitingRooms rooms,
    IOptionsMonitor<SparkCommerceOptions> commerce,
    IOptionsMonitor<SparkCommerceWaitingRoomOptions> options,
    TimeProvider time,
    ILogger<TicketPhaseRooms> logger) : ISparkCommerceWaitingRooms
{
    public async Task<string?> SyncPhaseAsync(string phaseId, CancellationToken cancellationToken = default)
    {
        TicketSalePhase? phase;
        TicketEvent? ev;
        using (var session = store.OpenAsyncSession())
        {
            phase = await session.LoadAsync<TicketSalePhase>(phaseId, cancellationToken);
            ev = phase is null ? null : await session.LoadAsync<TicketEvent>(phase.EventId, cancellationToken);
        }
        if (phase is null || ev is null)
            return null;
        return await SyncAsync(ev, phase, cancellationToken);
    }

    public async Task SyncEventAsync(string eventId, CancellationToken cancellationToken = default)
    {
        TicketEvent? ev;
        List<TicketSalePhase> phases;
        using (var session = store.OpenAsyncSession())
        {
            ev = await session.LoadAsync<TicketEvent>(eventId, cancellationToken);
            phases = ev is null ? [] : await session.Query<TicketSalePhase>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
                .Where(p => p.EventId == eventId)
                .Take(256)
                .ToListAsync(cancellationToken);
        }
        if (ev is null)
            return;
        foreach (var phase in phases)
            await SyncAsync(ev, phase, cancellationToken);
    }

    internal async Task<string?> SyncAsync(TicketEvent ev, TicketSalePhase phase, CancellationToken cancellationToken)
    {
        var key = CommerceWaitingRoomScopes.RoomKey(phase.Id!);
        var existing = await rooms.GetRoomAsync(key, cancellationToken);
        var now = time.GetUtcNow();

        if (!phase.QueueEnabled)
        {
            if (existing is not { } current || current.Room.EndsAt is { } ended && ended <= now)
                return existing is null ? null : key;
            // The queue was switched off: the room ends (now, or right after its start when that is still ahead, since a
            // room must end after it starts). Checkout no longer asks for its token: the phase's items lost the scope.
            var room = current.Room;
            room.EndsAt = now > room.StartsAt ? now : room.StartsAt.AddSeconds(1);
            await rooms.SaveRoomAsync(key, room, cancellationToken);
            return key;
        }

        var channel = commerce.CurrentValue.Channels.TryGetValue(ev.Channel, out var found) ? found : null;
        var mode = channel is null ? null : SalePolicies.Effective(ev.Policy, channel).QueueMode;
        if (mode is null)
        {
            // Publication refuses this too (TicketEventPublisher.Problems); a draft is left without a room.
            logger.LogWarning("Phase {Phase} is queued, but channel '{Channel}' allows no waiting-room mode: no room.", phase.Id, ev.Channel);
            return null;
        }

        var settings = options.CurrentValue;
        var definition = existing?.Room ?? new WaitingRoomDefinition { Admission = Copy(settings.Admission) };
        definition.Gate = phase.Id!;
        definition.Title = phase.Name;
        definition.Mode = mode.Value;
        definition.OpensAt = phase.QueueOpensAt;
        definition.StartsAt = phase.StartsAt;
        definition.EndsAt = phase.EndsAt;
        // D10: a shuffle or a draw without sign-in rewards whoever opens the most browsers.
        definition.RequireSignIn = definition.RequireSignIn || mode != WaitingRoomMode.Fifo;
        definition.CapacityKey = CommerceWaitingRoomScopes.TicketingPhasePrefix + phase.Id;
        if (existing is null && settings.AdmittedUrl is { Length: > 0 } url)
            definition.AdmittedUrl = url.Replace("{phaseId}", Uri.EscapeDataString(phase.Id!), StringComparison.Ordinal)
                .Replace("{eventId}", Uri.EscapeDataString(ev.Id!), StringComparison.Ordinal);
        await rooms.SaveRoomAsync(key, definition, cancellationToken);
        return key;
    }

    private static WaitingRoomAdmissionSettings Copy(WaitingRoomAdmissionSettings from) => new()
    {
        MaxActive = from.MaxActive,
        PerMinute = from.PerMinute,
        TokenLifetime = from.TokenLifetime,
        UnitsPerAdmission = from.UnitsPerAdmission,
    };
}

/// <summary>
/// Keeps the automatic rooms in step with the phases (<see cref="SparkCommerceWaitingRoomOptions.AutomaticPhaseRooms"/>):
/// after a phase is saved its room is synchronized, and after an event is saved (published, its policy changed) every
/// room of its phases. An after-save interceptor: a failure is logged and never fails the save; a queued phase without
/// its room is refused at checkout (fail closed), so the seller sees it and saves again (or the host calls
/// <see cref="ISparkCommerceWaitingRooms.SyncPhaseAsync"/>).
/// </summary>
internal sealed class TicketPhaseRoomInterceptor(ISparkCommerceWaitingRooms phaseRooms, IOptionsMonitor<SparkCommerceWaitingRoomOptions> options)
    : IAfterSave<TicketSalePhase>
{
    public async ValueTask OnAfterSaveAsync(TicketSalePhase entity, SaveContext context)
    {
        if (options.CurrentValue.AutomaticPhaseRooms && entity.Id is { Length: > 0 } id)
            await phaseRooms.SyncPhaseAsync(id);
    }
}

/// <summary>The event half of <see cref="TicketPhaseRoomInterceptor"/>: an event's save re-synchronizes its phases' rooms (its policy decides the mode).</summary>
internal sealed class TicketEventRoomInterceptor(ISparkCommerceWaitingRooms phaseRooms, IOptionsMonitor<SparkCommerceWaitingRoomOptions> options)
    : IAfterSave<TicketEvent>
{
    public async ValueTask OnAfterSaveAsync(TicketEvent entity, SaveContext context)
    {
        if (options.CurrentValue.AutomaticPhaseRooms && entity.Id is { Length: > 0 } id)
            await phaseRooms.SyncEventAsync(id);
    }
}
