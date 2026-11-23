namespace MintPlayer.Spark.Configuration;

/// <summary>
/// The anonymous visitor id (<c>docs/notification_bell_PRD.md</c> G4, G5), bound from <c>Spark:Visitor</c>.
/// </summary>
/// <remarks>
/// <para>
/// A visitor id identifies one anonymous browser that asked for something to be delivered later, such as
/// the result of a long-running action. It is issued only by
/// <see cref="Abstractions.Authentication.ISparkCurrentPrincipal.EnsureVisitorId"/>: a plain visit never gets
/// a cookie. The cookie is strictly necessary for the service the visitor requested, so it needs no consent
/// banner; it holds a random id protected with Data Protection and nothing else.
/// </para>
/// <para>
/// Two cookies: <see cref="CookieName"/> (HttpOnly, the protected id) and <see cref="PresenceCookieName"/>
/// (readable by the page, the value <c>1</c>, no id), which tells the client to follow what is addressed to
/// the visitor (G7). Both are set, renewed and cleared together.
/// </para>
/// </remarks>
public sealed class SparkVisitorOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Spark:Visitor";

    /// <summary>The HttpOnly cookie that carries the protected visitor id.</summary>
    public const string CookieName = "spark.visitor";

    /// <summary>The page-readable companion cookie (value <c>1</c>, no id).</summary>
    public const string PresenceCookieName = "spark.visitor.present";

    /// <summary>The prefix of every visitor id, so a visitor id can never equal a user id.</summary>
    public const string IdPrefix = "visitor:";

    /// <summary>
    /// How long a visitor lives, sliding: each <c>EnsureVisitorId()</c> renews the cookies. Features
    /// expire what they addressed to a visitor after the same span. Default 24 hours.
    /// </summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// <see langword="true"/> makes both cookies browser-session cookies (no <c>Expires</c>): the visitor is
    /// gone when the browser closes. What was addressed to it still expires after <see cref="Lifetime"/>.
    /// </summary>
    public bool SessionCookie { get; set; }
}
