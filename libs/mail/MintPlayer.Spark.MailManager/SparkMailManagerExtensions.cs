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
    /// (<see cref="UseSmtpTransport"/>, <see cref="UseMailpitTransport"/>, <see cref="UsePickupFolderTransport"/>
    /// or <see cref="AddMailTransport{TTransport}"/>; with none registered, SMTP when <c>Smtp:Host</c> is
    /// set, the pickup folder when <c>PickupFolder</c> is set), templates from the app's <c>Templates/Mail</c> folder
    /// over embedded defaults, the <c>mail-transactional</c> and <c>mail-bulk</c> lanes, the suppression
    /// list, one-click unsubscribe, and — opt-in — VERP and the bounce endpoint. Binds <c>Spark:Mail</c>,
    /// then applies <paramref name="configure"/>. Requires <c>spark.AddMessaging()</c>.
    /// </summary>
    /// <remarks>
    /// Startup refuses: no transport, two transports, no <c>From:Address</c>, a template without a neutral
    /// file (a warning in Development), a template that does not parse, a bounce endpoint without a
    /// 32-character secret, <c>Development:RedirectTo</c> in Production, and — in Development — a
    /// transport that delivers to real recipients without <c>Development:RedirectTo</c>. The Authorization package's account mail goes through this automatically.
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

        // The fallback when no transport is registered: the one the configuration names. Use*Transport()
        // and AddMailTransport<T>() replace it (in either order: TryAdd).
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
        // /spark/mail/unsubscribe and /spark/mail/bounces (the latter answers 503 unless enabled).
        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkMailManagerEndpoints());
        return builder;
    }

    /// <summary>
    /// SMTP through MailKit, bound from <c>Spark:Mail:Smtp</c> (<c>Host</c> required); <paramref name="configure"/>
    /// runs after binding. Delivers to real recipients unless the host is loopback.
    /// </summary>
    public static ISparkBuilder UseSmtpTransport(this ISparkBuilder builder, Action<SparkMailSmtpOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.PostConfigure<SparkMailOptions>(o => configure(o.Smtp));
        return builder.RegisterMailTransport<SmtpMailTransport>(nameof(UseSmtpTransport));
    }

    /// <summary>
    /// SMTP into a local Mailpit (<c>docker run -d --name mailpit -p 1025:1025 -p 8025:8025 axllent/mailpit</c>):
    /// <c>localhost:1025</c>, no TLS, no authentication, overridable through <c>Spark:Mail:Mailpit:{Host,Port,Tags}</c>
    /// and <paramref name="configure"/>. Every mail gets Mailpit's <c>X-Tags</c> (template, lane, extra tags).
    /// Never delivers to real recipients, so Development needs no <c>RedirectTo</c>. With
    /// <c>Spark:Mail:Mailpit:AutoStart</c>, Mailpit is started with the host in Development (see
    /// <see cref="SparkMailMailpitOptions.AutoStart"/>).
    /// </summary>
    public static ISparkBuilder UseMailpitTransport(this ISparkBuilder builder, Action<SparkMailMailpitOptions>? configure = null)
    {
        if (configure is not null)
            builder.Services.PostConfigure<SparkMailOptions>(o => configure(o.Mailpit));
        // Spark:Mail:Mailpit:AutoStart (Development only); a no-op hosted service otherwise.
        builder.Services.TryAddSingleton<IMailpitLauncher, MailpitLauncher>();
        builder.Services.AddHostedService<MailpitAutoStart>();
        return builder.RegisterMailTransport<MailpitMailTransport>(nameof(UseMailpitTransport));
    }

    /// <summary>
    /// Every mail as an <c>.eml</c> file in <c>Spark:Mail:PickupFolder</c>, or in <paramref name="folder"/>
    /// (relative to the content root) when given. Never delivers to real recipients.
    /// </summary>
    public static ISparkBuilder UsePickupFolderTransport(this ISparkBuilder builder, string? folder = null)
    {
        if (!string.IsNullOrWhiteSpace(folder))
            builder.Services.PostConfigure<SparkMailOptions>(o => o.PickupFolder = folder);
        return builder.RegisterMailTransport<PickupFolderMailTransport>(nameof(UsePickupFolderTransport));
    }

    /// <summary>
    /// A custom transport (singleton). It delivers to real recipients unless it overrides
    /// <see cref="ISparkMailTransport.DeliversToRealRecipients"/>.
    /// </summary>
    public static ISparkBuilder AddMailTransport<TTransport>(this ISparkBuilder builder) where TTransport : class, ISparkMailTransport
        => builder.RegisterMailTransport<TTransport>($"AddMailTransport<{typeof(TTransport).Name}>");

    /// <summary>Explicit registration wins over the configuration fallback; a second one is a startup error.</summary>
    private static ISparkBuilder RegisterMailTransport<TTransport>(this ISparkBuilder builder, string name) where TTransport : class, ISparkMailTransport
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<ISparkMailTransport, TTransport>());
        builder.Services.AddSingleton(new SparkMailTransportRegistration(name));
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

/// <summary>One explicit transport registration (<c>UseSmtpTransport</c>, <c>AddMailTransport&lt;T&gt;</c>, …).</summary>
internal sealed record SparkMailTransportRegistration(string Name);

