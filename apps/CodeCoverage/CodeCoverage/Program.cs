using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using CodeCoverage;
using CodeCoverage.ApiTokens;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using MintPlayer.AspNetCore.SpaServices.Extensions;
using MintPlayer.Spark;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Controllers;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using CodeCoverage.Feedback;
using CodeCoverage.Ingestion;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using MintPlayer.Spark.Messaging;
using MintPlayer.Spark.Webhooks.GitHub.Extensions;

var builder = WebApplication.CreateBuilder(args);

var envPrefix = builder.Environment.EnvironmentName;

// The --spark-* commands are build steps, not run modes: they reflect over the
// entity classes and security.json, open no database, and return before Build().
// They must therefore keep working where no secrets exist — CI, and a fork's PR
// run — which is why the credential check below exempts them rather than being
// unconditional. Nothing they touch can reach an authentication handler.
var isSparkBuildCommand = args.Any(a => a.StartsWith("--spark-", StringComparison.Ordinal));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
// Bounded, and separate from the shared cache on purpose — see SourceContentCache.
builder.Services.AddSingleton<CodeCoverage.Services.ISourceContentCache, CodeCoverage.Services.SourceContentCache>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddCodeCoverage();
builder.Services.AddSpark(builder.Configuration, spark =>
{
    spark.UseContext<CoverageSparkContext>();

    // security.json grants QueryRead on the four entity types to BOTH well-known
    // roles — 'anonymous' is not 'everyone', so a right both should have is two
    // grants. The Actions classes (Coverage/Actions) are the only gate behind
    // that: row filters scope reads per viewer (public repos for anonymous,
    // GitHub-granted owners for signed-in users) and redact BadgeToken/
    // InstallationId for non-managers. Writes stay denied at the type level
    // (no Edit/New/Delete right exists), so the generic UI is read-only.
    // The /api controllers remain the primary read surface for the vanity pages.
    spark.AddActions();

    // The six /api controllers mount through Spark rather than through a bare
    // MapControllers(): they then run at the pipeline stage Spark chose, behind
    // Spark's antiforgery gate, and [SparkAuthorize] checks the *same*
    // security.json right the persistent-object endpoints check — so a controller
    // and its generic-UI equivalent provably agree instead of agreeing by
    // convention. builder.Services.AddControllers() above still applies: MVC's
    // registration is idempotent and the JsonStringEnumConverter survives.
    spark.AddControllers();
    spark.UseControllers();

    // WarnOnly first: the CI uploader and the badge endpoints are non-browser
    // callers that carry no antiforgery token, and turning the gate on hard would
    // break them silently at deploy rather than loudly here. The credential
    // schemes (covt_, GitHubOidc) are non-ambient and so already exempt; this
    // logs what a strict gate would have rejected, and the flag flips once the
    // logs are clean. /connect is named for the same reason it is named in the
    // rate limiter: the app has no Identity endpoints, but the omission should
    // not become a surprise if one is ever added.
    spark.AddAntiforgeryProtection(antiforgery =>
    {
        antiforgery.PathPrefixes = ["/spark", "/connect", "/api"];
        antiforgery.WarnOnly = true;
    });

    spark.AddAuthentication<SparkUser>(configureProviders: identity =>
    {
        // Fail loud (D5). GitHub is the only way into this app: LocalCredentials
        // defaults to Disabled since preview.58, so an unregistered provider means
        // nobody can sign in at all. Spark's own guard already throws for that, but
        // it can only say "register a provider" — naming the missing key here turns
        // a fresh clone's first run into a one-line fix. The cost is deliberate:
        // boot-without-credentials is gone, so fork PRs and clean clones must
        // configure user-secrets before `dotnet run` (see README, local setup).
        var gitHubClientId = builder.Configuration[$"GitHub:{envPrefix}:ClientId"];
        if (string.IsNullOrEmpty(gitHubClientId))
        {
            // A build command never serves a request, so no provider is needed and
            // none is registered. Spark's own unreachable-sign-in guard runs at
            // endpoint mapping, which these commands return before reaching.
            if (isSparkBuildCommand)
                return;

            throw new InvalidOperationException(
                $"GitHub sign-in is not configured: 'GitHub:{envPrefix}:ClientId' is missing. "
                + "It is the only authentication provider this app registers, so without it no "
                + $"user could sign in. Set it (and 'GitHub:{envPrefix}:ClientSecret') via "
                + "user-secrets for local development, or environment variables in production.");
        }

        identity.AddGitHub(options =>
        {
            options.ClientId = gitHubClientId;
            options.ClientSecret = builder.Configuration[$"GitHub:{envPrefix}:ClientSecret"] ?? string.Empty;
            options.SaveTokens = true;
            // GitHub can hit the callback with a code but no OAuth state —
            // notably the App's "Request user authorization during
            // installation" flow, which our server never initiated. Without
            // this the handler throws and the user gets a 500 instead of
            // the app; the real sign-in path is unaffected (it always has
            // state). Sign-in itself stays available via the shell button.
            options.Events.OnRemoteFailure = context =>
            {
                context.Response.Redirect("/home");
                context.HandleResponse();
                return Task.CompletedTask;
            };
        });
    });
    // Registered as a Spark credential scheme (non-ambient): the composite
    // default-authenticate scheme tries it, which both silences the
    // "refused by every registered scheme" warning on CI uploads and earns
    // the non-ambient antiforgery exemption. The handler returns NoResult
    // for anything that isn't a covt_ value, so this widens nothing.
    // GitHubOidc is deliberately NOT a credential scheme — workflow JWTs
    // stay valid only on endpoints that name the scheme explicitly.
    spark.AddCredentialScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
        ApiTokenAuthenticationHandler.SchemeName);

    // Meters /spark (Spark's generic query API — a second anonymous read surface
    // over the same documents as /api/browse) and /api/browse itself, one bucket
    // per client IP. Registered at the BeforeAuthentication stage since
    // preview.52, so a flood is rejected before a covt_ token lookup is paid for
    // — which is why this replaced a hand-rolled GlobalLimiter rather than
    // sitting alongside one. Assigning PathPrefixes replaces the defaults, hence
    // /connect is listed even though this app has no Identity endpoints: naming
    // it costs nothing and stops the omission becoming a surprise if one is ever
    // added. Named policies below still apply per endpoint on top of this.
    spark.AddRateLimiter(rateLimiter =>
        rateLimiter.PathPrefixes = ["/spark", "/connect", "/api/browse"]);

    // Five lanes, one RavenDB subscription. Before partitioned lanes this app declared five queues
    // plus the framework's, against a licence that allows three subscriptions per database — so three
    // were silently never created. That is why the pull-request comment never appeared on `opened`,
    // and why coverage-delete-pr-builds had never run at all.
    spark.AddMessaging(configure: null, messaging: messaging => messaging.AddLane(lanes =>
    {
        // Ordering here is load-bearing and is scoped to a BUILD, not to the lane: finalize must not
        // overtake the parses of its own build, or it closes on a half-computed number and publishes
        // a wrong percentage. Parses of different builds have no relationship, so they run in
        // parallel — which queue-wide FIFO could not express.
        //
        // AssembleCommitMessage keys on the commit instead, because its requirement is mutual
        // exclusion per commit rather than ordering against parses: it is only ever broadcast after
        // the finalize it follows has completed.
        lanes.Queue<ParseSessionMessage>()
            .Ordered()
            .PartitionBy<ParseSessionMessage>(m => m.BuildId)
            .PartitionBy<FinalizeBuildMessage>(m => m.BuildId)
            .PartitionBy<AssembleCommitMessage>(m => m.CommitId)
            // Two, not four: a parse holds the raw gzip, a decompressed copy and the UTF-16 string at
            // once — roughly four times the report size, on the large-object heap — and RavenDB is
            // co-resident on the same host with no memory limit declared for this container.
            .MaxPartitionsInFlight(2);

        // Each of these calls GitHub for one build, and one build's failure has nothing to do with
        // another's, so they are concurrent. Under queue-wide FIFO they needed a separate queue each
        // to get that; now it is what the lane says.
        lanes.Queue<PublishFeedbackMessage>().Concurrent(maxConcurrency: 4);
        lanes.Queue<PublishPullRequestCommentMessage>().Concurrent(maxConcurrency: 4);
        lanes.Queue<OpenPullRequestCommentMessage>().Concurrent(maxConcurrency: 4);

        // Retention deletion. Ordered per pull request so two events for one PR cannot interleave,
        // and unrelated PRs are deleted in parallel.
        lanes.Queue<DeletePullRequestBuildsMessage>()
            .Ordered()
            .PartitionBy<DeletePullRequestBuildsMessage>(m => $"{m.RepositoryGitHubId}/{m.PullRequestNumber}")
            .MaxPartitionsInFlight(4);

        // The framework's webhook lane. Ordered per repository: a push and a pull_request event for
        // one repository both write shared commit state, while two repositories share nothing.
        lanes.Queue<GitHubWebhookMessage>()
            .Ordered()
            .PartitionBy<GitHubWebhookMessage>(m => m.RepositoryFullName)
            .MaxPartitionsInFlight(8);
    }));
    spark.AddRecipients();
    spark.AddCronJobs();
    // Pending ISparkMigration classes run inside UseSpark(), after indexes are
    // created and before the app serves — once per database, in Version order,
    // under a cluster-wide lock. Committed and replayed automatically, so a
    // restored backup or a fresh environment can't miss one the way a hand-run
    // patch in Raven Studio can.
    spark.AddMigrations();
    spark.AddGithubWebhooks(options =>
    {
        options.WebhookSecret = builder.Configuration["GitHub:WebhookSecret"] ?? string.Empty;
        options.ClientId = builder.Configuration[$"GitHub:{envPrefix}:ClientId"];
        options.PrivateKeyPath = builder.Configuration[$"GitHub:{envPrefix}:PrivateKeyPath"];

        // ProductionAppId = "the App whose webhooks THIS instance processes"
        // — locally that's the dev App. DevelopmentAppId means something else
        // entirely: "forward that App's webhooks to connected dev clients
        // instead of processing them", a production-side setting. Setting it
        // on a local machine makes the processor silently skip every
        // recipient (Spark webhooks README warns exactly this).
        if (long.TryParse(builder.Configuration[$"GitHub:{envPrefix}:AppId"], out var appId))
            options.ProductionAppId = appId;

        if (!builder.Environment.IsDevelopment()
            && long.TryParse(builder.Configuration["GitHub:Development:AppId"], out var devAppId))
            options.DevelopmentAppId = devAppId;

        // Deliberately NOT options.AddSmeeDevTunnel(smeeUrl): re-minifying the
        // smee-relayed body is necessary (GitHub signs minified bytes), but
        // Spark's tunnel does it via a Newtonsoft round-trip that rewrites
        // fractional-second timestamps — so every installation event fails
        // signature validation. Our lexically-minifying replacement is
        // registered below; upstream fix tracked in docs/spark-handoff.md.
    });
});

