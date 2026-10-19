using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Ticketing.Events.Answers;
using MintPlayer.Spark.Commerce.Ticketing.Events.Discovery;
using MintPlayer.Spark.Commerce.Ticketing.Events.PreSelect;
using MintPlayer.Spark.Commerce.Ticketing.Events.Sales;
using MintPlayer.Spark.Commerce.Ticketing.Events.Services;
using MintPlayer.Spark.Commerce.Ticketing.Venues;
using MintPlayer.Spark.Cron;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Commerce.Ticketing.Events;

/// <summary>
/// Ticketing events (<c>docs/commerce_PRD.md</c> §4.11, <c>docs/guide-ticketing-events.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddMolliePayments();      // or another provider
///     spark.AddTicketingEvents();     // Spark:Commerce:Ticketing:Events; adds Checkout and Ticketing.Venues
///     spark.AddWaitingRoom();         // for queued phases (implements Checkout's admission gate)
/// });
/// // Spark:Commerce:Channels:tickets { "Allocation": "HoldFromSelection", … }
/// </code>
/// </summary>
public static class SparkTicketingEventsExtensions
{
    /// <summary>
    /// Registers the event types (<c>TicketEvent</c>, <c>TicketEventDate</c>, <c>TicketType</c>, <c>TicketSalePhase</c>)
    /// and their rights, ticket types as a checkout capacity source, publication and provisioning, the public event
    /// endpoints, page meta tags (X6) with schema.org Event data and the sitemap source (X22), the <c>.ics</c> of a date
    /// (GB9), attendee questions with their retention job, export and GDPR hooks (GB3), PreSelectOrder (D25), the DSA
    /// notice target <c>event</c> and the event list's ranking parameters (GA5). Safe to call more than once.
    /// </summary>
    public static ISparkBuilder AddTicketingEvents(this ISparkBuilder builder, Action<SparkTicketingEventsOptions>? configure = null)
    {
        var services = builder.Services;
        if (!services.TryAddSparkMarker<EventsMarker>())
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }

        builder.AddCommerceCheckout();
        builder.AddTicketingVenues();

        services.AddSparkConfigurationSection<SparkTicketingEventsOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ITicketSeating, VenueTicketSeating>();
        services.TryAddScoped<TicketEventPublisher>();
        services.TryAddScoped<SparkTicketEvents>();
        services.TryAddScoped<ISparkTicketEvents>(sp => sp.GetRequiredService<SparkTicketEvents>());
        services.TryAddScoped<AttendeeAnswers>();
        services.TryAddScoped<IAttendeeAnswers>(sp => sp.GetRequiredService<AttendeeAnswers>());
        services.TryAddScoped<IPreSelectOrders, PreSelectOrders>();

        // Checkout's extension points (PRD §4.1): ticket types are a capacity source; answers follow the order's fate.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutCapacitySource, TicketCapacitySource>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IOrderEventRecipient, AttendeeAnswerOrderEvents>());
        // GB3: required "at checkout" questions are answered before "Pay" and stored with the order.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ICheckoutPrePaymentCheck, AttendeeAnswersPrePaymentCheck>());
        // Commerce's: the seller's export (GB14), DSA notices (GA5), ranking transparency (GA5).
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISellerExportSource, AttendeeAnswerExport>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDsaNoticeTargetResolver, EventDsaTargetResolver>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRankingParameterSource, EventRankingParameters>());
        // GDPR (GB3): the buyer's answers in the personal-data export, erased with the account.
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkPersonalDataContributor<>), typeof(AttendeeAnswerPersonalData<>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkAccountDeletionHandler<>), typeof(AttendeeAnswerAccountDeletion<>)));

        builder.AddInterceptor<TicketEventInterceptor>();
        builder.AddInterceptor<TicketEventDateInterceptor>();
        builder.AddInterceptor<TicketTypeInterceptor>();
        builder.AddInterceptor<TicketSalePhaseInterceptor>();

        builder.AddPageMetaProvider<EventPageMetaProvider>();
        builder.AddSitemapSource<EventSitemapSource>();

        // The schedule is read at registration (the Cron registry needs it now): code, then configuration.
        var registrationTime = new SparkTicketingEventsOptions();
        configure?.Invoke(registrationTime);
        builder.Configuration?.GetSection(SparkTicketingEventsOptions.SectionName).Bind(registrationTime);
        builder.AddCron(cron => cron.AddJob<AttendeeAnswerRetentionJob>(registrationTime.Answers.RetentionSchedule, "Ticketing.Events.AnswerRetention"));

        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkTicketingEventsEndpoints());
        return builder;
    }

    /// <summary>
    /// Refuses a default channel that is not configured, a default currency that is not ISO 4217, an event page path
    /// without <c>{slug}</c>, and retention or PreSelect bounds that make no sense.
    /// </summary>
    internal static void ValidateStartup(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var problems = Problems(
            sp.GetRequiredService<IOptions<SparkTicketingEventsOptions>>().Value,
            sp.GetRequiredService<IOptions<SparkCommerceOptions>>().Value);
        if (problems.Count > 0)
            throw new InvalidOperationException("Ticketing events cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        var channel = sp.GetRequiredService<IOptions<SparkCommerceOptions>>().Value.Channels[sp.GetRequiredService<IOptions<SparkTicketingEventsOptions>>().Value.DefaultChannel];
        if (channel.Allocation == AllocationMode.OnPayment)
            sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(SparkTicketingEventsExtensions))
                .LogWarning("The default ticket channel claims nothing until payment (OnPayment): seats are not held while the buyer pays (§1).");
    }

    internal static List<string> Problems(SparkTicketingEventsOptions options, SparkCommerceOptions commerce)
    {
        const string root = "Spark:Commerce:Ticketing:Events";
        var problems = new List<string>();
        if (!commerce.Channels.ContainsKey(options.DefaultChannel))
            problems.Add($"{root}:DefaultChannel names '{options.DefaultChannel}', which Spark:Commerce:Channels does not define.");
        if (options.DefaultCurrency is { } currency && !Abstractions.Money.IsKnownCurrency(currency))
            problems.Add($"{root}:DefaultCurrency '{currency}' is not an ISO 4217 currency.");
        if (!options.EventPagePath.StartsWith('/') || !options.EventPagePath.Contains("{slug}", StringComparison.Ordinal))
            problems.Add($"{root}:EventPagePath must be a path with {{slug}}.");
        if (options.Answers.DefaultRetention < TimeSpan.Zero || options.Answers.MaxRetention < options.Answers.DefaultRetention)
            problems.Add($"{root}:Answers: DefaultRetention must lie between 0 and MaxRetention.");
        if (options.PreSelect.MaxLines < 1 || options.PreSelect.MaxAlternatives < 1)
            problems.Add($"{root}:PreSelect: MaxLines and MaxAlternatives must be at least 1.");
        if (options.ShardsPerQuota < 1)
            problems.Add($"{root}:ShardsPerQuota must be at least 1.");
        return problems;
    }

    private sealed class EventsMarker;
}
