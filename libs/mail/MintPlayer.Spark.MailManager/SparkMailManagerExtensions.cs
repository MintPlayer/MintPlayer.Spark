using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.MailManager.Bounces;
using MintPlayer.Spark.MailManager.Deliveries;
using MintPlayer.Spark.MailManager.Messaging;
using MintPlayer.Spark.MailManager.Templates;
using MintPlayer.Spark.MailManager.Transports;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.MailManager;

/// <summary>Registration for MailManager.</summary>
public static class SparkMailManagerExtensions
{
    private const string ConfigurationSection = "Spark:Mail";

    /// <summary>
    /// Outgoing mail as building blocks configured through <c>Spark:Mail</c> (#460, D9): the transport
    /// (SMTP when <c>Smtp:Host</c> is set, the pickup folder when <c>PickupFolder</c> is set, or
    /// <see cref="AddMailTransport{TTransport}"/>), templates from the app's <c>Templates/Mail</c> folder
    /// over embedded defaults, the <c>mail-transactional</c> and <c>mail-bulk</c> lanes, the suppression
    /// list, one-click unsubscribe, and — opt-in — VERP and the bounce endpoint. Binds <c>Spark:Mail</c>,
    /// then applies <paramref name="configure"/>. Requires <c>spark.AddMessaging()</c>.
    /// </summary>
    /// <remarks>
    /// Startup refuses: no transport, two transports, no <c>From:Address</c>, a template without a neutral
    /// file (a warning in Development), a template that does not parse, a bounce endpoint without a
    /// 32-character secret. The Authorization package's account mail goes through this automatically.
    /// </remarks>
    public static ISparkBuilder AddMailManager(this ISparkBuilder builder, Action<SparkMailOptions>? configure = null)
    {
        var section = builder.Configuration?.GetSection(ConfigurationSection);
        builder.Services.AddOptions<SparkMailOptions>().Configure(options =>
        {
            section?.Bind(options);
            configure?.Invoke(options);
        });

        var services = builder.Services;

        // Lane defaults, declared with Configure so Spark:Messaging:Queues:{name} still wins (M4: config
        // beats code for Queues, re-applied in a PostConfigure). TryAdd: an app that declared the queue
        // in code first keeps its own.
        services.Configure<SparkMessagingOptions>(messaging =>
        {
            // A relay outage longer than the global schedule (~13 min) must not dead-letter every mail.
            TimeSpan[] backoff = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2)];
            messaging.Queues.TryAdd(SparkMailQueues.Transactional, new SparkQueueOptions { MaxAttempts = 10, Backoff = backoff });
            // Strict: 20 a minute (GCRA; a burst after idle can reach ~3x, measured S-M3).
            messaging.Queues.TryAdd(SparkMailQueues.Bulk, new SparkQueueOptions { MaxPerInterval = 20, Interval = TimeSpan.FromMinutes(1), MaxAttempts = 10, Backoff = backoff });
        });

        services.TryAddSingleton<ISparkMailTemplateResolver, SparkMailTemplateResolver>();
        services.TryAddSingleton<SparkMailRenderer>();
        services.TryAddSingleton<SparkMailPayloadProtector>();
        services.TryAddSingleton<SparkMailUnsubscribeTokens>();
        services.TryAddSingleton<ISparkMailSuppressions, SparkMailSuppressions>();
        services.TryAddSingleton<SparkMailDeliveryStore>();
        services.TryAddSingleton<ISparkMailBounceParser, DsnBounceParser>();
        services.TryAddScoped<SparkMailBounceProcessor>();
        services.TryAddScoped<SparkMailer>();
        services.TryAddScoped<ISparkMailer>(sp => sp.GetRequiredService<SparkMailer>());

        // The transport the configuration names; AddMailTransport<T>() replaces it.
        services.TryAddSingleton<ISparkMailTransport>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<SparkMailOptions>>().Value;
            return !string.IsNullOrWhiteSpace(o.Smtp.Host)
                ? ActivatorUtilities.CreateInstance<SmtpMailTransport>(sp)
                : ActivatorUtilities.CreateInstance<PickupFolderMailTransport>(sp);
        });

        // Handlers: plain IRecipient<T> registrations (ImplementationType set), which is what Messaging
        // discovers queues and the type allow-list from.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecipient<SparkMailMessage>, SparkMailHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecipient<SparkMailCampaignMessage>, SparkMailCampaignHandler>());

        builder.Registry.AddMiddleware(app => SparkMailStartup.Validate(app.ApplicationServices));
        // The per-library method the Endpoints generator emits for this assembly (AssemblyInfo.cs):
        // /spark/mail/unsubscribe and /spark/mail/bounces (the latter answers 404 unless enabled).
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkMailManagerEndpoints());
        return builder;
    }

    /// <summary>A custom transport, replacing SMTP and the pickup folder (singleton).</summary>
    public static ISparkBuilder AddMailTransport<TTransport>(this ISparkBuilder builder) where TTransport : class, ISparkMailTransport
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<ISparkMailTransport, TTransport>());
        builder.Services.AddSingleton(new SparkMailCustomTransportMarker());
        return builder;
    }

    /// <summary>A bounce parser for another report format (the secret check stays in the framework).</summary>
    public static ISparkBuilder AddMailBounceParser<TParser>(this ISparkBuilder builder) where TParser : class, ISparkMailBounceParser
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<ISparkMailBounceParser, TParser>());
        return builder;
    }

    /// <summary>An extra template layer, between the app's folder and embedded defaults.</summary>
    public static ISparkBuilder AddMailTemplateStore(this ISparkBuilder builder, ISparkMailTemplateStore store)
    {
        builder.Services.AddSingleton(store);
        return builder;
    }
}

