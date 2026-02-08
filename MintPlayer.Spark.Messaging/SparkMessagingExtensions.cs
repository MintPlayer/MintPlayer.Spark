using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Indexes;
using MintPlayer.Spark.Messaging.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Messaging;

public static class SparkMessagingExtensions
{
    public static IServiceCollection AddSparkMessaging(
        this IServiceCollection services,
        Action<SparkMessagingOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.AddSingleton<RecipientRegistry>();
        services.AddScoped<IMessageBus, MessageBus>();
        services.AddHostedService<MessageProcessor>();

        return services;
    }

    /// <summary>
    /// Deploys the SparkMessages RavenDB index. Call this after the application is built.
    /// </summary>
    public static IApplicationBuilder CreateSparkMessagingIndexes(this IApplicationBuilder app)
    {
        var documentStore = app.ApplicationServices.GetRequiredService<IDocumentStore>();
        new SparkMessages_ByQueue().Execute(documentStore);
        return app;
    }

    public static IServiceCollection AddRecipient<TMessage, TRecipient>(
        this IServiceCollection services)
        where TRecipient : class, IRecipient<TMessage>
    {
        var registry = GetOrCreateRegistry(services);
        registry.Register(typeof(TMessage), typeof(TRecipient));
        return services;
    }

    private static RecipientRegistry GetOrCreateRegistry(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(RecipientRegistry) &&
            d.Lifetime == ServiceLifetime.Singleton);

        if (descriptor?.ImplementationInstance is RecipientRegistry existing)
        {
            return existing;
        }

        var registry = new RecipientRegistry();
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(RecipientRegistry))
            {
                services.RemoveAt(i);
            }
        }
        services.AddSingleton(registry);
        return registry;
    }
}
