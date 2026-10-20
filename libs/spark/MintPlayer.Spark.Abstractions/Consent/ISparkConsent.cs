using Microsoft.Extensions.DependencyInjection;

namespace MintPlayer.Spark.Abstractions.Consent;

/// <summary>
/// The consent primitive's server side (commerce PRD §4.20, X21/GA9): whether a visitor or an account granted a
/// purpose, and recording what they chose. Generic, not commerce-specific (K6). Registered by
/// <c>spark.AddConsent()</c> (Spark core); see <c>docs/guide-consent.md</c>.
/// </summary>
/// <remarks>
/// <para><b>Legal basis</b> (checked against the official texts, D37; EUR-Lex could not be fetched by the agent on
/// 2026-10-19, so the citations below are to be confirmed against
/// <a href="https://eur-lex.europa.eu/eli/reg/2016/679/oj">Regulation (EU) 2016/679</a> and
/// <a href="https://eur-lex.europa.eu/eli/dir/2002/58/oj">Directive 2002/58/EC</a>):</para>
/// <list type="bullet">
/// <item>ePrivacy Art. 5(3): storing or reading information on the visitor's device needs consent unless strictly
/// necessary for the service the visitor asked for. <see cref="SparkConsentPurposes.Necessary"/> is that exemption,
/// so it is always granted and never asked.</item>
/// <item>GDPR Art. 4(11) and Recital 32: consent is a clear affirmative act; silence, pre-ticked boxes or inactivity
/// are not consent. An undecided purpose therefore reads as <b>not granted</b>.</item>
/// <item>GDPR Art. 7(1): the controller must be able to demonstrate consent. Every decision is recorded with its
/// policy version, time, source, language and a hash of the text shown (the proof).</item>
/// <item>GDPR Art. 7(3): withdrawal at any time, as easy as giving. <see cref="RecordAsync"/> takes a refusal the same
/// way it takes a grant, and the banner offers both with equal weight.</item>
/// </list>
/// </remarks>
public interface ISparkConsent
{
    /// <summary>
    /// Whether the current request's account (when signed in) or visitor (X1) granted <paramref name="purpose"/>
    /// under the current policy version and within the validity period. <see cref="SparkConsentPurposes.Necessary"/>
    /// is always granted; an undeclared purpose never is.
    /// </summary>
    Task<bool> HasAsync(string purpose, CancellationToken cancellationToken = default);

    /// <summary>The same question for a given subject, outside a request: a mail job checking marketing consent.</summary>
    Task<bool> HasAsync(SparkConsentSubject subject, string purpose, CancellationToken cancellationToken = default);

    /// <summary>The declared purposes and the current request's decisions, as the banner and settings page show them.</summary>
    Task<SparkConsentState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the current request's decisions. A guest gets a visitor cookie (strictly necessary: it stores the
    /// choice itself) when the request is an action. Unknown purposes and <see cref="SparkConsentPurposes.Necessary"/>
    /// are ignored. Returns the new state.
    /// </summary>
    /// <exception cref="InvalidOperationException">The request is a read (a choice is recorded by an action only).</exception>
    Task<SparkConsentState> RecordAsync(SparkConsentChoice choice, CancellationToken cancellationToken = default);

    /// <summary>Records <paramref name="choice"/> for a subject outside a request (a checkout checkbox for a guest's e-mail address).</summary>
    Task RecordAsync(SparkConsentSubject subject, SparkConsentChoice choice, CancellationToken cancellationToken = default);
}

/// <summary>The purpose names Spark and its packages declare (§4.20). A host may add its own; names are stored on consent records, so they are permanent.</summary>
public static class SparkConsentPurposes
{
    /// <summary>Strictly necessary processing (ePrivacy Art. 5(3) exemption); needs no consent and is never asked.</summary>
    public const string Necessary = "necessary";

    /// <summary>Analytics and measurement.</summary>
    public const string Analytics = "analytics";

    /// <summary>Marketing e-mail (ePrivacy Art. 13).</summary>
    public const string MarketingMail = "marketing-mail";

