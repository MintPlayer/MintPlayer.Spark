namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Scopes and switches on Spark's antiforgery (CSRF) gate.
/// <para>
/// Spark's gate fires on endpoints carrying
/// <see cref="Microsoft.AspNetCore.Antiforgery.IAntiforgeryMetadata"/>, plus — since 11.0.0 — on any
/// mutating, ambient-credentialed request inside <see cref="PathPrefixes"/>. Nothing attaches that
/// metadata by default: <c>AddControllers()</c> attaches none, and MVC's own
/// <c>[ValidateAntiForgeryToken]</c> does not either, so an app's own cookie-authenticated
/// <c>POST</c> was unprotected and the obviously-correct MVC annotation did not change that (#300).
/// </para>
/// <para>
/// ⚠️ <b>Precisely why <c>[ValidateAntiForgeryToken]</c> does not satisfy this gate</b>, since the
/// loose version of this sentence has been wrong here before. The attribute implements
/// <c>IFilterFactory, IOrderedFilter</c> — it implements neither <c>IAntiforgeryMetadata</c> nor
/// <c>IAntiforgeryPolicy</c>. It is a filter <em>factory</em>; the filter it resolves from DI
/// (<c>ValidateAntiforgeryTokenAuthorizationFilter</c>) is what carries the empty
/// <c>IAntiforgeryPolicy</c> marker, and that marker exists only for MVC's own most-effective-filter
/// resolution. The conclusion is unchanged — routing reads <c>IAntiforgeryMetadata</c> and never
/// sees any of this — but the mechanism is one indirection further than it used to say.
/// </para>
/// <para>
/// ⚠️ <b>New in .NET 11:</b> <c>AntiforgeryApplicationModelProvider</c> bridges the two worlds, so a
/// controller or action carrying <c>[RequireAntiforgeryToken]</c> now also gets MVC's own rejecting
/// filter. That makes <c>[RequireAntiforgeryToken]</c> the supported annotation on controllers, and
/// it means applying <em>both</em> attributes to the same action <b>throws at startup</b> rather
/// than layering.
/// </para>
/// <para>
/// Turning <see cref="RequireAntiforgery"/> on inverts the default <em>inside a path scope the app
/// names</em>: within it, a mutating request carrying an ambient credential is checked unless an
/// endpoint explicitly says otherwise. Explicit metadata still wins in both directions, so
/// <c>DisableAntiforgery()</c> remains the escape hatch.
/// </para>
/// <para>
/// Inverting the default rather than stamping metadata is deliberate. Metadata can only be attached
/// by something that knows the endpoint exists — an MVC convention reaches controllers and nothing
/// else — so a stamping design would cover controllers and leave the app's own
/// <c>MapPost</c> silently open, which is the shape of the defect rather than its fix.
/// </para>
/// </summary>
public class SparkAntiforgeryOptions
{
    /// <summary>
    /// Whether a mutating request with an ambient credential inside <see cref="PathPrefixes"/> is
    /// antiforgery-checked without any per-endpoint annotation. Defaults to <see langword="true"/>.
    /// <para>
    /// ⚠️ <b>This default flipped in 11.0.0.</b> It was <see langword="false"/> through the 10.x
    /// line, where the surrounding documentation promised "the default becomes <c>true</c> at the
    /// next major". Packages moved to <c>11.0.0-preview.*</c> when the solution retargeted
    /// <c>net11.0</c>, so this is that major and this is that flip.
    /// </para>
    /// <para>
    /// While it was <see langword="false"/> the inverted default protected <em>nothing</em>: no
    /// application ever assigned it, the middleware's null-metadata branch is guarded on it, and
    /// <see cref="WarnOnly"/> lives inside that same branch — so the migration aid logged nothing
    /// either, and an app waiting for a clean log before turning this on would have waited forever.
    /// Endpoints carrying explicit <c>RequireAntiforgeryTokenAttribute</c> were, and remain,
    /// enforced regardless of this flag; what was unprotected is everything an application maps
    /// itself.
    /// </para>
    /// <para>
    /// An app that needs the old behaviour back sets this to <see langword="false"/> explicitly, or
    /// uses <see cref="WarnOnly"/> to see its affected surface in one deploy first.
    /// </para>
    /// </summary>
    public bool RequireAntiforgery { get; set; } = true;

    /// <summary>
    /// Log what <em>would</em> have been rejected and let it through, instead of rejecting it. The
    /// migration path onto <see cref="RequireAntiforgery"/>: an app can see its whole affected
    /// surface in one deploy rather than one 400 at a time. Ignored when
    /// <see cref="RequireAntiforgery"/> is off.
    /// </summary>
    public bool WarnOnly { get; set; }

    /// <summary>
    /// The path prefixes the inverted default applies to. Requests outside them keep the old
    /// metadata-only behaviour, so an app can adopt this one area at a time.
    /// <para>
    /// Defaults to Spark's own surfaces — <c>/spark</c> and <c>/connect</c> — which already carry
    /// explicit metadata, so the default configuration changes nothing. An app protecting its own
    /// controllers names them here: <c>["/spark", "/connect", "/api"]</c>.
    /// </para>
    /// <para>
    /// Assigning <em>replaces</em> the defaults. Prefixes match whole path segments, so <c>/api</c>
    /// covers <c>/api/tokens</c> but not <c>/apidocs</c>; leading and trailing slashes are
    /// normalized.
    /// </para>
    /// </summary>
    public string[] PathPrefixes { get; set; } = ["/spark", "/connect"];
}
