using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Live;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Ticketing.Events;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.CheckIn;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Endpoints;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Exchange;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Pdf;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Scanner;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Extensions;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets;

/// <summary>
/// Tickets and the door scanner (<c>docs/commerce_PRD.md</c> §4.11, <c>docs/guide-ticketing-tickets.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddMolliePayments();      // or another provider
///     spark.AddMailManager();         // the ticket mails (optional: without it holders open the ticket page)
///     spark.AddTicketing();           // events, venues, tickets and the scanner
/// });
/// </code>
/// </summary>
public static class SparkTicketingTicketsExtensions
{
    /// <summary>Events, venues and tickets with the scanner: <see cref="SparkTicketingEventsExtensions.AddTicketingEvents"/> + <see cref="AddTicketingTickets"/>.</summary>
    public static ISparkBuilder AddTicketing(this ISparkBuilder builder, Action<SparkTicketingTicketsOptions>? configure = null)
    {
        builder.AddTicketingEvents();
        return builder.AddTicketingTickets(configure);
    }

    /// <summary>
    /// Registers tickets (<c>Spark:Commerce:Ticketing:Tickets</c>): issuance on confirmed orders, revocation, the dispute
    /// freeze and reissue as a Checkout order-event recipient; the resale and transfer-fee items as a capacity source;
    /// delivery and holder-retention jobs; the ticket mails with PDF and <c>.ics</c>; the check-in lists, settings and
    /// devices with their rights; the holder's GDPR export and erasure; the scanner's live topic; the endpoints under
    /// <c>/spark/ticketing/tickets</c>. Adds Ticketing.Events (and so Checkout and Venues). Safe to call more than once.
    /// </summary>
    public static ISparkBuilder AddTicketingTickets(this ISparkBuilder builder, Action<SparkTicketingTicketsOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<TicketsMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        builder.AddTicketingEvents();

        services.AddSparkConfigurationSection<SparkTicketingTicketsOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddDataProtection();
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TicketSecretVault>();
        services.TryAddSingleton<TicketChangeFeed>();
        services.TryAddSingleton<ITicketPdfRenderer, TicketPdfRenderer>();
        services.TryAddScoped<ITicketResaleSettlement, PendingResaleSettlement>();
        services.TryAddScoped<TicketAccess>();
        services.TryAddScoped<TicketPresenter>();
        services.TryAddScoped<TicketMails>();
        services.TryAddScoped<TicketDelivery>();
        services.TryAddScoped<TicketIssuer>();
        services.TryAddScoped<TicketExchange>();
        services.TryAddScoped<ScannerDevices>();
        services.TryAddScoped<TicketCheckIns>();
        services.TryAddScoped<SparkTickets>();
        services.TryAddScoped<ISparkTickets>(sp => sp.GetRequiredService<SparkTickets>());

        // Checkout's extension points (PRD §4.1): tickets follow the order; resale listings and transfer fees sell through it.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOrderEventRecipient, TicketOrderEvents>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutCapacitySource, TicketExchangeCapacitySource>());
        // GDPR (§4.15): the holder's tickets in the export; the account erased as holder on deletion.
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkPersonalDataContributor<>), typeof(TicketHolderPersonalData<>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkAccountDeletionHandler<>), typeof(TicketHolderAccountDeletion<>)));
        // S9: the door devices' nudge topic (public, carries no ticket data).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISparkLiveTopicPolicy, TicketScannerTopicPolicy>());

        builder.AddInterceptor<TicketEventSettingsInterceptor>();
        builder.AddInterceptor<TicketCheckInListInterceptor>();

        // The schedules are read at registration (the Cron registry needs them now): code, then configuration.
        var registrationTime = new SparkTicketingTicketsOptions();
        configure?.Invoke(registrationTime);
        builder.Configuration?.GetSection(SparkTicketingTicketsOptions.SectionName).Bind(registrationTime);
        builder.AddCron(cron =>
        {
            cron.AddJob<TicketDeliveryJob>(registrationTime.Delivery.Schedule, "Ticketing.Tickets.Delivery");
            cron.AddJob<TicketHolderRetentionJob>(registrationTime.HolderRetentionSchedule, "Ticketing.Tickets.HolderRetention");
        });

        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkTicketingTicketsEndpoints());
        return builder;
    }

    /// <summary>Refuses settings that make no sense (see <see cref="Problems"/>).</summary>
    internal static void ValidateStartup(IServiceProvider services)
    {
        var problems = Problems(services.GetRequiredService<IOptions<SparkTicketingTicketsOptions>>().Value);
        if (problems.Count > 0)
            throw new InvalidOperationException("Ticketing tickets cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }

    internal static List<string> Problems(SparkTicketingTicketsOptions options)
    {
        const string root = "Spark:Commerce:Ticketing:Tickets";
        var problems = new List<string>();
        // S8: below 10,000 iterations an offline list (it carries the verifiers) is cheap to attack; above 1,000,000 a
        // door device needs seconds per scan.
        if (options.Secrets.Iterations is < 10_000 or > 1_000_000)
            problems.Add($"{root}:Secrets:Iterations must lie between 10,000 and 1,000,000 (S8 chose 50,000).");
        if (!options.Delivery.TicketPagePath.StartsWith('/') || !options.Delivery.TicketPagePath.Contains("{code}", StringComparison.Ordinal))
            problems.Add($"{root}:Delivery:TicketPagePath must be a path with {{code}}.");
        if (!options.Delivery.TransferPagePath.StartsWith('/') || !options.Delivery.TransferPagePath.Contains("{id}", StringComparison.Ordinal))
            problems.Add($"{root}:Delivery:TransferPagePath must be a path with {{id}}.");
        if (options.Delivery.MaxLeadTime < TimeSpan.Zero)
            problems.Add($"{root}:Delivery:MaxLeadTime must not be negative.");
        if (options.Exchange.MaxTransferFeeBasisPoints is < 0 or > 10_000)
            problems.Add($"{root}:Exchange:MaxTransferFeeBasisPoints must lie between 0 and 10,000.");
        if (options.Exchange.DefaultFreezeBeforeDoors < TimeSpan.Zero || options.Exchange.TransferOfferLifetime <= TimeSpan.Zero)
            problems.Add($"{root}:Exchange: DefaultFreezeBeforeDoors must not be negative and TransferOfferLifetime must be positive.");
        if (string.IsNullOrWhiteSpace(options.Scanner.Role))
            problems.Add($"{root}:Scanner:Role must name the door staff's custom role.");
        if (options.Scanner.ListPageSize is < 100 or > 20_000 || options.Scanner.MaxScansPerSync < 1 || options.Scanner.MaxPresenceEvents < 2)
            problems.Add($"{root}:Scanner: ListPageSize lies between 100 and 20,000; MaxScansPerSync and MaxPresenceEvents are positive.");
        if (options.HolderRetention < TimeSpan.Zero)
            problems.Add($"{root}:HolderRetention must not be negative.");
        return problems;
    }

    private sealed class TicketsMarker;
}