    /// <summary>A third-party human-verification challenge that loads the vendor's script (GA9, §4.12).</summary>
    public const string ThirdPartyChallenge = "third-party-challenge";

    /// <summary>Embedded third-party media.</summary>
    public const string ThirdPartyMedia = "third-party-media";
}

/// <summary>Where a decision was taken: stored on the record as part of the proof (GDPR Art. 7(1)).</summary>
public static class SparkConsentSources
{
    public const string Banner = "banner";
    public const string SettingsPage = "settings";
    public const string Checkout = "checkout";
    public const string Unsubscribe = "unsubscribe";
    /// <summary>The visitor's decisions moved onto the account at sign-in.</summary>
    public const string SignInMerge = "sign-in-merge";
    /// <summary>Recorded by the application's own code.</summary>
    public const string Application = "application";
}

/// <summary>Whose consent a record holds.</summary>
public enum SparkConsentSubjectKind
{
    /// <summary>A signed-in account (<c>SparkUser.Id</c>).</summary>
    Account,
    /// <summary>A guest's visitor id (X1).</summary>
    Visitor,
    /// <summary>An e-mail address without an account (a guest's marketing opt-in at checkout).</summary>
    Email,
}

/// <summary>A consent subject. An e-mail address is normalised (trimmed, lower-case).</summary>
public readonly record struct SparkConsentSubject
{
    private SparkConsentSubject(SparkConsentSubjectKind kind, string id)
    {
        Kind = kind;
        Id = id;
    }

    public SparkConsentSubjectKind Kind { get; }

    public string Id { get; }

    public static SparkConsentSubject Account(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return new(SparkConsentSubjectKind.Account, userId);
    }

    public static SparkConsentSubject Visitor(string visitorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(visitorId);
        return new(SparkConsentSubjectKind.Visitor, visitorId);
    }

    public static SparkConsentSubject Email(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        return new(SparkConsentSubjectKind.Email, address.Trim().ToLowerInvariant());
    }

    public override string ToString() => $"{Kind}:{Id}";
}

/// <summary>A vendor behind a purpose (shown in the settings page; "informed" consent, GDPR Art. 4(11)).</summary>
[SparkContract]
public sealed record SparkConsentVendor(string Name, string? PrivacyPolicyUrl = null);

/// <summary>
/// A purpose a package or the host declares with <see cref="SparkConsentRegistration.AddConsentPurpose"/>: its name,
/// translated title and description, and the vendors behind it.
/// </summary>
public sealed record SparkConsentPurpose(
    string Name,
    TranslatedString Title,
    TranslatedString Description,
    IReadOnlyList<SparkConsentVendor>? Vendors = null);

/// <summary>A purpose as the client shows it, with the current decision.</summary>
/// <param name="Granted"><see langword="true"/> granted, <see langword="false"/> refused, <see langword="null"/> not decided (or the decision is stale: older policy version or past its validity).</param>
/// <param name="DecidedAt">When the shown decision was taken (ISO 8601), or <see langword="null"/>.</param>
[SparkContract]
public sealed record SparkConsentPurposeState(
    string Name,
    TranslatedString Title,
    TranslatedString Description,
    IReadOnlyList<SparkConsentVendor> Vendors,
    bool Necessary,
    bool? Granted,
    DateTimeOffset? DecidedAt);

/// <summary>The banner's and settings page's view (<c>GET /spark/consent</c>).</summary>
/// <param name="PolicyVersion">The host's current consent policy version (<c>Spark:Consent:PolicyVersion</c>).</param>
/// <param name="NeedsPrompt">Some non-necessary purpose has no current decision: show the banner.</param>
/// <param name="PolicyUrl">The host's privacy or cookie policy page, linked from the banner.</param>
[SparkContract]
public sealed record SparkConsentState(
    string PolicyVersion,
    bool NeedsPrompt,
    string? PolicyUrl,
    IReadOnlyList<SparkConsentPurposeState> Purposes);

