using System.Text.RegularExpressions;
using Microsoft.AspNetCore.ResponseCompression;
using MintPlayer;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Media;
using MintPlayer.Services;
using MintPlayer.Spark;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Boards;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Minification;
using MintPlayer.Spark.OpenSearchDescription;
using MintPlayer.Spark.Search;
using MintPlayer.Spark.Search.OpenSearch;
using MintPlayer.Spark.SitemapXml;
using MintPlayer.Spark.SoftDelete;

var builder = WebApplication.CreateBuilder(args);

// mintplayer.com. Production: Data Protection and forwarded headers come from Spark:DataProtection and
// Spark:ForwardedHeaders in the deployment's environment, never from appsettings.json.
builder.Services.AddSpark(builder.Configuration, spark =>
{
    // The store (#551): RavenDB, configured from Spark:RavenDb.
    spark.UseRavenDb();

    spark.UseContext<MintPlayerContext>();
    spark.AddActions();
    spark.AddMigrations();

    spark.AddAuthentication<SparkUser>(auth =>
    {
        auth.LocalCredentials = SparkLocalCredentials.Full;
        auth.Passkeys = SparkPasskeys.Enabled;
        // localhost in Development, mintplayer.com in production (a passkey is bound to its domain).
        auth.PasskeyServerDomain = builder.Configuration["MintPlayer:PasskeyServerDomain"];
        auth.AuthenticatorIssuer = "MintPlayer";
        // The legacy site let a user skip their code for external sign-ins; the migrated flag maps here.
        auth.ExternalLoginTwoFactor.AllowUserBypass = true;
    }, identity =>
    {
        // As on the legacy site (MintPlayer.Web Startup.cs): any character. 80 of the 776 migrated user names have
        // spaces, parentheses or non-ASCII letters, and Identity validates the name on every update, so the default
        // set refused even a password reset for them. Spark's own validator still refuses '@'.
        identity.User.AllowedUserNameCharacters = string.Empty;
    });
    // Facebook, GitHub, Google, LinkedIn, Microsoft and Twitter from Spark:Auth:Providers; the secrets are in the
    // deployment's .env.
    spark.AddExternalProviders(builder.Configuration);

    spark.AddMessaging();
    // #601: MediaUrlsAddedRecipient resolves the songs' media thumbnails in the background.
    spark.AddRecipients();
    spark.AddMailManager();

    spark.AddSoftDelete();
    spark.AddHistory();
    // #530: the home page is the "home" board (App_Data/boards.json); a signed-in user may rearrange it.
    spark.AddBoards();
    // docs/spark_ssr_PRD.md: every page is server-rendered with its data. Dates on a first visit render in
    // Brussels time (D10). /home is the "home" board, which names its path in boards.json (D15).
    spark.AddPrerendering(prerendering => prerendering.DefaultTimeZone = "Europe/Brussels");
    // #563 (docs/html_minification_PRD.md): the rendered pages are minified, as the legacy site's were meant to be,
    // inside UseResponseCompression and outside the renderer. Spark:Minification:Enabled=false turns it off.
    spark.AddMinification();
    // SeoTester crawl.sitemap: /sitemap.xml lists every public song, artist, person, medium type, tag and public
    // playlist (D17). robots.txt (ClientApp/public) names it.
    spark.AddSitemap();
    // #238 (docs/spark_search_PRD.md): songs, artists and people ("search" in their model files), served at
    // /spark/search and /spark/search/suggest. Production names its OpenSearch engine (docker-compose.yml); without
    // one, and while it is down, the generated Spark_Search RavenDB index answers.
    spark.AddSearch(search =>
    {
        if (builder.Configuration["Search:OpenSearch:Uri"] is { Length: > 0 } engine)
            search.UseOpenSearch(o => o.Uri = new Uri(engine));
    });
    // The browser's "add search engine": /opensearch.xml, linked by withOpenSearchDescription() in app.config.ts.
    spark.AddOpenSearchDescription(o => o.ShortName = "MintPlayer");
});

builder.Services.AddScoped<MintPlayerAccess>();
builder.Services.AddMediaThumbnails();
// Liveness for the compose healthcheck and the deploy: no checks registered, so it depends on nothing that can be
// misconfigured, and a bad secret cannot restart-loop the container.
builder.Services.AddHealthChecks();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.MintPlayer";
});

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

// SeoTester (performance.compression, security.hsts): Brotli or gzip for text, and a year of HSTS. Traefik terminates TLS,
// and Spark's forwarded headers make the request HTTPS here, so both apply to every public response.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["image/svg+xml", "application/manifest+json"]);
});
builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

if (builder.SynchronizeSparkModelsIfRequested(args))
    return;

if (builder.InitializeSparkSecurityIfRequested(args))
    return;

if (builder.VerifySparkSecurityIfRequested(args))
    return;

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.Use((context, next) =>
{
    // SeoTester security.nosniff and security.permissions-policy. The policy denies only what the site never uses: the
    // media players need autoplay, fullscreen and encrypted-media, and passkeys need publickey-credentials-*.
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        return Task.CompletedTask;
    });
    return next(context);
});
app.UseResponseCompression();

if (builder.Configuration.GetValue("Spark:HttpsRedirection", true))
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFiles(new StaticFileOptions
{
    // Angular's hashed bundles (main-QOAEFUKX.js, styles-CCMUWBJD.css, chunk-*.js) never change under their name, so
    // browsers may keep them for a year (SeoTester performance.cache-control). index.html is not hashed and keeps
    // SpaServices' no-cache.
    OnPrepareResponse = context =>
    {
        if (hashedAsset().IsMatch(context.File.Name))
            context.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    },
});

app.UseRouting();
app.UseSpark();

app.UseEndpoints(endpoints =>
{
    endpoints.MapSpark();
    endpoints.MapMintPlayerEndpoints();
    endpoints.MapHealthChecks("/health").AllowAnonymous();
});

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark"),
    appBuilder =>
    {
        appBuilder.UseSpaImproved(spa =>
        {
            spa.Options.SourcePath = "ClientApp";
            // UseSparkPrerendering also places AddMinification()'s middleware, just outside the renderer (#563).
            // In Development the server bundle is a watch build beside the dev server (tools/ssr-watch.mjs), so a
            // change to server-side code shows on the next render; in production it is the published build's.
            spa.UseSparkPrerendering(prerendering => prerendering.BootModuleBuilder = app.Environment.IsDevelopment()
                ? SparkBuilderPrerenderingExtensions.WatchBuild("build:ssr:watch")
                : null);

            if (app.Environment.IsDevelopment())
            {
                spa.UseAngularCliServer(npmScript: "start", cliRegexes: [openBrowserRegex()]);
            }
        });
    });

app.Run();

partial class Program
{
    [GeneratedRegex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]
    private static partial Regex openBrowserRegex();

    // The esbuild output hash: eight base-32 capitals before the extension.
    [GeneratedRegex(@"-[A-Z0-9]{8}\.(js|css|woff2?|ttf|svg|png|jpg|webp)$")]
    private static partial Regex hashedAsset();
}
