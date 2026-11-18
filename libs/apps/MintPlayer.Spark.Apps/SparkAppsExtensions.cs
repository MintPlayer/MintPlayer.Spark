using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Apps.Catalog;
using MintPlayer.Spark.Apps.Endpoints;
using MintPlayer.Spark.Apps.Messaging;
using MintPlayer.Spark.Apps.Services;
using MintPlayer.Spark.Http;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Apps;

/// <summary>
/// Apps and outbound webhooks (#691, <c>docs/apps_webhooks_PRD.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddAuthentication&lt;SparkUser&gt;();
///     spark.AddMessaging();
///     spark.AddOrganizations();   // optional: organizations own webhooks too
///     spark.AddApps(apps => apps.AddEventsFromAssemblyContaining&lt;Program&gt;());
/// });
///
/// [WebhookEvent("report.completed", Permission = "reports:read")]
/// public sealed record ReportCompleted(string ReportId, decimal LineRate);
///
/// await webhooks.PublishAsync(new ReportCompleted(id, rate), WebhookOwner.Organization(orgId), session);
/// </code>
/// </summary>
public static class SparkAppsExtensions
{
    /// <summary>The SSRF-safe named client (X2) every delivery goes out through.</summary>
    public const string DeliveryHttpClient = "Spark.Apps.Webhooks";

    /// <summary>
    /// Registers the event catalog, <see cref="IWebhookPublisher"/>, the delivery engine (two Messaging queues and an
    /// SSRF-safe client), the user and organization owners and the <c>/spark/apps</c> endpoints. Needs
    /// <c>AddAuthentication&lt;TUser&gt;()</c> and <c>AddMessaging()</c>.
    /// </summary>
    [global::MintPlayer.Spark.Abstractions.Storage.SparkRequiresStore("Spark.Apps", Provider = global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProviders.RavenDb)]
    public static ISparkBuilder AddApps(this ISparkBuilder builder, Action<AppsBuilder>? configure = null)
    {
        global::MintPlayer.Spark.Abstractions.Storage.SparkStoreRequirementExtensions.RequireStore(builder, "Spark.Apps", provider: global::MintPlayer.Spark.Abstractions.Storage.SparkStoreProviders.RavenDb);

        var services = builder.Services;
        if (services.Any(d => d.ServiceType == typeof(AppsMarker)))
            throw new InvalidOperationException("AddApps() is called twice; register every event in one call.");
        services.AddSingleton<AppsMarker>();

        var apps = new AppsBuilder();
        configure?.Invoke(apps);

        services.AddOptions<SparkAppsOptions>();
        services.AddSingleton<IConfigureOptions<SparkAppsOptions>>(sp => new ConfigureNamedOptions<SparkAppsOptions>(
            Options.DefaultName,
            options => sp.GetService<IConfiguration>()?.GetSection(SparkAppsOptions.SectionName).Bind(options)));
        foreach (var action in apps.Configure)
            services.Configure(action);

        // Receivers answer directly: no redirects (a redirect would carry the signed body elsewhere), small answers.
        services.AddSafeHttpClient(DeliveryHttpClient, builder.Configuration?.GetSection(SparkAppsOptions.SectionName + ":Http"), o =>
        {
            o.MaxRedirects = 0;
            o.FollowRedirects = false;
            o.MaxResponseBytes = 64 * 1024;
            o.Timeout = TimeSpan.FromSeconds(15);
            o.ConnectTimeout = TimeSpan.FromSeconds(10);
        });

        services.AddDataProtection();
        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new WebhookEventCatalog(apps.Events));
        services.AddSingleton(new WebhookSigners(apps.Signers.ToList()));
        services.TryAddSingleton<WebhookSecretProtector>();
        services.TryAddScoped<WebhookScheduler>();
        services.TryAddScoped<WebhookPublisher>();
        services.TryAddScoped<IWebhookPublisher>(sp => sp.GetRequiredService<WebhookPublisher>());
        services.TryAddScoped<WebhookEndpointManager>();
        services.TryAddScoped<WebhookOwnerResolver>();
        if (apps.AppsEnabled)
        {
            services.AddSingleton<SparkAppsFeature>();
            services.TryAddScoped<SparkAppManager>();
        }

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWebhookOwnerProvider, UserWebhookOwnerProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IWebhookOwnerProvider, OrganizationWebhookOwnerProvider>());
        foreach (var provider in apps.OwnerProviders)
            services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IWebhookOwnerProvider), provider));

        services.AddOptions<SparkMessagingOptions>().Configure<IOptions<SparkAppsOptions>>((messaging, appsOptions) =>
        {
            // D14: about 32 hours of retries, 16 receivers at once, a slow receiver cut off at 20 s.
            TimeSpan[] backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(5), TimeSpan.FromHours(10), TimeSpan.FromHours(10)];
            messaging.Queues.TryAdd(SparkAppsQueues.Deliveries, new SparkQueueOptions { MaxAttempts = 8, Backoff = backoff, MaxConcurrency = 16, HandlerTimeout = TimeSpan.FromSeconds(20) });
            // Ordered delivery (D22) needs the events to enter their chains in publish order: one fan-out at a time.
            var ordered = appsOptions.Value.Ordering == WebhookOrdering.PerDestination;
            messaging.Queues.TryAdd(SparkAppsQueues.Events, new SparkQueueOptions { MaxConcurrency = ordered ? 1 : 4 });
        });
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecipient<WebhookEventRaised>, WebhookFanOut>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecipient<WebhookDeliveryMessage>, WebhookDeliveryHandler>());

        // The delivery log expires (D23): WebhookDelivery is IExpiring.
        builder.Registry.RequireDocumentExpiration("Spark.Apps delivery log");
        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkAppsEndpoints());
        return builder;
    }

    internal static void ValidateStartup(IServiceProvider services)
    {
        if (services.GetService<IMessageOutbox>() is null)
            throw new InvalidOperationException("Spark Apps needs Messaging: call spark.AddMessaging(). Every event and delivery is a durable message.");
        if (SparkAppsFeature.IsOn(services) && services.GetService<MintPlayer.Spark.IdentityProvider.Configuration.SparkIdentityProviderOptions>() is null)
            throw new InvalidOperationException("apps.EnableApps() needs the identity provider: call spark.AddIdentityProvider(). An App is an OAuth client of this site (D5).");
        var options = services.GetRequiredService<IOptions<SparkAppsOptions>>().Value;
        if (options.MaxPayloadBytes < 1024)
            throw new InvalidOperationException("Spark:Apps:MaxPayloadBytes must be at least 1024.");
        if (options.BreakerThreshold < 1)
            throw new InvalidOperationException("Spark:Apps:BreakerThreshold must be at least 1.");
    }

    private sealed class AppsMarker;
}