internal sealed record SparkMailCustomTransportMarker;

/// <summary>Startup refusals (#460, D9) and template validation (owner decision: multi-language templates).</summary>
internal static class SparkMailStartup
{
    public static void Validate(IServiceProvider services)
    {
        var o = services.GetRequiredService<IOptions<SparkMailOptions>>().Value;
        var environment = services.GetRequiredService<IHostEnvironment>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("MintPlayer.Spark.MailManager");
        var problems = Problems(o, services.GetService<SparkMailCustomTransportMarker>() is not null, services.GetService<IMessageBus>() is not null || HasMessageBus(services));
        if (problems.Count > 0)
            throw new InvalidOperationException("MailManager is misconfigured: " + string.Join(" ", problems));

        _ = CultureInfo.GetCultureInfo(o.DefaultCulture);

        var resolver = services.GetRequiredService<ISparkMailTemplateResolver>();
        var renderer = services.GetRequiredService<SparkMailRenderer>();
        foreach (var message in ValidateTemplates(resolver, renderer))
        {
            if (environment.IsDevelopment())
                logger.LogWarning("{Problem}", message);
            else
                throw new InvalidOperationException(message);
        }
    }

    private static bool HasMessageBus(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return scope.ServiceProvider.GetService<IMessageBus>() is not null;
    }

    internal static List<string> Problems(SparkMailOptions o, bool customTransport, bool messaging)
    {
        var problems = new List<string>();
        var smtp = !string.IsNullOrWhiteSpace(o.Smtp.Host);
        var pickup = !string.IsNullOrWhiteSpace(o.PickupFolder);
        if (!messaging)
            problems.Add("It queues through Spark Messaging: call spark.AddMessaging().");
        if (!customTransport && !smtp && !pickup)
            problems.Add("No transport: set Spark:Mail:Smtp:Host (SMTP) or Spark:Mail:PickupFolder (.eml files), or register one with AddMailTransport<T>().");
        if (!customTransport && smtp && pickup)
            problems.Add("Both Spark:Mail:Smtp:Host and Spark:Mail:PickupFolder are set; choose one.");
        if (string.IsNullOrWhiteSpace(o.From.Address))
            problems.Add("Spark:Mail:From:Address is required.");
        if (o.Bounces.Endpoint.Enabled && (o.Bounces.Endpoint.Secret?.Length ?? 0) < 32)
            problems.Add("Spark:Mail:Bounces:Endpoint:Enabled needs Spark:Mail:Bounces:Endpoint:Secret of at least 32 characters.");
        return problems;
    }

    /// <summary>A template without a neutral <c>.mjml</c>; any file that does not parse.</summary>
    internal static IEnumerable<string> ValidateTemplates(ISparkMailTemplateResolver resolver, SparkMailRenderer renderer)
    {
        var files = resolver.AllFiles().Where(f => f.Path.EndsWith(".mjml", StringComparison.OrdinalIgnoreCase) || f.Path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var name in files.Where(f => f.Path.EndsWith(".mjml", StringComparison.OrdinalIgnoreCase)).Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!files.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) && f.Culture.Length == 0 && f.Path.EndsWith(".mjml", StringComparison.OrdinalIgnoreCase)))
                yield return $"Mail template '{name}' has no neutral file '{name}.mjml': a recipient whose culture has no variant would get no mail.";

        foreach (var file in files)
        {
            string? error = null;
            try { renderer.Parse(file); }
            catch (SparkMailTemplateException ex) { error = ex.Message; }
            if (error is not null)
                // A file that does not parse fails every environment: it would fail every send.
                throw new InvalidOperationException(error);
        }
    }
}
