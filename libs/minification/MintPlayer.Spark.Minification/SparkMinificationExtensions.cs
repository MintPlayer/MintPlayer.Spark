using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using WebMarkupMin.AspNetCoreLatest;
using WebMarkupMin.Core;

namespace MintPlayer.Spark.Minification;

/// <summary>
/// <c>spark.AddMinification()</c> (<c>docs/html_minification_PRD.md</c>): minifies the server-rendered pages.
/// <code>
/// builder.Services.AddSpark(builder.Configuration, spark => spark
///     .AddPrerendering()
///     .AddMinification(o => o.ExcludedPaths.Add("/legal")));
/// </code>
/// <para>
/// Server-rendered apps only. The middleware runs at <see cref="SparkMiddlewareStage.BeforePrerendering"/>, which
/// <c>spa.UseSparkPrerendering()</c> applies just outside the renderer. It therefore wraps the rendered page, not the
/// <c>index.html</c> template the renderer reads, and stays inside the app's <c>UseResponseCompression()</c>. An app
/// that never calls <c>UseSparkPrerendering()</c> fails at startup instead of silently minifying nothing.
/// </para>
/// </summary>
public static class SparkMinificationExtensions
{
    /// <summary>Minifies every server-rendered page. Calling it again only adds <paramref name="configure"/>.</summary>
    public static ISparkBuilder AddMinification(this ISparkBuilder builder, Action<SparkMinificationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Services.AddOptions<SparkMinificationOptions>();
        if (configure is not null)
            options.Configure(configure);

        if (builder.Services.Any(d => d.ServiceType == typeof(SparkHtmlMinification)))
            return builder;

        // Configuration after all of the code's configure calls, so Spark:Minification:Enabled=false switches a
        // deployment off.
        if (builder.Configuration is { } configuration)
            options.PostConfigure(o => configuration.GetSection(SparkMinificationOptions.SectionName).Bind(o));

        // WebMarkupMin logs a page it could not minify (and leaves it as it was) through its own logger interface, which
        // defaults to a null logger. Before AddWebMarkupMin, whose registration is a TryAdd.
        builder.Services.TryAddSingleton<WebMarkupMin.Core.Loggers.ILogger, AspNetCoreLogger>();
        builder.Services
            .AddWebMarkupMin(SparkHtmlMinification.ConfigureWebMarkupMin)
            .AddHtmlMinification();
        builder.Services.AddOptions<HtmlMinificationOptions>()
            .Configure<IOptions<SparkMinificationOptions>>((html, spark) => SparkHtmlMinification.ConfigureHtml(html, spark.Value));

        builder.Services.AddSingleton<SparkHtmlMinification>();
        builder.Registry.AddMiddleware(
            app => app.ApplicationServices.GetRequiredService<SparkHtmlMinification>().Use(app),
            SparkMiddlewareStage.BeforePrerendering);
        return builder;
    }
}

/// <summary>
/// The WebMarkupMin wiring behind <see cref="SparkMinificationExtensions.AddMinification"/>. Its settings are fixed
/// here and not exposed (PRD G4, G5): only <see cref="SparkMinificationOptions"/> is public.
/// </summary>
internal sealed class SparkHtmlMinification(IOptions<SparkMinificationOptions> options, ILogger<SparkHtmlMinification> logger)
{
    /// <summary>
    /// Never minified: the development server's paths (the ones <c>UseSparkPrerendering</c> excludes from rendering), and
    /// the template files, whose bytes the Angular service worker checks against the hashes in <c>ngsw.json</c>.
    /// </summary>
    internal static readonly string[] AlwaysExcluded = ["/sockjs-node", "/ng-cli-ws", "/@vite", "/@fs", "/index.html", "/index.csr.html"];

    /// <summary>
    /// The statuses a rendered page can have (PRD §2.4: the slug 301 carries no page). WebMarkupMin's default is 200 only,
    /// which would leave a missing object's page as it was.
    /// </summary>
    internal static readonly int[] StatusCodes = [200, 401, 403, 404];

    internal static void ConfigureWebMarkupMin(WebMarkupMinOptions webMarkupMin)
    {
        webMarkupMin.AllowMinificationInDevelopmentEnvironment = true; // D4: a hydration break shows before production
        webMarkupMin.DisableCompression = true; // D2: UseResponseCompression is the only compression
        webMarkupMin.AllowCompressionInDevelopmentEnvironment = false;
        webMarkupMin.DisablePoweredByHttpHeaders = true;
    }

    internal static void ConfigureHtml(HtmlMinificationOptions html, SparkMinificationOptions spark)
    {
        html.MinificationSettings = Settings(spark);
        html.SupportedHttpStatusCodes = new HashSet<int>(StatusCodes);
        html.SupportedHttpMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GET" };
    }

    /// <summary>
    /// WebMarkupMin's defaults, less everything that could change what Angular hydrates or what a crawler reads, and
    /// everything that saved nothing in SP1. The inline styles stay minified: that is where the saving is.
    /// </summary>
    internal static HtmlMinificationSettings Settings(SparkMinificationOptions spark) => new()
    {
        WhitespaceMinificationMode = spark.Whitespace switch
        {
            SparkWhitespaceMinification.Safe => WhitespaceMinificationMode.Safe,
            SparkWhitespaceMinification.Medium => WhitespaceMinificationMode.Medium,
            SparkWhitespaceMinification.Aggressive => WhitespaceMinificationMode.Aggressive,
            _ => throw new ArgumentOutOfRangeException(nameof(spark), spark.Whitespace, "Unknown whitespace mode."),
        },
        RemoveHtmlComments = spark.RemoveComments,
        AttributeQuotesRemovalMode = HtmlAttributeQuotesRemovalMode.KeepQuotes,
        RemoveOptionalEndTags = false,
        RemoveRedundantAttributes = false,
        RemoveEmptyAttributes = false,
        RemoveHttpProtocolFromAttributes = false,
        RemoveHttpsProtocolFromAttributes = false,
        MinifyEmbeddedJsonData = false,
        MinifyInlineJsCode = false,
        MinifyEmbeddedJsCode = false,
        MinifyEmbeddedCssCode = true,
        MinifyInlineCssCode = true,
    };

    /// <summary>Adds the middleware, unless <see cref="SparkMinificationOptions.Enabled"/> is off.</summary>
    internal void Use(IApplicationBuilder app)
    {
        var spark = options.Value;
        if (!spark.Enabled)
        {
            logger.LogInformation("HTML minification is off ({Section}:Enabled is false); pages are served as rendered.", SparkMinificationOptions.SectionName);
            return;
        }

        var excluded = AlwaysExcluded.Concat(spark.ExcludedPaths).Select(path => new PathString(path)).ToArray();
        app.UseWhen(context => Applies(context.Request, context.WebSockets.IsWebSocketRequest, excluded), branch => branch.UseWebMarkupMin());
    }

    /// <summary>Whether a request's page may be minified. WebMarkupMin then decides on status and media type.</summary>
    internal static bool Applies(HttpRequest request, bool isWebSocket, IReadOnlyList<PathString> excluded)
        => HttpMethods.IsGet(request.Method)
            && !isWebSocket
            && !excluded.Any(path => request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase));
}
