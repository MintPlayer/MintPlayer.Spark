using System.Text.RegularExpressions;
using Fleet;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using MintPlayer.Spark;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.History;
using MintPlayer.Spark.MailManager;
using MintPlayer.Spark.Controllers;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Replication.Authentication;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.ResourceServer;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Migrations;
using MintPlayer.AspNetCore.SpaServices.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Fleet owns data other modules replicate, so it has to be able to recognise them. Asking for a
// client certificate is not the same as requiring one: AllowCertificate keeps every ordinary
// browser and anonymous request working exactly as before, and simply makes the certificate
// available on the connections that do present one.
//
// ⚠️ Asking is still visible to the user: a browser hitting an endpoint that requests a certificate
// shows the Windows certificate / smartcard picker, even though not presenting one is fine. That is
// noise for anyone just clicking through the demo, so it is switchable — `Spark:ClientCertificates`
// defaults to on, and appsettings.Development.json turns it off.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ConfigureHttpsDefaults(https =>
    {
        https.ClientCertificateMode = builder.Configuration.GetValue("Spark:ClientCertificates", true)
            ? ClientCertificateMode.AllowCertificate
            : ClientCertificateMode.NoCertificate;

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
    // Everything comes from the `Spark:Replication` section, bound by AddReplication. Fleet reads HR's people and
    // companies through its [Replicated] classes (Fleet.Replicated); what Fleet owns (cars) other modules read through
    // their own classes, which Fleet approves under "Replication requests" (automatically while Fleet runs in Development).
    options.Replication = _ => { };
    options.RateLimiter = _ => { };

    // Explicit since preview.60: LocalCredentials now defaults to Disabled, matching the client's
    // opt-in routes. Fleet's E2E smoke test signs in with a password, so it says Full out loud
    // rather than relying on a default that has moved.
    // Passkeys are on so the E2E suite can drive a real WebAuthn ceremony against a virtual
    // authenticator. Independent of LocalCredentials above — the two options are unrelated, and Fleet
    // exercises the both-on combination that neither production app does.
    options.Authentication = auth =>
    {
        auth.LocalCredentials = SparkLocalCredentials.Full;
        auth.Passkeys = SparkPasskeys.Enabled;
    };

    options.Configure = spark =>
    {
        // #460 D6: registration needs somewhere to send account mail. Demo app: every mail is written
        // as an .eml file into Spark:Mail:PickupFolder (appsettings.json) instead of being sent.
        spark.AddMailManager();

        // Stamps Car.CreatedBy / CreatedAt (IAuditCreated; the members are generated, #271). The row
        // filter in CarActions scopes users to the cars they created by that stamp.
        spark.AddHistory();

        // What a context root's properties become when synchronize first creates their attributes
        // (docs/default_index_PRD.md, D9). It matters for a root over a type Fleet does not own: FleetContext's
        // MailDeliveries root lists the mail package's SparkMailDelivery, whose model is derived from the CLR type, and
        // Fleet cannot put attributes on that class. Spark's default convention hides the audit names it knows
        // (CreatedBy, CreatedAt*, ModifiedBy, ModifiedAt*). The mail package spells its stamp UpdatedAtUtc, so this
        // wraps the convention to treat SparkMailDelivery's UpdatedAt* as an audit stamp too: created hidden (showedOn
        // None) and read-only. The rule is scoped to that one library type; every other type, Fleet's own included,
        // keeps the default convention, so an UpdatedAt member Fleet declares itself stays an ordinary column.
        // Runs during --spark-synchronize-model only, and never changes an attribute that already exists (#253): an
        // administrator who wants the column after all sets its showedOn in App_Data/Model/SparkMailDelivery.json.
        spark.ModelAttributes((type, property) =>
            type == typeof(MintPlayer.Spark.MailManager.Deliveries.SparkMailDelivery)
            && property.Name.StartsWith("UpdatedAt", StringComparison.Ordinal)
                ? MintPlayer.Spark.Configuration.ModelAttributeSeed.Hidden
                : MintPlayer.Spark.Configuration.ModelAttributeConventions.AuditMembersHidden(type, property));

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

        // People sign in through SparkId, the demo identity provider: appsettings.Development.json
        // registers it as the OpenID Connect scheme "SparkId", and SparkId seeds the "fleet" client.
        spark.AddExternalProviders(builder.Configuration);

        // MCP server (docs/guide-mcp.md): agents read cars, their service history, their managers and companies as the
        // signed-in user sees them (Car, ServiceEntry, Person and Company are "agentPermissions": "Read"), and never
        // change anything. Personal access tokens (account page, Connected applications) work everywhere.
        spark.AddMcp(mcp =>
        {
            mcp.ServerName = "Fleet";
            mcp.ToolPrefix = "fleet";
            mcp.Instructions = "Cars, their service history, the people who manage them and companies in the Fleet demo, as the signed-in user sees them. Read only.";
            mcp.AgentPermissions = _ => EAgentPermission.Read;
        });

        // Fleet is also its agents' authorization server (setup 3): an agent signs the user in here, with Fleet's own
        // sign-in (password, passkey or SparkId), and gets a token for Fleet's /mcp and nothing else. The provider beside
        // the SparkId sign-in serves agents only: AgentsOnly() maps what agents use (metadata, sign-in and consent, code
        // and refresh, loopback registration, revocation) and nothing else of the provider, and its management types stay
        // off (security.json "libraries"). It needs a pinned issuer and the MCP canonical URL (appsettings.Development.json;
        // a deployment also sets the signing key, Spark:IdentityProvider:SigningKeyPath, from its secrets). Without an
        // issuer /mcp takes personal access tokens only, as in the E2E host.
        if (builder.Configuration["Spark:IdentityProvider:Issuer"] is { Length: > 0 })
            spark.AddIdentityProvider(idp => idp.ForMcp().AgentsOnly());

        // Fleet is a resource server: SparkId issues machine tokens, Fleet validates them. Fleet used
        // to host an issuer of its own for this round trip; since SparkId exists the topology is the
        // real one (docs/identity_provider_platform_PRD.md R1), and Spark:JwtBearer:Authority names
        // the issuer Fleet trusts. Unset, Fleet accepts no bearer tokens at all.
        var authority = builder.Configuration["Spark:JwtBearer:Authority"];
        if (!string.IsNullOrWhiteSpace(authority))
        {
            // The consumer half of client_credentials, and the reason a CI job can POST here at all.
            // Audience is required by the extension: without it this app would accept every token
            // the issuer ever minted, including ones a client obtained for a different resource.
            // As a resource server (PRD I12) it also accepts only at+jwt access tokens and enforces
            // DPoP and certificate binding; /api/fleet/cars below needs the fleet.read scope.
            spark.AddSparkResourceServer(
                authority,
                builder.Configuration["Spark:JwtBearer:Audience"] ?? "fleet-api",
                rs =>
                {
                    // Discovery is fetched over the issuer's own scheme. Left at the default this
                    // demands HTTPS, which a local http issuer cannot satisfy.
                    rs.RequireHttpsMetadata = authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                    // Ask SparkId about every token instead of trusting it until it expires. The
                    // introspecting client's id must be the audience (SparkId's MayIntrospect).
                    rs.UseIntrospection = builder.Configuration.GetValue("Spark:JwtBearer:UseIntrospection", false);
                    rs.IntrospectionClientId = builder.Configuration["Spark:JwtBearer:IntrospectionClientId"];
                    rs.IntrospectionClientSecret = builder.Configuration["Spark:JwtBearer:IntrospectionClientSecret"];
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

// --spark-verify-security fails the build when the set of rights reachable WITHOUT signing in
// has changed. security.json is a data file: widening it is a one-line diff that reads no
// differently from narrowing it, so the baseline is what makes the change reviewable.
if (builder.VerifySparkSecurityIfRequested(args))
    return;

var app = builder.Build();

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
    // The app's own generator endpoints (Api/FleetCarsEndpoint.cs, the I12 resource-server demo).
    MintPlayer.AspNetCore.Endpoints.Generated.FleetEndpointsExtensions.MapFleetEndpoints(endpoints);
});

// /connect/* and /.well-known/* (the agents' authorization server) are server-rendered or JSON, as on SparkId: an
// unknown path there answers 404, never the SPA's index.html, which an agent probing for metadata would misread.
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/spark")
        && !context.Request.Path.StartsWithSegments("/connect")
        && !context.Request.Path.StartsWithSegments("/.well-known"),
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