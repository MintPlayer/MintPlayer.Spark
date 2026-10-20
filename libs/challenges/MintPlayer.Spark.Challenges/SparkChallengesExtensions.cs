using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Challenges.Altcha;
using MintPlayer.Spark.Challenges.Deceptive;
using MintPlayer.Spark.Challenges.Risk;

namespace MintPlayer.Spark.Challenges;

/// <summary>
/// Bot defence (<c>docs/commerce_PRD.md</c> §4.12, <c>docs/guide-challenges.md</c>):
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark =>
/// {
///     spark.AddConsent();                                   // needed by any hosted provider (GA9)
///     spark.AddChallenges(c => c.UseAltcha().UseTurnstile()); // Spark:Challenges; Turnstile, ALTCHA when consent is refused
/// });
/// // appsettings: "Spark": { "Challenges": { "Points": [ "queue-join" ] } }
/// </code>
/// </summary>
public static class SparkChallengesExtensions
{
    /// <summary>
    /// Registers <see cref="ISparkHumanChallenges"/>, the risk pipeline (<see cref="ISparkRiskPipeline"/>) with its built-in
    /// signals, the decision log, the deceptive measures (all off) and <c>GET /spark/challenges/offer</c>. Safe to call more
    /// than once (the waiting room calls it for the pipeline). <paramref name="configure"/> picks providers.
    /// </summary>
    public static ISparkBuilder AddChallenges(this ISparkBuilder builder, Action<SparkChallengesBuilder>? configure = null)
    {
        var services = builder.Services;
        var registry = services.GetOrAddSparkSingleton(() => new SparkChallengeProviderRegistry(), out var created);
        var challenges = new SparkChallengesBuilder(services, registry);
        if (created)
        {
            services.AddSparkConfigurationSection<SparkChallengesOptions>();
            services.AddHttpContextAccessor();
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<ISparkChallengeSwitch, OptionsChallengeSwitch>();
            services.TryAddScoped<ISparkHumanChallenges, SparkHumanChallenges>();

            services.TryAddSingleton<SparkRiskMemory>();
            services.TryAddSingleton<ISparkRiskDecisionLog, SparkRiskDecisionLog>();
            services.TryAddScoped<ISparkRiskPipeline, SparkRiskPipeline>();
            services.TryAddScoped<ISparkDeceptiveMeasures, SparkDeceptiveMeasures>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkRiskSignal, RecordedEvidenceSignal>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkRiskSignal, VelocitySignal>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkRiskSignal, HoneypotSignal>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkRiskSignal, AutomationFlagSignal>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkRiskSignal, SharedAddressSignal>());

            builder.Registry.AddMiddleware(app => ValidateStartup(app.ApplicationServices));
            builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkChallengesEndpoints());
        }
        configure?.Invoke(challenges);
        return builder;
    }

    /// <summary>
    /// Refuses startup for a deceptive measure enabled without an explicit trigger (D29), a score trigger without a score,
    /// and a risk signal keyed on nationality, residence or card-issuing country (GA11); warns while any deceptive measure
    /// is on. The consent requirement of hosted providers (GA9) is checked by <c>UseSpark()</c> (X21).
    /// </summary>
    internal static void ValidateStartup(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptionsMonitor<SparkChallengesOptions>>().CurrentValue;
        using var scope = services.CreateScope();
        var signals = scope.ServiceProvider.GetServices<ISparkRiskSignal>().Select(s => (s.Name, s.Basis)).ToList();
        var (errors, warnings) = Check(options, signals);
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("MintPlayer.Spark.Challenges");
        foreach (var warning in warnings)
            logger?.LogWarning("{Warning}", warning);
        if (errors.Count > 0)
            throw new InvalidOperationException("Challenges cannot start:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));
    }

    internal static (List<string> Errors, List<string> Warnings) Check(SparkChallengesOptions options, IEnumerable<(string Name, SparkRiskBasis Basis)> signals)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        foreach (var (name, measure) in SparkDeceptiveMeasures.Measures(options.Deceptive))
        {
            if (!measure.Enabled)
                continue;
            if (measure.Trigger == SparkDeceptiveTrigger.Unset)
                errors.Add($"Spark:Challenges:Deceptive:{Pascal(name)} is enabled without an explicit Trigger (Deterministic or RiskScore) (D29).");
            else if (measure.Trigger == SparkDeceptiveTrigger.RiskScore && measure.TriggerScore is not (> 0 and <= 1))
                errors.Add($"Spark:Challenges:Deceptive:{Pascal(name)} triggers on a risk score but TriggerScore is not between 0 and 1 (D29).");
            else
                warnings.Add($"Deceptive measure '{name}' is ON (trigger {measure.Trigger}). Deceiving a visitor who is human is a misleading practice toward a consumer (Directive 2005/29/EC) and an automated decision (GDPR Art. 22); every application is logged for appeal. See docs/guide-challenges.md.");
        }
        if (options.Risk.ShadowQueue == SparkShadowQueueMode.DeterministicOnly)
            warnings.Add("The shadow queue is ON (deterministic evidence only): flagged callers look queued and are never admitted. Logged for appeal; see docs/guide-challenges.md.");
        foreach (var point in options.Points.Where(p => !SparkChallengePoints.All.Contains(p)))
            errors.Add(string.Equals(point, SparkChallengePoints.Admission, StringComparison.OrdinalIgnoreCase)
                ? "Spark:Challenges:Points names 'admission': admission is never challenged, the queue position is the proof (§4.12)."
                : $"Spark:Challenges:Points names '{point}', which is not a challenge point ({string.Join(", ", SparkChallengePoints.All)}).");

        const SparkRiskBasis forbidden = SparkRiskBasis.Nationality | SparkRiskBasis.Residence | SparkRiskBasis.CardIssuingCountry;
        foreach (var (name, basis) in signals.Where(s => (s.Basis & forbidden) != 0))
            errors.Add($"Risk signal '{name}' is keyed on {basis & forbidden}: Regulation (EU) 2018/302 Art. 3–5 forbid treating a customer differently for nationality, place of residence or the card's issuing country (GA11).");
        return (errors, warnings);
    }

    private static string Pascal(string name) => string.Concat(name.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}