// Key ring in RavenDB instead of the container filesystem, where a redeploy
// destroyed it and signed everyone out (auth + antiforgery cookies both
// decrypt with these keys). Configured through options so the IDocumentStore
// that AddSpark registers is resolved lazily, not at registration time.
builder.Services.AddDataProtection().SetApplicationName("CodeCoverage");
builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
    .Configure<Raven.Client.Documents.IDocumentStore>((options, store) =>
        options.XmlRepository = new CodeCoverage.Services.RavenDataProtectionKeyRepository(store));

if (!string.IsNullOrEmpty(builder.Configuration["GitHub:SmeeChannelUrl"]))
{
    builder.Services.AddHostedService<CodeCoverage.Services.SmeeWebhookTunnelService>();
}

// GitHubOidc: GitHub-signed workflow JWTs, validated against GitHub's JWKS;
// the audience must be this deployment's public base URL and the action must
// request exactly that audience. (ApiToken is registered inside AddSpark as a
// credential scheme — see above.)
builder.Services.AddAuthentication()
    .AddJwtBearer(GitHubOidc.SchemeName, options =>
    {
        options.Authority = GitHubOidc.Issuer;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidIssuer = GitHubOidc.Issuer,
            ValidAudience = builder.Configuration["Coverage:BaseUrl"] ?? "https://localhost:5200",
            ValidateLifetime = true,
        };
    });

