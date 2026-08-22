using System.Text.RegularExpressions;
using Fleet;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using MintPlayer.Spark;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Controllers;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Replication.Authentication;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.AspNetCore.SpaServices.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Fleet owns data other modules replicate, so it has to be able to recognise them. Asking for a
// client certificate is not the same as requiring one: AllowCertificate keeps every ordinary
// browser and anonymous request working exactly as before, and simply makes the certificate
// available on the connections that do present one.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ConfigureHttpsDefaults(https =>
    {
        https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;

        // Kestrel validates the client certificate's chain during the TLS handshake, before any
        // authentication handler runs. Module certificates come from an operator-created CA that no
        // machine trusts by default, so the handshake would fail and the request would never reach
        // Spark — the scheme would look misconfigured rather than refused.
        //
        // Deferring here is not "trust anything": it moves the decision to the thumbprint pinned in
        // SparkModules, which is strictly narrower than chain validation. A certificate from the
        // right CA still fails unless it is *the* certificate that module registered.
        https.AllowAnyClientCertificate();
    });
});

builder.Services.AddSparkFull(builder.Configuration, options =>
{
    // Everything else comes from the `Spark:Replication` section, bound by AddReplication.
    // Assemblies are the one setting configuration cannot express.
    options.Replication = opt => opt.AssembliesToScan = [typeof(Fleet.Replicated.Person).Assembly];
    options.RateLimiter = _ => { };

    // Explicit since preview.60: LocalCredentials now defaults to Disabled, matching the client's
    // opt-in routes. Fleet's E2E smoke test signs in with a password, so it says Full out loud
    // rather than relying on a default that has moved.
    options.Authentication = auth => auth.LocalCredentials = SparkLocalCredentials.Full;

    options.Configure = spark =>
    {
        // Mounted through Spark rather than with endpoints.MapControllers(), so the controllers
        // share Spark's pipeline — its authentication schemes, its antiforgery scope, and
        // [SparkAuthorize]. A bare MapControllers() is reported by SPARK010.
        spark.AddControllers();
        spark.UseControllers();

        // A module presenting its registered certificate becomes an ordinary Spark caller: the CN
        // names the module, the handler emits `group = "Module:{CN}"`, and security.json governs it
        // exactly like a person. Nothing here is replication-specific — the credential works on
        // every Spark endpoint, which is the point of M9's composite scheme.
        spark.AddModuleCertificateAuthentication();

        // Fleet both issues and consumes machine tokens here. In a real topology those are separate
        // deployments — a central SparkId issues, each resource server validates — but keeping both
        // in one app is what makes the round trip demonstrable, and the two halves are configured
        // independently either way.
        var issuer = builder.Configuration["SparkIdentityProvider:Issuer"];
        if (!string.IsNullOrWhiteSpace(issuer))
        {
            spark.AddIdentityProvider(idp =>
            {
                idp.Issuer = issuer;

                // Outside Development the provider requires a key rather than generating one, so
                // this has to be configurable — a key that appears on first use is a key nobody
                // backed up, and every token in flight dies at the next restart.
                idp.SigningKeyPath = builder.Configuration["SparkIdentityProvider:SigningKeyPath"]
                    ?? idp.SigningKeyPath;
            });
        }

        // Who this app TRUSTS is a separate question from who it IS, and configuring them
        // separately is the difference between a demo and the real topology. These were briefly the
        // same value here, which quietly made "validate tokens from SparkId" impossible: Fleet could
        // only trust an issuer it was also hosting at that URL. Defaulting to the self-hosted issuer
        // keeps the single-app demo working; setting Spark:JwtBearer:Authority points Fleet at a
        // separate provider, which is what a real deployment does.
        var authority = builder.Configuration["Spark:JwtBearer:Authority"] ?? issuer;
        if (!string.IsNullOrWhiteSpace(authority))
        {
            // The consumer half of client_credentials, and the reason a CI job can POST here at all.
            // Audience is required by the extension: without it this app would accept every token
            // the issuer ever minted, including ones a client obtained for a different resource.
            spark.AddJwtBearerCredential(jwt =>
            {
                jwt.Authority = authority;
                jwt.Audience = builder.Configuration["Spark:JwtBearer:Audience"] ?? "fleet-api";

                // Discovery is fetched over the issuer's own scheme. Left at the default this
                // demands HTTPS, which a local http issuer cannot satisfy.
                jwt.RequireHttpsMetadata = authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            });
        }
    };
});

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = ".SparkAuth.Fleet";
});

builder.Services.AddSpaStaticFilesImproved(configuration =>
{
    configuration.RootPath = "ClientApp/dist/ClientApp/browser";
});

// Model synchronization is a build step, not a run mode: it writes App_Data/Model/*.json from the
// entity classes and needs no database, so it runs here and the process returns before Build().
if (builder.SynchronizeSparkModelsIfRequested(args))
    return;

// Writes a starting App_Data/security.json for an application that has none. Never overwrites.
if (builder.InitializeSparkSecurityIfRequested(args))
    return;

var app = builder.Build();

app.UseForwardedHeaders();

// Deployments that terminate TLS at a proxy — and the E2E host, whose issuer must be reachable
// over plain http so the JWT handler can fetch discovery from itself without a trusted certificate
// — turn this off. Defaults to on, so nothing changes for an ordinary run.
if (builder.Configuration.GetValue("Spark:HttpsRedirection", true))
    app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSpaStaticFilesImproved();

app.UseRouting();
app.UseSparkFull();

app.UseEndpoints(endpoints =>
{
    endpoints.MapSparkFull();
});

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark"),
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