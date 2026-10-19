using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Webhooks;

/// <summary>Registration for inbound webhooks.</summary>
public static class SparkWebhooksExtensions
{
    /// <summary>
    /// Registers a webhook source: its verification, its delivery id, its body cap and its one
    /// handler. Served at <c>POST /spark/webhooks/{name}</c> unless the source maps its own route.
    /// Queued sources (the default) need <c>spark.AddMessaging()</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// spark.AddWebhookSource("stripe", source =>
    /// {
    ///     source.Verification = services => new StripeSignatureStrategy(
    ///         services.GetRequiredService&lt;IConfiguration&gt;()["Stripe:WebhookSecret"]);
    ///     source.DeliveryId = WebhookDeliveryIds.FromJsonProperty("id");
    ///     source.ContentTypes.Add("application/json");
    ///     source.UseHandler&lt;StripeWebhookHandler&gt;();
    /// });
    /// </code>
    /// </example>
    /// <param name="builder">The Spark builder.</param>
    /// <param name="name">Lower-case letters, digits and <c>-</c>, 1–64 characters; unique.</param>
    /// <param name="configure">Configures the source. <see cref="WebhookSourceBuilder.Verification"/> and a handler are required.</param>
    public static ISparkBuilder AddWebhookSource(this ISparkBuilder builder, string name, Action<WebhookSourceBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        if (string.IsNullOrEmpty(name) || !WebhookSourceBuilder.NamePattern().IsMatch(name))
            throw new ArgumentException("A webhook source name is 1-64 lower-case letters, digits or '-'.", nameof(name));

        var sourceBuilder = new WebhookSourceBuilder(name);
        configure(sourceBuilder);
        var source = sourceBuilder.Build();

        var catalog = AddWebhooksCore(builder);
        catalog.Add(source);

        builder.Services.Add(ServiceDescriptor.KeyedScoped(typeof(IWebhookHandler), source.Name, source.HandlerType));

        // Only a queued source needs the queue. A plain IRecipient<T> registration (ImplementationType
        // set): Messaging derives its queues and its message-type allow-list from exactly that, so an
        // app with inline sources only (GitHub) gets no spark-webhooks queue and no subscription for it.
        if (source.Handling == WebhookHandling.Queued)
            builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecipient<WebhookDelivery>, WebhookDeliveryRecipient>());

        return builder;
    }

    /// <summary>
    /// The shared registrations, once per service collection however many sources are added: the
    /// catalog, the receiver and the shared route.
    /// </summary>
    private static WebhookSourceCatalog AddWebhooksCore(ISparkBuilder builder)
    {
        var services = builder.Services;
        if (services.FirstOrDefault(d => d.ServiceType == typeof(WebhookSourceCatalog))?.ImplementationInstance is WebhookSourceCatalog existing)
            return existing;

        var catalog = new WebhookSourceCatalog();
        services.AddSingleton(catalog);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IWebhookReceiver, WebhookReceiver>();

        // The per-library method the Endpoints generator emits for this assembly (AssemblyInfo.cs):
        // POST /spark/webhooks/{source}, mapped only when a source uses the shared route.
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkWebhooksEndpoints());
        return catalog;
    }
}