// Ingest endpoints are partitioned per token (falling back to client IP).
// The limiter middleware runs BEFORE authentication, so context.User is always
// anonymous here — the partition key must come from the presented credential
// itself, not claims. That ordering is the framework's since preview.52; before
// it, this app hand-rolled the limiter specifically to keep it.
static string UploadsPartitionKey(HttpContext context)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        authorization = authorization[7..];
    else if (authorization.StartsWith("Token ", StringComparison.OrdinalIgnoreCase))
        authorization = authorization[6..];
    if (authorization.StartsWith("covt_", StringComparison.Ordinal))
        return ApiTokenService.Hash(authorization);
    return context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Browsing is anonymous for public repositories, and GetFile costs a GitHub
    // fetch per uncached path against the installation's shared rate limit — so
    // an unmetered crawler spends a budget every tenant depends on. Roomy enough
    // that the SPA never notices: a page view is a handful of requests.
    options.AddPolicy("browse", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.AddPolicy("uploads", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: UploadsPartitionKey(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    // Polling the status endpoint is metered separately from uploading to it.
    // Same partition — a CI caller gets its own bucket keyed on its token, never
    // collateral damage from a crawler on a shared IP — but a much higher limit,
    // because the two are nothing alike: `uploads` is sized for 50 MB payloads,
    // while a gate waiting on a build spends 12 requests/minute per waiting job
    // and a workflow may wait in several. Sharing one bucket would throttle the
    // poll and starve the uploads it is waiting for.
    options.AddPolicy("uploads-status", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: UploadsPartitionKey(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    // Badges get their own, roomier policy: GitHub's camo proxy funnels every
    // README render through a handful of IPs, so sharing the uploads policy
    // would let one popular badge throttle them all.
    options.AddPolicy("badges", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 600,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

// Model synchronization is a build step, not a run mode: it reflects over the entity classes to
// write App_Data/Model/*.json and modelHashes.json, needs no database, and so runs here and
// returns before Build(). --spark-verify-model is the same call in read-only mode (exit 3 on
// drift), which is what lets CI gate the model without a RavenDB.
if (builder.SynchronizeSparkModelsIfRequested(args))
    return;

// Same shape, over security.json instead: --spark-synchronize-security writes
// App_Data/securityPosture.txt, --spark-verify-security exits 3 when the anonymous
// surface moved. Computed from configuration alone, so CI gates it without a
// RavenDB — the point being that widening security.json is a one-line diff that
// reads no differently from narrowing it.
if (builder.VerifySparkSecurityIfRequested(args))
    return;

var app = builder.Build();

app.UseForwardedHeaders();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
// No app.UseRateLimiter() here: spark.AddRateLimiter() registers it through the
// builder registry, at the BeforeAuthentication stage. Calling it here as well
// would put two RateLimitingMiddleware instances in the pipeline — ASP.NET Core
// has no idempotence marker on either — so every request would take two leases
// from the same partition and silently get half its configured budget.
app.UseSpark();

app.UseEndpoints(endpoints =>
{
    // No endpoints.MapControllers() here: spark.UseControllers() mounts them
    // through MapSpark() instead (SPARK010). Mapping both would be idempotent
    // on Spark's side but would put the controllers back at this stage.
    endpoints.MapSpark();
    endpoints.MapGet("/health", () => Results.Ok());
    // Readiness that can actually fail (#13 U1 / roadmap T0.4): 503 only when
    // the GitHub App key is decisively unusable. The compose healthcheck keeps
    // probing /health (a bad key must not restart-loop the container); the
    // deploy workflow polls this and fails the deploy instead.
    endpoints.MapGet("/health/ready", async (IGitHubAppReadinessService readiness, CancellationToken cancellationToken) =>
    {
        var gitHubApp = await readiness.CheckAsync(cancellationToken);
        var payload = new { status = gitHubApp.Status == GitHubAppReadiness.Failed ? "unready" : "ready", gitHubApp };
        return gitHubApp.Status == GitHubAppReadiness.Failed
            ? Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Json(payload);
    });
});

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark")
        && !context.Request.Path.StartsWithSegments("/api")
        && !context.Request.Path.StartsWithSegments("/badge"),
    appBuilder =>
    {
        appBuilder.UseSpaImproved(spa =>
        {
            spa.Options.SourcePath = "ClientApp";

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
}