/// <summary>Decisions to record (<c>POST /spark/consent</c>).</summary>
/// <param name="Decisions">Purpose name → granted. A purpose left out keeps its decision.</param>
/// <param name="Source">A <see cref="SparkConsentSources"/> value.</param>
/// <param name="Language">The language the texts were shown in (part of the proof); null means the request's.</param>
[SparkContract]
public sealed record SparkConsentChoice(
    Dictionary<string, bool> Decisions,
    string Source,
    string? Language = null);

/// <summary>What an unsubscribe link's page shows (<c>GET /spark/consent/unsubscribe?token=</c>).</summary>
/// <param name="Purpose">The purpose the link withdraws.</param>
/// <param name="Recipient">The address, masked (<c>j***@example.com</c>).</param>
/// <param name="Granted">Whether the purpose is still granted.</param>
[SparkContract]
public sealed record SparkConsentUnsubscribeInfo(string Purpose, string Recipient, bool Granted);

/// <summary>An unsubscribe link's one-click request (<c>POST /spark/consent/unsubscribe</c>).</summary>
[SparkContract]
public sealed record SparkConsentUnsubscribeRequest(string Token);

/// <summary>
/// Unsubscribe links for marketing mail (ePrivacy Art. 13(2): the recipient can object "free of charge and in an easy
/// manner" on every message; GDPR Art. 21(2)–(3)). Registered with the consent primitive.
/// </summary>
public interface ISparkConsentLinks
{
    /// <summary>The token of a link that withdraws <paramref name="purpose"/> for <paramref name="subject"/>. Never expires.</summary>
    string CreateUnsubscribeToken(SparkConsentSubject subject, string purpose = SparkConsentPurposes.MarketingMail);

    /// <summary>The page link for the mail body: the SPA's unsubscribe page with the token (one click there, no sign-in).</summary>
    Uri CreateUnsubscribeUrl(SparkConsentSubject subject, string purpose = SparkConsentPurposes.MarketingMail);

    /// <summary>
    /// The <c>List-Unsubscribe</c> and <c>List-Unsubscribe-Post</c> headers (RFC 2369, RFC 8058 one-click) every marketing
    /// mail carries (§4.20). The POST target is <c>/spark/consent/unsubscribe/one-click</c>.
    /// </summary>
    IReadOnlyDictionary<string, string> CreateListUnsubscribeHeaders(SparkConsentSubject subject, string purpose = SparkConsentPurposes.MarketingMail);
}

/// <summary>
/// The consent records of an account, for the GDPR export and erasure (Art. 15, 17, 20). Authorization calls it from
/// its personal-data contributor and account-deletion handler; an application with its own account store calls it itself.
/// </summary>
public interface ISparkConsentPersonalData
{
    /// <summary>The account's records (and those of <paramref name="email"/>, when given): decisions and history.</summary>
    Task<object?> ExportAsync(string userId, string? email, CancellationToken cancellationToken);

    /// <summary>Deletes the account's records (and <paramref name="email"/>'s). Idempotent.</summary>
    Task EraseAsync(string userId, string? email, CancellationToken cancellationToken);
}

/// <summary>A package's declaration that it processes data only with consent for <see cref="Purpose"/>.</summary>
/// <param name="Purpose">A <see cref="SparkConsentPurposes"/> value or the host's own.</param>
/// <param name="RequiredBy">The package or feature that declared it, for the startup message.</param>
public sealed record SparkConsentRequirement(string Purpose, string RequiredBy);

