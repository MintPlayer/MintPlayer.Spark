namespace MintPlayer.Spark.Abstractions.DateTimes;

/// <summary>
/// Whom server-rendered output is for: an IANA time zone and a culture (commerce PRD X14, D33). Mails,
/// PDFs and background jobs have no request, so no viewer zone can be resolved there; they format with
/// the locale the recipient stored, or the one captured when they placed the order.
/// </summary>
/// <param name="TimeZoneId">An IANA zone id (<c>Europe/Brussels</c>); <c>UTC</c> when nothing better is known.</param>
/// <param name="Culture">A culture name (<c>nl-BE</c>, <c>fr</c>).</param>
public sealed record SparkRecipientLocale(string TimeZoneId, string Culture);

/// <summary>
/// What a user stored on their account (<c>SparkUser.PreferredTimeZone</c> / <c>PreferredCulture</c>).
/// Either part may be <see langword="null"/>: unset, so the defaults apply.
/// </summary>
public sealed record SparkRecipientLocalePreference(string? TimeZoneId, string? Culture);

/// <summary>
/// Resolves the locale server-rendered output is formatted in (X14). The one place mails, PDFs and
/// background jobs get a time zone and culture from; pass the result to <see cref="ISparkDateTimeFormatter"/>.
/// </summary>
/// <remarks>
/// Order for a user: their stored preference, part by part, then <c>Spark:TimeZone:DefaultRecipientTimeZone</c>
/// (default <c>UTC</c>) and the application's default language. Never the current request: a job that one
/// person triggers must not format another person's mail in the first one's zone.
/// </remarks>
public interface ISparkRecipientLocale
{
    /// <summary>
    /// The viewer of the current request: the zone from <c>IRequestTimeZoneResolver</c> (header, cookie,
    /// else UTC) and the request's culture. Use it to <i>capture</i> a locale, for example onto an order at
    /// checkout; outside a request it is the defaults.
    /// </summary>
    SparkRecipientLocale FromCurrentRequest();

    /// <summary>The locale of a user's account: their stored preference, completed with the defaults.</summary>
    Task<SparkRecipientLocale> ForUserAsync(string? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the current request's locale on the user's account where a part is still unset, and returns the
    /// effective locale. Call it on a request the user made (checkout, profile save); it never overwrites a choice.
    /// </summary>
    Task<SparkRecipientLocale> CaptureForUserAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Where account locale preferences live. Registered by the Authorization package, which owns the user
/// document; an application without accounts has none, and every user resolves to the defaults.
/// </summary>
public interface ISparkRecipientLocaleSource
{
    /// <summary>The user's stored preference, or <see langword="null"/> for an unknown user.</summary>
    Task<SparkRecipientLocalePreference?> GetAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Fills the parts of the user's preference that are unset; a part already chosen is kept.</summary>
    Task SaveIfUnsetAsync(string userId, SparkRecipientLocale locale, CancellationToken cancellationToken);
}
