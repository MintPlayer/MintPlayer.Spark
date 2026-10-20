using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Approvals;
using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Live;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Services;
using MintPlayer.Spark.Cron;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues;

/// <summary>
/// Venues and layouts (<c>docs/commerce_PRD.md</c> §4.11, <c>docs/guide-ticketing-venues.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddAuthentication&lt;SparkUser&gt;();
///     spark.AddTicketingVenues();     // Spark:Commerce:Ticketing:Venues; adds Inventory and Approvals
/// });
/// // App_Data/security.json: { "resource": "ApproveProposal/Venue", "groupId": "PlatformAdmins" },
/// //                          { "resource": "ApproveProposal/VenueLayout", "groupId": "PlatformAdmins" }
/// </code>
/// </summary>
public static class SparkTicketingVenuesExtensions
{
    /// <summary>
    /// Registers <see cref="ISparkVenues"/>, the <c>Venue</c> and <c>VenueLayout</c> types and their rights, the
    /// layout-publication approval kind, the seat-map endpoints and the live seat-state topics
    /// <c>ticketing-venues:{snapshotId}</c>. Venue saves are Approvals document proposals: modifications always, creations
    /// when <c>ApproveCreations</c> (D8). Safe to call more than once.
    /// </summary>
    public static ISparkBuilder AddTicketingVenues(this ISparkBuilder builder, Action<SparkTicketingVenuesOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<VenuesMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        builder.AddCommerceInventory();
        builder.AddApprovals();

        services.AddSparkConfigurationSection<SparkTicketingVenuesOptions>();
        if (configure is not null)
            services.Configure(configure);

        // D8: a venue modification always waits for an approver; a creation when configured. Layout renames are
        // modifications too. PostConfigure, so no configuration of Approvals can switch the modification rule off.
        services.AddOptions<SparkApprovalsOptions>().PostConfigure<IOptions<SparkTicketingVenuesOptions>>((approvals, venues) =>
        {
            approvals.Types[VenueAccess.VenueType] = new SparkApprovalTypeOptions { Modify = true, Create = venues.Value.ApproveCreations };
            approvals.Types[VenueAccess.LayoutType] = new SparkApprovalTypeOptions { Modify = true };
        });

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<VenueAccess>();
        services.TryAddScoped<ISparkVenues, SparkVenues>();
        services.TryAddScoped<IApprovalDeciders<LayoutPublishApproval>, LayoutApprovers>();
        services.TryAddScoped<IApprovalHandler<LayoutPublishApproval>, LayoutPublishHandler>();
        builder.AddInterceptor<VenueSaveInterceptor>();
        builder.AddInterceptor<VenueLayoutSaveInterceptor>();

        services.TryAddSingleton<VenueSeatMaps>();
        services.TryAddSingleton<SeatStatePusher>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISparkLiveTopicPolicy, VenueSeatTopicPolicy>());
        // Seat states are pushed after the inventory commits that touched them (no polling).
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IInventoryCommitted, VenueSeatStateCommitted>());

        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkTicketingVenuesEndpoints());
        return builder;
    }

    private sealed class VenuesMarker;
}
