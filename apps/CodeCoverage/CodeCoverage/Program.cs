using CodeCoverage.GithubIntegration.Extensions;
using System.Net;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Authorization.Configuration;
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
using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Extensions;
using MintPlayer.Spark.Authorization.Identity;
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

    // ⚠️ These lists used to be CLEARED, which does not mean "no proxies" — it disables peer
    // validation entirely, so ASP.NET Core took `X-Forwarded-For` from whoever sent it. Any
    // anonymous caller could therefore choose the address every rate limiter partitions on and
    // every log line records, which is a bypass of the fork-upload limiter rather than a
    // theoretical one.
    //
    // The proxy's address cannot be pinned (Docker assigns it), but it does not have to be. See
    // docker-compose.yml: `coverage-app` publishes no host ports and is reachable only from the
    // `web` network, where Traefik is the sole ingress. So the transport peer is ALWAYS a
    // container address on a Docker bridge network, and trusting the private ranges is exactly
    // as tight as naming the container would be — nothing on a public address can reach this
    // process to be trusted in the first place.
    //
    // ⚠️ If this app is ever exposed directly, or put behind a proxy that is not on a private
    // network, this must become an explicit KnownProxies entry. The safety argument is the
    // topology, not the address family.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("127.0.0.0"), 8));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("::1"), 128));
    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("fc00::"), 7));

    // One hop, which is what makes a spoofed header harmless rather than merely validated.
    // Traefik APPENDS the real peer to whatever the client sent, so with a limit of one the
    // rightmost entry — the only one Traefik wrote — is the one taken, and the attacker-supplied
    // entries to its left are never read. It is the default; it is spelled out because the whole
    // argument above collapses without it.
    options.ForwardLimit = 1;
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
// Outgoing mail. Bound before AddSpark so the conditional registration below can read it.
builder.Services.Configure<CoverageMailOptions>(builder.Configuration.GetSection("Coverage:Mail"));
var mailOptions = builder.Configuration.GetSection("Coverage:Mail").Get<CoverageMailOptions>()
    ?? new CoverageMailOptions();

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

    // ⚠️ THIS BLOCK CURRENTLY DOES NOTHING, and the comment that used to live here said otherwise.
    //
    // WarnOnly is documented as "ignored when RequireAntiforgery is off", and RequireAntiforgery is
    // never set — not here, not anywhere outside the tests. So the path-prefix gate protects no
    // endpoint, and it logs nothing either: the warning lives inside a branch that only runs once
    // RequireAntiforgery is on. Anyone waiting for "the logs are clean" before flipping the flag
    // would wait forever, because the logs cannot be anything else.
    //
    // Endpoints that matter are therefore protected explicitly instead, by RequireAntiforgeryToken
    // metadata, which the middleware enforces regardless of this configuration.
    //
    // ⚠️ Before turning RequireAntiforgery on, settle the GitHubOidc question. The previous comment
    // claimed "the credential schemes (covt_, GitHubOidc) are non-ambient and so already exempt",
    // which is false and contradicted a few hundred lines below: only ApiToken is registered as a
    // credential scheme, and GitHubOidc is "deliberately NOT" one. A request authenticated by a
    // workflow JWT records no scheme feature, and HasAmbientCredential treats "nothing recorded" as
    // ambient — so flipping the flag would very likely start rejecting CI uploads. That is exactly
    // the breakage WarnOnly was meant to surface, and exactly what it never surfaced.
    //
    // /connect is named for the same reason it is named in the rate limiter: the app has no Identity
    // endpoints, but the omission should not become a surprise if one is ever added.
    spark.AddAntiforgeryProtection(antiforgery =>
    {
        antiforgery.PathPrefixes = ["/spark", "/connect", "/api"];
        antiforgery.WarnOnly = true;
    });

    spark.AddAuthentication<SparkUser>(configure: auth =>
    {
        // Disabled | WhenSignedIn | ConfirmByEmail, from Spark:Auth:ExternalLoginLinking.
        // Left at Spark's default until there is a second forge worth linking to — GitHub
        // is currently the only way in, so the situation the modes exist for cannot arise.
        //
        // ⚠️ ConfirmByEmail is refused at startup unless a link-confirmation sender is
        // registered, which is conditional on the mail settings below. That pairing is
        // deliberate: a confirmation nobody sends is a link nobody makes, and the symptom
        // is a sign-in that appears to do nothing.
        if (Enum.TryParse<SparkExternalLoginLinking>(
                builder.Configuration["Spark:Auth:ExternalLoginLinking"], ignoreCase: true,
                out var linking))
        {
            auth.ExternalLoginLinking = linking;
        }

        // Passkeys are on, and deliberately not tied to LocalCredentials — which stays Disabled.
        // A passkey is a passwordless credential, so "this app has no passwords" is the reason to
        // want one, not a reason to withhold it. It also gives the app its first forge-independent
        // way in: today a user who loses their GitHub account loses this one too.
        //
        // Enrollment still needs an existing session, so this adds no new way to *reach* an account
        // that GitHub did not already open.
        auth.Passkeys = SparkPasskeys.Enabled;

        // ⚠️ Pinned rather than derived from the request Host. A passkey binds to its relying-party
        // id for life and there is no migration: if a proxy ever presented a different host, every
        // credential enrolled under it would be permanently unusable, and so would the correct ones
        // once the value moved back. Coverage:BaseUrl is the same value the GitHubOidc audience
        // already trusts, so the two cannot drift apart.
        if (Uri.TryCreate(builder.Configuration["Coverage:BaseUrl"], UriKind.Absolute, out var baseUrl))
        {
            auth.PasskeyServerDomain = baseUrl.Host;
        }
    },
    configureProviders: identity =>
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

    spark.AddMessaging();
    spark.AddCustomActions();
    spark.AddRecipients();
    spark.AddCronJobs();

    // ⚠ The four calls above are source-generated over THIS assembly only, so they do not see
    // anything in CodeCoverage.GithubIntegration. Its own entry point has to be called explicitly,
    // or every GitHub service and recipient silently fails to register - the app would still start
    // and still serve pages, it would just stop publishing pull-request comments and handling
    // webhooks. RegistrationInventoryTests is what stops that shipping unnoticed.
    //
    // One line per forge. When GitLab and Bitbucket gain implementations, they are added here
    // beside this, and nothing else in the composition root changes.
    spark.AddGithubIntegration();
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

        // Dev only: relay a smee.io channel into the local webhook processor.
        // The tunnel hands the processor the exact bytes GitHub signed, so
        // signature validation applies here as it does to a direct delivery.
        var smeeChannelUrl = builder.Configuration["GitHub:SmeeChannelUrl"];
        if (!string.IsNullOrEmpty(smeeChannelUrl))
            options.AddSmeeDevTunnel(smeeChannelUrl);
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

// GitHubOidc: GitHub-signed workflow JWTs, validated against GitHub's JWKS;
// the audience must be this deployment's public base URL and the action must
// request exactly that audience. (ApiToken is registered inside AddSpark as a
// credential scheme — see above.)
// ⚠️ Registered only when there is somewhere to send. Spark ships the contract and no
// transport on purpose, so an unregistered sender is how "this deployment cannot send mail"
// is expressed — and it is what makes the ConfirmByEmail startup guard a plain null check
// rather than a guess about whether some default is a real transport.
if (mailOptions.IsConfigured)
{
    builder.Services.AddScoped<ISparkLinkConfirmationSender<SparkUser>, SmtpLinkConfirmationSender>();
}

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
/// <summary>
/// Partition key for anonymous fork uploads: the TARGET repository, not the caller.
/// </summary>
/// <remarks>
/// <para>
/// The caller is anonymous by construction, so there is no credential to key on, and an IP is
/// nearly free to change. Keying on the target bounds the two things that actually cost us — stored
/// documents and forge API calls — per repository, which is the unit a repository owner consented
/// to and the unit an abuser has to multiply to do damage.
/// </para>
/// <para>
/// ⚠️ The trade is deliberate and worth stating: one abusive fork can exhaust a repository's own
/// window and delay a legitimate contributor's coverage for the rest of the minute. That is a
/// recoverable annoyance on a best-effort courtesy feature; the alternative — keying on the IP —
/// lets one actor spend every repository's budget at once, which is not.
/// </para>
/// <para>
/// Read from the PATH rather than from route values, because this runs at the
/// BeforeAuthentication stage and must not depend on endpoint selection having happened. The shape
/// is <c>/api/uploads/fork/{provider}/{owner}/{name}/pull/{number}</c>; anything that does not
/// match falls back to one shared bucket, which is the restrictive answer.
/// </para>
/// </remarks>
static string ForkUploadsPartitionKey(HttpContext context)
{
    var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
    // api / uploads / fork / provider / owner / name / ...
    var repository = segments is { Length: >= 6 }
        ? string.Join('/', segments[3], segments[4], segments[5]).ToLowerInvariant()
        : "unattributed";

    // ⚠️ The CALLER is part of the key, not just the target. Keyed on the repository alone, the
    // partition is a string the caller chooses: varying the name segment yields a fresh window per
    // request, so there was no aggregate bound on an anonymous caller at all — and each request
    // still reached RepositoryResolver, which spends a forge API call on any unknown name under a
    // known owner. That is the installation's shared budget, which the reconciler, the comment
    // publisher and every badge depend on.
    //
    // Keeping the repository in the key is what stops one caller spending every repository's
    // allowance at once; adding the caller is what stops them having an unlimited number of
    // allowances.
    //
    // ⚠️⚠️ THIS ONLY WORKS BECAUSE THE FORWARDED-HEADER OPTIONS ARE NOW PINNED. While
    // `KnownProxies` and `KnownNetworks` were cleared, `RemoteIpAddress` was whatever the caller
    // put in `X-Forwarded-For` — so keying on it would have been exactly as caller-chosen as
    // keying on the path: one header per request, one fresh window per request, the same abuse
    // through a different string. See the ForwardedHeadersOptions at the top of this file; if the
    // trust list is ever emptied again, this stops being a control and silently reads as one.
    return $"{context.Connection.RemoteIpAddress?.ToString() ?? "anonymous"}|{repository}";
}

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
    // Anonymous fork uploads. Deliberately the tightest window in the app: the caller holds no
    // credential, every accepted request stores documents and each one costs a forge round trip to
    // read the pull request. A real fork pull request uploads a handful of times per run — once per
    // job — so 10/min per (caller, repository) is generous for the honest case and cheap to survive
    // otherwise. See ForkUploadsPartitionKey for why the key has both halves.
    options.AddPolicy("fork-uploads", context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ForkUploadsPartitionKey(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
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

// Before anything can load a FileCoverage — including the migration runner
// inside UseSpark() — teach the store to read documents written before #420,
// which stored branch coverage as a flat edge list. Reports opened before a
// deploy must still open after it, and this keeps that true independently of
// whether the migration has run.
CodeCoverage.Services.LegacyBranchCompatibility.Enable(
    app.Services.GetRequiredService<Raven.Client.Documents.IDocumentStore>());

// M6d. Proves the forge-qualifying migration left the database in the shape it claims, and exits
// without serving. Placed here on purpose: after Build() because it needs a store, after the
// LegacyBranchCompatibility hook because it streams Build documents, and before UseSpark() because
// that is where pending migrations run — a verifier must observe the database as the migrations
// left it, not race them.
//
// ⚠️ Deliberately NOT named `--spark-verify-*`. Program.cs treats any `--spark-` argument as a build
// command and skips registering the GitHub sign-in provider, including its missing-secret throw —
// so a database verb in that namespace would let a production start come up with no auth provider
// if the secret were absent. The prefix is load-bearing in a way its name does not suggest.
if (args.Contains("--verify-forge-ids", StringComparer.Ordinal))
{
    var findings = await CodeCoverage.Services.ForgeQualifiedIdVerifier.VerifyAsync(
        app.Services.GetRequiredService<Raven.Client.Documents.IDocumentStore>());

    foreach (var finding in findings)
        Console.WriteLine($"{(finding.Ok ? "ok  " : "FAIL")}  {finding.Check}: {finding.Detail}");

    var failed = findings.Count(f => !f.Ok);
    Console.WriteLine(failed == 0
        ? $"All {findings.Count} checks passed."
        : $"{failed} of {findings.Count} checks FAILED.");

    // Non-zero on failure so this is usable from a deploy script without parsing the output.
    // Set rather than returned: the other --spark-* verbs above exit with a bare `return`, and a
    // returning entry point would force every one of them to produce a value.
    Environment.ExitCode = failed == 0 ? 0 : 1;
    return;
}

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

/// <summary>
/// Public only so the test host can name it: <c>WebApplicationFactory&lt;Program&gt;</c> needs the
/// entry-point type to be accessible, and top-level statements generate it as internal. This app
/// runs a live website, so booting the real composition root in tests is the only way its
/// [SparkAuthorize] filters are ever actually executed rather than assumed.
/// </summary>
public partial class Program
{
    [GeneratedRegex(@"Local\:\s+(?<openbrowser>https?\:\/\/(.+))")]
    private static partial Regex openBrowserRegex();
}
