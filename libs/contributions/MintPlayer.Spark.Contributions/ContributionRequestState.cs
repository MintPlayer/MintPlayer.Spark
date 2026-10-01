using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Contributions;

/// <summary>
/// Per request: the save notices (PRD Q3) and the moderator audits (Q4) waiting for their commit. Both
/// are only sent after the commit — a notice on a save that then answers 409 would describe a write
/// that never happened, and so would an audit entry.
/// </summary>
internal sealed class ContributionRequestState
{
    public List<(TranslatedString Message, NotificationKind Kind)> Notices { get; } = [];

    public List<SatelliteAuditEntry> Audits { get; } = [];

    /// <summary>
    /// Who wrote the version each hydrated row shows, by (target id, property) and then row key (the
    /// slot key; empty for a single-valued property). Filled by hydration, read by the after-load
    /// decoration that tells the client which rows are the caller's own (conflict merge, M1c).
    /// </summary>
    public Dictionary<(string TargetId, string Property), Dictionary<string, string>> RowContributors { get; } = [];

    /// <summary>Sends the notices to the response's <c>operations</c>.</summary>
    public void FlushNotices(IServiceProvider services)
    {
        if (Notices.Count == 0)
            return;
        var client = services.GetService<IClientAccessor>();
        foreach (var (message, kind) in Notices)
            client?.Notify(message, kind);
        Notices.Clear();
    }

    /// <summary>Hands the audits to every registered <see cref="ISatelliteAuditSink"/> (Moderation's, when present).</summary>
    public async Task FlushAuditsAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (Audits.Count == 0)
            return;
        var entries = Audits.ToList();
        Audits.Clear();
        foreach (var sink in services.GetServices<ISatelliteAuditSink>())
            foreach (var entry in entries)
                await sink.RecordAsync(entry, cancellationToken);
    }
}

/// <summary>Display names of contributors, resolved at read time through core's optional <see cref="ISparkUserNameResolver"/>.</summary>
internal static class ContributorNames
{
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    /// <summary>One batched call for every distinct id; empty without a resolver.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> ResolveAsync(IServiceProvider services, IEnumerable<string?> userIds, CancellationToken cancellationToken = default)
    {
        var resolver = services.GetService<ISparkUserNameResolver>();
        if (resolver is null)
            return None;
        var distinct = userIds.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
            return None;
        return await resolver.ResolveAsync(distinct, cancellationToken) ?? None;
    }

    public static string? NameOf(this IReadOnlyDictionary<string, string> names, string? userId)
        => userId is not null && names.TryGetValue(userId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : null;
}

/// <summary>
/// The notices' texts (PRD Q3), in every language (the client shows its user's). An app overrides one by
/// defining the same key in its <c>translations.json</c>; otherwise the built-in English, French and Dutch apply.
/// </summary>
internal static class ContributionMessages
{
    /// <summary><c>{0}</c> = the slot (<c>en/Latn</c>), <c>{1}</c> = the contributor now shown.</summary>
    public const string WithdrawnNowShowing = "contributions.withdrawnNowShowing";

    /// <summary><c>{0}</c> = the slot.</summary>
    public const string WithdrawnNowShowingAnother = "contributions.withdrawnNowShowingAnother";

    /// <summary><c>{0}</c> = the slot.</summary>
    public const string WithdrawnNoneLeft = "contributions.withdrawnNoneLeft";

    /// <summary><c>{0}</c> = the slot, <c>{1}</c> = the contributor whose version stays.</summary>
    public const string NotYoursStays = "contributions.notYoursStays";

    /// <summary><c>{0}</c> = the slot.</summary>
    public const string NotYoursStaysAnother = "contributions.notYoursStaysAnother";

    /// <summary><c>{0}</c> = how many newer versions the revert hid.</summary>
    public const string Reverted = "contributions.reverted";

    internal static readonly IReadOnlyDictionary<string, TranslatedString> BuiltIn = new Dictionary<string, TranslatedString>(StringComparer.Ordinal)
    {
        [Reverted] = TranslatedString.Create(
            "This version is current again; {0} newer version(s) were hidden.",
            "Cette version est de nouveau la version actuelle ; {0} version(s) plus récente(s) ont été masquées.",
            "Deze versie is opnieuw de huidige; {0} nieuwere versie(s) werden verborgen."),
        [WithdrawnNowShowing] = TranslatedString.Create(
            "Your version of {0} was withdrawn; {1}'s version is shown now.",
            "Votre version de {0} a été retirée ; la version de {1} est maintenant affichée.",
            "Uw versie van {0} is ingetrokken; nu wordt de versie van {1} getoond."),
        [WithdrawnNowShowingAnother] = TranslatedString.Create(
            "Your version of {0} was withdrawn; another contributor's version is shown now.",
            "Votre version de {0} a été retirée ; la version d'un autre contributeur est maintenant affichée.",
            "Uw versie van {0} is ingetrokken; nu wordt de versie van een andere bijdrager getoond."),
        [WithdrawnNoneLeft] = TranslatedString.Create(
            "Your version of {0} was withdrawn; no version of it is left.",
            "Votre version de {0} a été retirée ; il n'en reste aucune version.",
            "Uw versie van {0} is ingetrokken; er is geen versie meer over."),
        [NotYoursStays] = TranslatedString.Create(
            "{0} shows {1}'s version, which only its author or a moderator can remove: it stays.",
            "{0} affiche la version de {1}, que seuls son auteur ou un modérateur peuvent retirer : elle reste.",
            "{0} toont de versie van {1}, die alleen de auteur of een moderator kan verwijderen: die blijft staan."),
        [NotYoursStaysAnother] = TranslatedString.Create(
            "{0} shows another contributor's version, which only its author or a moderator can remove: it stays.",
            "{0} affiche la version d'un autre contributeur, que seuls son auteur ou un modérateur peuvent retirer : elle reste.",
            "{0} toont de versie van een andere bijdrager, die alleen de auteur of een moderator kan verwijderen: die blijft staan."),
    };

    /// <summary>
    /// The notice formatted in every language: the built-in texts, overlaid by the app's
    /// <c>translations.json</c> entry for the key. Every language travels because the language the user
    /// picked in ng-spark never reaches the server (it is not the browser's <c>Accept-Language</c>): the
    /// client resolves it, as it does labels and validation messages.
    /// </summary>
    public static TranslatedString Format(IServiceProvider services, string key, params object[] arguments)
    {
        var templates = new Dictionary<string, string>(BuiltIn[key].Translations, StringComparer.Ordinal);
        if (services.GetService<ITranslationsLoader>()?.Resolve(key) is { } app)
            foreach (var (language, text) in app.Translations)
                if (!string.IsNullOrEmpty(text))
                    templates[language] = text;

        var formatted = new TranslatedString();
        foreach (var (language, text) in templates)
            formatted.Translations[language] = string.Format(System.Globalization.CultureInfo.InvariantCulture, text, arguments);
        return formatted;
    }
}
