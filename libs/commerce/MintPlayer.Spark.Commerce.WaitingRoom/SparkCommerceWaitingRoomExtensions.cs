using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Promotions;
using MintPlayer.Spark.Commerce.Ticketing.Events;
using MintPlayer.Spark.WaitingRoom;

namespace MintPlayer.Spark.Commerce.WaitingRoom;

/// <summary>
/// The waiting room in the commerce suite (<c>docs/commerce_PRD.md</c> §4.1, §4.6, §4.12; <c>docs/guide-waiting-room.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddTicketingEvents();
///     spark.AddCommercePromotions();
///     spark.AddCommerceWaitingRoom();   // Spark:Commerce:WaitingRoom; adds the waiting room, Checkout, Promotions, Events
/// });
/// </code>
/// </summary>
public static class SparkCommerceWaitingRoomExtensions
{
    /// <summary>
    /// Registers the bridges: the waiting room's admission tokens as Checkout's admission gate (scopes
    /// <c>ticketing-phase:{phaseId}</c> and <c>waiting-room:{gate}</c>), Promotions' waiting-list queue state, the phase
    /// capacity for the rooms, and the automatic room of every queued sale phase (<see cref="ISparkCommerceWaitingRooms"/>).
    /// Safe to call more than once.
    /// </summary>
    public static ISparkBuilder AddCommerceWaitingRoom(this ISparkBuilder builder, Action<SparkCommerceWaitingRoomOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<CommerceWaitingRoomMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        builder.AddWaitingRoom();
        builder.AddCommerceCheckout();
        builder.AddCommercePromotions();
        builder.AddTicketingEvents();

        services.AddSparkConfigurationSection<SparkCommerceWaitingRoomOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ISparkCommerceWaitingRooms, TicketPhaseRooms>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutAdmissionGate, WaitingRoomCheckoutGate>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWaitingListQueueState, WaitingListQueueState>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWaitingRoomCapacity, TicketPhaseWaitingRoomCapacity>());

        builder.AddInterceptor<TicketPhaseRoomInterceptor>();
        builder.AddInterceptor<TicketEventRoomInterceptor>();
        return builder;
    }

    private sealed class CommerceWaitingRoomMarker;
}