/// <summary>Picks the challenge providers (<see cref="SparkChallengesExtensions.AddChallenges"/>). Adapters add <c>UseTurnstile()</c> and friends.</summary>
public sealed class SparkChallengesBuilder
{
    internal SparkChallengesBuilder(IServiceCollection services, SparkChallengeProviderRegistry registry)
    {
        Services = services;
        Registry = registry;
    }

    public IServiceCollection Services { get; }

    internal SparkChallengeProviderRegistry Registry { get; }

    /// <summary>ALTCHA, built in: self-hosted proof of work, no third party, no consent needed. Also the consent fallback of every hosted provider.</summary>
    public SparkChallengesBuilder UseAltcha()
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<ISparkHumanChallenge, AltchaChallenge>());
        Registry.Add(AltchaChallenge.ProviderName);
        return this;
    }

    /// <summary>
    /// A provider of the host's own. A provider that <see cref="ISparkHumanChallenge.RequiresConsent"/> must be registered
    /// through <see cref="UseHostedChallenge{T}"/> so its consent requirement is declared.
    /// </summary>
    public SparkChallengesBuilder UseChallenge<T>(string name) where T : class, ISparkHumanChallenge
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<ISparkHumanChallenge, T>());
        Registry.Add(name);
        return this;
    }

    /// <summary>
    /// A third-party provider (the adapters call it): registers it, declares that it needs <c>third-party-challenge</c>
    /// consent (startup refuses it without the consent primitive, GA9, X21), and registers ALTCHA as its fallback for
    /// visitors who refuse.
    /// </summary>
    public SparkChallengesBuilder UseHostedChallenge<T>(string name, string vendor) where T : class, ISparkHumanChallenge
    {
        UseAltcha();
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<ISparkHumanChallenge, T>());
        Services.RequiresConsent(SparkConsentPurposes.ThirdPartyChallenge, $"challenge provider '{name}' ({vendor})");
        Registry.Add(name);
        return this;
    }
}
