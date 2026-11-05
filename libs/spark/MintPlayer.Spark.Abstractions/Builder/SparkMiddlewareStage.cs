namespace MintPlayer.Spark.Abstractions.Builder;

/// <summary>
/// Where in the pipeline a registered middleware runs: two points in <c>UseSpark()</c>, and one in the SPA branch
/// (<see cref="BeforePrerendering"/>).
/// <para>
/// Most module middleware wants to run <em>after</em> Spark has authenticated the caller and handled
/// the request, which is why <see cref="AfterSpark"/> is the default and holds the enum's zero value:
/// <c>default(SparkMiddlewareStage)</c> must mean "where middleware has always run", never a silent
/// relocation.
/// </para>
/// <para>
/// A stage is not a general ordering mechanism. Within one stage, middleware still runs in
/// registration order — this only chooses which side of authentication it lands on.
/// </para>
/// </summary>
public enum SparkMiddlewareStage
{
    /// <summary>
    /// The end of <c>UseSpark()</c> — after <c>UseAuthentication</c>, <c>UseAuthorization</c>,
    /// antiforgery and <c>SparkMiddleware</c> itself. The default, and the right place for anything
    /// that reads the authenticated principal, or that is a one-off startup task rather than
    /// per-request middleware.
    /// </summary>
    AfterSpark = 0,

    /// <summary>
    /// The start of <c>UseSpark()</c> — after the app's own <c>UseRouting()</c>, but before
    /// <c>UseAuthentication</c>.
    /// <para>
    /// For middleware that must reject a request before the cost of authenticating it is paid. A rate
    /// limiter is the motivating case: an app whose flood risk is an authenticated ingest endpoint
    /// costs a database lookup per credential, and a limiter behind authentication only protects the
    /// app from load it has already absorbed the expensive part of.
    /// </para>
    /// <para>
    /// Routing has already run at this point — <c>UseSpark()</c> is documented as "call after
    /// <c>UseRouting()</c>" — so endpoint metadata is available and endpoint-attached policies still
    /// resolve. Do <b>not</b> use this stage for anything needing
    /// <see cref="System.Security.Claims.ClaimsPrincipal"/>: no credential has been validated yet, so
    /// every request looks anonymous here.
    /// </para>
    /// </summary>
    BeforeAuthentication = 1,

    /// <summary>
    /// Inside the SPA branch, just outside the server-side renderer: <c>UseSparkPrerendering()</c> applies it
    /// immediately before <c>UseSpaPrerendering</c>, not <c>UseSpark()</c> (<c>docs/html_minification_PRD.md</c> G3).
    /// <para>
    /// Middleware here wraps the <em>rendered page</em>, not the <c>index.html</c> template the renderer reads
    /// with its own <c>next()</c>. It runs after the static files, which have already ended their requests, and
    /// never sees <c>/spark</c> when the app branches the SPA off it. Response compression, registered at the top
    /// of the pipeline, stays outside it. HTML minification is the motivating case.
    /// </para>
    /// <para>
    /// Only server-rendered apps have this point. Registering for it in an app that never calls
    /// <c>UseSparkPrerendering()</c> fails the host's start, rather than leaving the middleware silently unused.
    /// </para>
    /// </summary>
    BeforePrerendering = 2,
}
