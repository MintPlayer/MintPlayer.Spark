namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Scopes and switches on Spark's antiforgery (CSRF) gate.
/// <para>
/// Spark's gate historically fired only on endpoints carrying
/// <see cref="Microsoft.AspNetCore.Antiforgery.IAntiforgeryMetadata"/>. Nothing attaches that
/// metadata by default: <c>AddControllers()</c> attaches none, and MVC's own
/// <c>[ValidateAntiForgeryToken]</c> implements a <em>different</em> interface
/// (<c>IAntiforgeryPolicy</c>, from <c>Mvc.ViewFeatures</c>) that this gate never sees. So an app's
/// own cookie-authenticated <c>POST</c> was unprotected, and the obviously-correct MVC annotation
/// did not change that (#300).
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
    /// antiforgery-checked without any per-endpoint annotation. Defaults to <see langword="false"/>.
    /// <para>
    /// Off by default <em>this preview only</em>: turning it on rejects writes from any client that
    /// does not echo the <c>XSRF-TOKEN</c> cookie, and an app upgrading Spark should discover that
    /// from a release note rather than from production 400s. Use <see cref="WarnOnly"/> to find out
    /// what would break, then turn this on. The default becomes <see langword="true"/> at the next
    /// major.
    /// </para>
    /// </summary>
    public bool RequireAntiforgery { get; set; }

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