/// <summary>Startup refusals (#460, D9) and template validation (owner decision: multi-language templates).</summary>
internal static class SparkMailStartup
{
    public static void Validate(IServiceProvider services)
    {
        var o = services.GetRequiredService<IOptions<SparkMailOptions>>().Value;
        var environment = services.GetRequiredService<IHostEnvironment>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("MintPlayer.Spark.MailManager");
        var transports = services.GetServices<SparkMailTransportRegistration>().Select(r => r.Name).ToList();
        var problems = Problems(o, transports, HasMessageBus(services), environment.IsProduction());
        if (problems.Count == 0
            && DevelopmentProblem(o, environment.IsDevelopment(), services.GetRequiredService<ISparkMailTransport>()) is { } development)
            problems.Add(development);
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

    // IMessageBus is scoped (AddSparkMessaging), and a Development host validates scopes: resolving it
    // from the root provider throws. Ask whether it is registered instead of resolving it.
    private static bool HasMessageBus(IServiceProvider services)
    {
        if (services.GetService<IServiceProviderIsService>() is { } isService)
            return isService.IsService(typeof(IMessageBus));
        using var scope = services.CreateScope();
        return scope.ServiceProvider.GetService<IMessageBus>() is not null;
    }

    /// <param name="o">The bound options.</param>
    /// <param name="transports">The explicit transport registrations, by name; empty = the configuration fallback.</param>
    /// <param name="messaging">Whether Spark Messaging is registered.</param>
    /// <param name="production">Whether the environment is Production.</param>
    internal static List<string> Problems(SparkMailOptions o, IReadOnlyList<string> transports, bool messaging, bool production = false)
    {
        var problems = new List<string>();
        var smtp = !string.IsNullOrWhiteSpace(o.Smtp.Host);
        var pickup = !string.IsNullOrWhiteSpace(o.PickupFolder);
        var registered = transports.Count == 1 ? transports[0] : null;
        if (!messaging)
            problems.Add("It queues through Spark Messaging: call spark.AddMessaging().");
        if (transports.Count > 1)
            problems.Add($"{transports.Count} mail transports are registered ({string.Join(", ", transports)}); register one.");
        else if (registered is null && !smtp && !pickup)
            problems.Add("No transport: set Spark:Mail:Smtp:Host (SMTP) or Spark:Mail:PickupFolder (.eml files), or register one with UseSmtpTransport(), UseMailpitTransport(), UsePickupFolderTransport() or AddMailTransport<T>().");
        else if (registered == nameof(SparkMailManagerExtensions.UseSmtpTransport) && !smtp)
            problems.Add("UseSmtpTransport() needs Spark:Mail:Smtp:Host.");
        else if (registered == nameof(SparkMailManagerExtensions.UsePickupFolderTransport) && !pickup)
            problems.Add("UsePickupFolderTransport() needs Spark:Mail:PickupFolder or a folder argument.");
        // Both configured is ambiguous for the fallback and for the two transports that read them; Mailpit
        // and a custom transport read neither.
        if (smtp && pickup && transports.Count <= 1
            && (registered is null or nameof(SparkMailManagerExtensions.UseSmtpTransport) or nameof(SparkMailManagerExtensions.UsePickupFolderTransport)))
            problems.Add("Both Spark:Mail:Smtp:Host and Spark:Mail:PickupFolder are set; choose one.");
        if (string.IsNullOrWhiteSpace(o.From.Address))
            problems.Add("Spark:Mail:From:Address is required.");
        if (o.Bounces.Endpoint.Enabled && (o.Bounces.Endpoint.Secret?.Length ?? 0) < 32)
            problems.Add("Spark:Mail:Bounces:Endpoint:Enabled needs Spark:Mail:Bounces:Endpoint:Secret of at least 32 characters.");
        // Other non-Development environments (Staging, E2E, …) keep redirecting, with a warning per mail.
        if (production && !string.IsNullOrWhiteSpace(o.Development.RedirectTo))
            problems.Add("Spark:Mail:Development:RedirectTo is set in the Production environment: every mail would go to the redirect address instead of its recipient. Remove it from the Production configuration.");
        return problems;
    }

    /// <summary>
    /// Development fails closed: a transport that <see cref="ISparkMailTransport.DeliversToRealRecipients"/>
    /// and no <c>Development:RedirectTo</c> would mail real people from a developer's machine (a copied
    /// database, a seeded address). Mailpit, the pickup folder and loopback SMTP need nothing.
    /// </summary>
    internal static string? DevelopmentProblem(SparkMailOptions o, bool development, ISparkMailTransport transport)
        => development && transport.DeliversToRealRecipients && string.IsNullOrWhiteSpace(o.Development.RedirectTo)
            ? $"In Development the mail transport ({transport.GetType().Name}) delivers to real recipients and Spark:Mail:Development:RedirectTo is not set. " +
              "Set it to your own address (dotnet user-secrets set \"Spark:Mail:Development:RedirectTo\" \"you@example.com\"), " +
              "or catch the mail locally with UseMailpitTransport() (docker run -d --name mailpit -p 1025:1025 -p 8025:8025 axllent/mailpit)."
            : null;

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
