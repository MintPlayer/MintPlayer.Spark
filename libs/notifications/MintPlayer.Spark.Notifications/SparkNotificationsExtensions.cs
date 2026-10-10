using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Http;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Notifications.Identity;
using MintPlayer.Spark.Notifications.Interceptors;
using MintPlayer.Spark.Notifications.Messaging;
using MintPlayer.Spark.Notifications.Services;
using MintPlayer.Spark.WebPush;

namespace MintPlayer.Spark.Notifications;

/// <summary>
/// Notifications (<c>docs/seo_analyzer_PRD.md</c> X10):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddAuthentication&lt;SparkUser&gt;();
///     spark.AddMessaging();         // Web Push deliveries
///     spark.AddMailManager();       // the mail channel (optional)
///     spark.AddNotifications();     // Spark:Notifications, VAPID keys under Spark:Notifications:WebPush
/// });
/// // App_Data/security.json: "bindings": { "notifications:users": [ "Users" ] }
/// await notifier.NotifyAsync(new SparkNotification { UserIds = [userId], Title = "Score dropped", Url = "/runs/42" });
/// </code>
/// </summary>
public static class SparkNotificationsExtensions
{
    /// <summary>The SSRF-safe named client (X2) the push messages go out through.</summary>
    public const string WebPushHttpClient = "Spark.Notifications.WebPush";

    /// <summary>
    /// Registers <see cref="ISparkNotifier"/>, the <c>/spark/notifications</c> endpoints, the Web Push queue and
    /// its SSRF-safe client, the save rules and the account deletion and export hooks. Needs
    /// <c>AddAuthentication&lt;TUser&gt;()</c>; Web Push also needs <c>AddMessaging()</c> (startup says so).
    /// </summary>
    public static ISparkBuilder AddNotifications(this ISparkBuilder builder, Action<SparkNotificationsOptions>? configure = null)
    {
        var services = builder.Services;
        if (services.Any(d => d.ServiceType == typeof(NotificationsMarker)))
        {
            if (configure is not null)
                services.Configure(configure);
            return builder;
        }
        services.AddSingleton<NotificationsMarker>();

        services.AddOptions<SparkNotificationsOptions>();
        services.AddSingleton<IConfigureOptions<SparkNotificationsOptions>>(sp => new ConfigureNamedOptions<SparkNotificationsOptions>(
            Options.DefaultName,
            options => sp.GetService<IConfiguration>()?.GetSection(SparkNotificationsOptions.SectionName).Bind(options)));
        if (configure is not null)
            services.Configure(configure);

        // Push endpoints come from browsers: X2's client, with no redirects (push services answer directly) and
        // small answers. Spark:Notifications:WebPush:Http overrides any limit.
        services.AddSafeHttpClient(WebPushHttpClient, builder.Configuration?.GetSection(SparkNotificationsOptions.SectionName + ":WebPush:Http"), o =>
        {
            o.MaxRedirects = 0;
            o.MaxResponseBytes = 64 * 1024;
            o.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpContextAccessor();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SparkWebPushSender>();
        services.TryAddScoped<SparkNotifier>();
        services.TryAddScoped<ISparkNotifier>(sp => sp.GetRequiredService<SparkNotifier>());
        services.TryAddScoped<NotificationInbox>();

        services.Configure<SparkMessagingOptions>(messaging =>
        {
            // A push service outage (429/5xx) is retried for about a day; the message's TTL bounds its worth anyway.
            TimeSpan[] backoff = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6)];
            messaging.Queues.TryAdd(SparkNotificationQueues.WebPush, new SparkQueueOptions { MaxAttempts = 8, Backoff = backoff, MaxConcurrency = 4 });
        });
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecipient<WebPushDeliveryMessage>, WebPushDeliveryHandler>());

        builder.AddInterceptor<NotificationInterceptors>();
        builder.AddInterceptor<NotificationPreferenceInterceptors>();

        // Open generics: they close over whichever user type AddAuthentication<TUser>() registered.
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkAccountDeletionHandler<>), typeof(NotificationAccountDeletion<>)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(ISparkPersonalDataContributor<>), typeof(NotificationPersonalData<>)));

        MintPlayer.Spark.MailManager.SparkMailTemplateServiceCollectionExtensions.AddSparkMailTemplates(
            services, typeof(SparkNotificationsExtensions).Assembly, "SparkMail/");

        builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkNotificationsEndpoints());
        // Notification is IExpiring with "expiresAfter" in the library model: UseSpark() enables expiration (X1).
        return builder;
    }

    /// <summary>Bad VAPID keys or Web Push without Messaging refuse startup instead of failing every push later.</summary>
    internal static void ValidateStartup(IServiceProvider services)
    {
        var push = services.GetRequiredService<IOptions<SparkNotificationsOptions>>().Value.WebPush;
        if (!push.IsConfigured)
            return;
        try
        {
            new VapidDetails(push.PublicKey!.Trim(), push.PrivateKey!.Trim(), push.Subject!.Trim()).Validate();
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Spark:Notifications:WebPush is misconfigured: {ex.Message}", ex);
        }
        if (push.TimeToLive < TimeSpan.Zero || push.TimeToLive > TimeSpan.FromDays(28))
            throw new InvalidOperationException("Spark:Notifications:WebPush:TimeToLive must be between 0 and 28 days.");
        if (services.GetService<IMessageOutbox>() is null)
            throw new InvalidOperationException(
                "Web Push is configured (Spark:Notifications:WebPush) but Messaging is not: call spark.AddMessaging(). "
                + "Each push is a durable message, retried while the push service is unavailable.");
    }

    private sealed class NotificationsMarker;
}