/// <summary>Declares purposes and requirements, and checks them at startup (§4.2, GA9).</summary>
public static class SparkConsentRegistration
{
    /// <summary>
    /// Declares a purpose the banner asks about. Packages that reference only Abstractions declare theirs here;
    /// <c>spark.AddConsent()</c> declares the built-in ones. The last declaration of a name wins.
    /// </summary>
    public static IServiceCollection AddConsentPurpose(this IServiceCollection services, SparkConsentPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose.Name);
        if (string.Equals(purpose.Name, SparkConsentPurposes.Necessary, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("'necessary' needs no consent and is not declared as a purpose.", nameof(purpose));
        if (!System.Text.RegularExpressions.Regex.IsMatch(purpose.Name, "^[a-z0-9][a-z0-9-]{0,62}$"))
            throw new ArgumentException($"Purpose name '{purpose.Name}' must be lower-case letters, digits and dashes (it is stored on records and used in ids).", nameof(purpose));
        services.AddSingleton(purpose);
        return services;
    }

    /// <summary>
    /// Declares that <paramref name="requiredBy"/> needs consent for <paramref name="purpose"/>. Startup then refuses
    /// the app unless the consent primitive is registered and the purpose declared (<see cref="ValidateWired"/>).
    /// </summary>
    public static IServiceCollection RequiresConsent(this IServiceCollection services, string purpose, string requiredBy)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredBy);
        services.AddSingleton(new SparkConsentRequirement(purpose, requiredBy));
        return services;
    }

    /// <summary>
    /// The requirements that cannot be met: without an <see cref="ISparkConsent"/>, every declared requirement other
    /// than <see cref="SparkConsentPurposes.Necessary"/>; with one, those whose purpose is not declared (the banner could
    /// never ask for it, so it would never be granted). <paramref name="additional"/> adds requirements found elsewhere,
    /// such as a payment provider's. Empty when everything is met.
    /// </summary>
    public static IReadOnlyList<SparkConsentRequirement> FindUnmet(IServiceProvider services, IEnumerable<SparkConsentRequirement>? additional = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var isService = services.GetService<IServiceProviderIsService>();
        var wired = isService?.IsService(typeof(ISparkConsent)) ?? services.GetService<ISparkConsent>() is not null;
        var declared = wired
            ? services.GetServices<SparkConsentPurpose>().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        return services.GetServices<SparkConsentRequirement>()
            .Concat(additional ?? [])
            .Where(r => !string.Equals(r.Purpose, SparkConsentPurposes.Necessary, StringComparison.OrdinalIgnoreCase))
            .Where(r => !wired || !declared.Contains(r.Purpose))
            .ToArray();
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> naming every unmet requirement (GA9). <c>UseSpark()</c> calls it.</summary>
    public static void ValidateWired(IServiceProvider services, IEnumerable<SparkConsentRequirement>? additional = null)
    {
        var unmet = FindUnmet(services, additional);
        if (unmet.Count == 0)
            return;
        throw new InvalidOperationException(
            "These features need the visitor's consent, but the consent primitive is not registered (spark.AddConsent()) or the purpose is not declared: "
            + string.Join("; ", unmet.Select(r => $"{r.RequiredBy} needs '{r.Purpose}'"))
            + ". Register the consent primitive and declare the purpose, or remove the feature (commerce PRD GA9: no third-party processing without consent).");
    }
}

/// <summary>Gating helpers for adapters (third-party challenges, analytics, marketing mail).</summary>
public static class SparkConsentGate
{
    /// <summary>
    /// Whether the current request may use <paramref name="purpose"/>: <see langword="false"/> when the consent primitive
    /// is not registered (the safe direction), else <see cref="ISparkConsent.HasAsync(string, CancellationToken)"/>.
    /// A third-party challenge adapter falls back to ALTCHA on <see langword="false"/> (§4.12), never blocks the visitor.
    /// </summary>
    public static Task<bool> AllowsAsync(this IServiceProvider services, string purpose, CancellationToken cancellationToken = default)
    {
        if (string.Equals(purpose, SparkConsentPurposes.Necessary, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(true);
        return services.GetService<ISparkConsent>() is { } consent
            ? consent.HasAsync(purpose, cancellationToken)
            : Task.FromResult(false);
    }

    /// <summary>Whether a mail job may send marketing mail to <paramref name="email"/>, or to the account <paramref name="userId"/> when given (either granted is enough).</summary>
    public static async Task<bool> AllowsMarketingMailAsync(this ISparkConsent consent, string? userId, string? email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consent);
        if (userId is { Length: > 0 } && await consent.HasAsync(SparkConsentSubject.Account(userId), SparkConsentPurposes.MarketingMail, cancellationToken))
            return true;
        return email is { Length: > 0 } && await consent.HasAsync(SparkConsentSubject.Email(email), SparkConsentPurposes.MarketingMail, cancellationToken);
    }
}
