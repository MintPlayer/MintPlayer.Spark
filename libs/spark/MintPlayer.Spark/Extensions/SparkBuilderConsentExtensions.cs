using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Consent;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Consent;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// The consent primitive (<c>docs/commerce_PRD.md</c> X21, <c>docs/guide-consent.md</c>):
/// <code>
/// spark.AddConsent(o => { o.PolicyVersion = "2026-10"; o.PolicyUrl = "/legal/privacy"; });
/// services.AddConsentPurpose(new SparkConsentPurpose("video", title, description, [new("Vimeo", "https://vimeo.com/privacy")]));
/// </code>
/// Without it, a package that declared <c>RequiresConsent</c> refuses to start (GA9).
/// </summary>
public static class SparkBuilderConsentExtensions
{
    /// <summary>Registers the consent primitive, its endpoints' service, the sign-in merge and the built-in purposes.</summary>
    public static ISparkBuilder AddConsent(this ISparkBuilder builder, Action<SparkConsentOptions>? configure = null)
    {
        var services = builder.Services;
        if (configure is not null)
            services.Configure(configure);

        services.TryAddScoped<SparkConsentService>();
        services.TryAddScoped<ISparkConsent>(sp => sp.GetRequiredService<SparkConsentService>());
        services.TryAddScoped<ISparkConsentLinks>(sp => sp.GetRequiredService<SparkConsentService>());
        services.TryAddScoped<ISparkConsentPersonalData>(sp => sp.GetRequiredService<SparkConsentService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ISparkVisitorMergeHandler, SparkConsentVisitorMergeHandler>());

        // Built-in purposes: declared first, so a host's own declaration of the same name (other texts, its vendors) wins.
        foreach (var purpose in BuiltInPurposes)
            services.AddConsentPurpose(purpose);
        return builder;
    }

    /// <summary>
    /// The purposes Spark's packages refer to, with neutral texts. A host replaces one by declaring the same name with
    /// its own texts and vendors: the banner must name who processes the data (GDPR Art. 13(1)(e), "informed").
    /// </summary>
    internal static IReadOnlyList<SparkConsentPurpose> BuiltInPurposes { get; } =
    [
        new(SparkConsentPurposes.Analytics,
            TranslatedString.Of(("en", "Analytics"), ("fr", "Mesure d'audience"), ("nl", "Statistieken")),
            TranslatedString.Of(
                ("en", "Measure how the site is used, to improve it."),
                ("fr", "Mesurer l'utilisation du site pour l'améliorer."),
                ("nl", "Meten hoe de site gebruikt wordt, om ze te verbeteren."))),
        new(SparkConsentPurposes.MarketingMail,
            TranslatedString.Of(("en", "News and offers by e-mail"), ("fr", "Actualités et offres par e-mail"), ("nl", "Nieuws en aanbiedingen per e-mail")),
            TranslatedString.Of(
                ("en", "Receive news and offers by e-mail. Every mail has a link to unsubscribe."),
                ("fr", "Recevoir des actualités et des offres par e-mail. Chaque e-mail contient un lien de désinscription."),
                ("nl", "Nieuws en aanbiedingen per e-mail ontvangen. Elke mail bevat een link om je af te melden."))),
        new(SparkConsentPurposes.ThirdPartyChallenge,
            TranslatedString.Of(("en", "External bot protection"), ("fr", "Protection anti-robot externe"), ("nl", "Externe botbeveiliging")),
            TranslatedString.Of(
                ("en", "A third-party service checks that you are not a robot; it reads information from your device. Refused: a check that runs on this site only is used instead."),
                ("fr", "Un service tiers vérifie que vous n'êtes pas un robot ; il lit des informations sur votre appareil. Refusé : une vérification effectuée uniquement sur ce site est utilisée."),
                ("nl", "Een externe dienst controleert dat je geen robot bent; die leest gegevens van je toestel. Geweigerd: dan gebruiken we een controle die alleen op deze site draait."))),
        new(SparkConsentPurposes.ThirdPartyMedia,
            TranslatedString.Of(("en", "Embedded media"), ("fr", "Médias intégrés"), ("nl", "Ingesloten media")),
            TranslatedString.Of(
                ("en", "Show videos and maps from other sites; those sites may set cookies."),
                ("fr", "Afficher des vidéos et des cartes d'autres sites ; ces sites peuvent déposer des cookies."),
                ("nl", "Video's en kaarten van andere sites tonen; die sites kunnen cookies plaatsen."))),
    ];
}
