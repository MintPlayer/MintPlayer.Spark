using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Replication.Abstractions.Configuration;
using MintPlayer.Spark.Replication.Services;

namespace MintPlayer.Spark.Replication.Authentication;

public static class SparkModuleCertificateDefaults
{
    /// <summary>The authentication scheme that turns a client certificate into a module identity.</summary>
    public const string Scheme = "Spark:ModuleCertificate";

    /// <summary>
    /// Claim value prefix for a module's group membership. A module registered as <c>HR</c> arrives
    /// carrying <c>group = "Module:HR"</c>, so <c>security.json</c> can grant it rights by name with
    /// no schema change and no special casing — a module is just another group.
    /// </summary>
    public const string GroupPrefix = "Module:";
}

/// <summary>
/// Turns a cross-module client certificate into a principal.
/// <para>
/// The identity comes from the certificate's <c>CN</c>, not from the request body. That is the
/// point: the pinning check has always been sound, but the module it checked *against* was whatever
/// the caller wrote in <c>RequestingModule</c>, so the body chose which pin to be measured by. A
/// certificate the caller cannot forge naming the module it belongs to removes that step entirely —
/// and the operator recipe in the mTLS guide already sets <c>CN=$MODULE</c>, so nothing changes for
/// existing deployments.
/// </para>
/// <para>
/// Registering this makes the module a first-class Spark caller: it authenticates like any other
/// credential, lands in <c>HttpContext.User</c>, and resolves groups through the same
/// <c>security.json</c> path as a person. That is what M11 needs in order to route
/// <c>/spark/sync/apply</c> through the permission chokepoint instead of around it.
/// </para>
/// </summary>
public static class SparkModuleCertificateExtensions
{
    /// <summary>
    /// Adds client-certificate authentication for cross-module calls and registers it as a Spark
    /// credential.
    /// </summary>
    public static ISparkBuilder AddModuleCertificateAuthentication(this ISparkBuilder builder)
    {
        builder.Services
            .AddAuthentication()
            .AddCertificate(SparkModuleCertificateDefaults.Scheme, options =>
            {
                // The guide tells operators to create their own CA and issue module certs from it,
                // so the chain terminates in a root no machine trusts by default. Left at the
                // defaults, chain validation rejects every certificate the documented recipe
                // produces — the scheme would authenticate nobody and look like a config error.
                //
                // The trust decision is not delegated to the chain here; it is the thumbprint pin
                // in SparkModules, which is strictly narrower: a valid certificate from the right
                // CA still fails unless it is *the* certificate that module registered.
                options.AllowedCertificateTypes = CertificateTypes.All;
                options.ValidateCertificateUse = false;
                options.RevocationMode = X509RevocationMode.NoCheck;

                options.Events = new CertificateAuthenticationEvents
                {
                    OnCertificateValidated = OnCertificateValidatedAsync,
                    OnAuthenticationFailed = context =>
                    {
                        context.Fail(context.Exception);
                        return Task.CompletedTask;
                    },
                };
            });

        // Not ambient. A browser cannot be induced by a third-party page to complete a TLS
        // handshake with a module's private key, so there is no ambient authority to forge and an
        // antiforgery token would only block the machine callers this exists to admit.
        //
        // Worth stating because "a certificate is never ambient" is NOT true in general: a browser
        // configured with a client certificate for an origin attaches it automatically, exactly
        // like a cookie. This scheme is safe to treat as non-ambient because it authenticates
        // *modules* against a registration pin — a browser holds no such certificate. A
        // browser-facing client-certificate deployment would need to revisit this.
        return builder.AddCredentialScheme(SparkModuleCertificateDefaults.Scheme, isAmbient: false);
    }

    private static async Task OnCertificateValidatedAsync(CertificateValidatedContext context)
    {
        var services = context.HttpContext.RequestServices;
        var directory = services.GetRequiredService<IModuleDirectory>();
        var options = services.GetRequiredService<IOptions<SparkReplicationOptions>>().Value;
        var environment = services.GetRequiredService<IHostEnvironment>();
        var logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MintPlayer.Spark.Replication.ModuleCertificate");

        var moduleName = context.ClientCertificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            context.Fail("The client certificate carries no CN, so it names no module.");
            return;
        }

        var module = await directory.FindAsync(moduleName, context.HttpContext.RequestAborted);
        if (module is null)
        {
            // Deliberately the same refusal as a thumbprint mismatch, and deliberately not logged
            // with the presented CN at a level that reaches ordinary logs: "that module does not
            // exist" and "that is not its certificate" should not be distinguishable to a caller
            // probing which modules are registered.
            logger.LogDebug("Client certificate CN '{Module}' is not a registered module.", moduleName);
            context.Fail("Unrecognised module certificate.");
            return;
        }

        if (ResolveMode(options, environment) == SparkReplicationCertificateMode.Production
            && !ThumbprintMatches(context.ClientCertificate, module.ClientCertificateThumbprint))
        {
            logger.LogWarning(
                "Client certificate for module '{Module}' does not match its pinned thumbprint.", moduleName);
            context.Fail("Unrecognised module certificate.");
            return;
        }

        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, moduleName),
                new Claim(ClaimTypes.Name, moduleName),

                // The entire authorization integration. AccessControlService resolves groups from
                // this claim, so a module becomes governable by security.json without the
                // authorization model learning what a module is.
                new Claim("group", SparkModuleCertificateDefaults.GroupPrefix + moduleName),

                // A module is the system acting, not a viewer — row-level rules (which scope
                // rows to a person) don't apply to it. See SparkSystemContext.
                new Claim(SparkSystemContext.ClaimType, "module"),
            ],
            SparkModuleCertificateDefaults.Scheme));

        context.Success();
    }

    /// <summary>
    /// Whether the pin is enforced. Mirrors <see cref="IModuleCertificateValidator"/>'s ladder so a
    /// deployment cannot be strict at one gate and relaxed at the other.
    /// <para>
    /// Note what <c>Development</c> does <b>not</b> relax: the CN must still name a registered
    /// module. Skipping the thumbprint is what makes local multi-module work possible without a CA;
    /// skipping identity would make the scheme an accept-anything (F1).
    /// </para>
    /// </summary>
    private static SparkReplicationCertificateMode ResolveMode(
        SparkReplicationOptions options,
        IHostEnvironment environment)
    {
        var mode = options.ClientCertificate.Mode;
        if (mode != SparkReplicationCertificateMode.Auto)
            return mode;

        return environment.IsDevelopment()
            ? SparkReplicationCertificateMode.Development
            : SparkReplicationCertificateMode.Production;
    }

    /// <summary>
    /// Compares the presented certificate against the module's pinned SHA-256 thumbprint. A module
    /// registered before the pin existed has none, and fails closed rather than matching everything.
    /// </summary>
    private static bool ThumbprintMatches(X509Certificate2 presented, string? pinned)
    {
        if (string.IsNullOrEmpty(pinned))
            return false;

        var actual = presented.GetCertHashString(HashAlgorithmName.SHA256);
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual.ToUpperInvariant()),
            System.Text.Encoding.ASCII.GetBytes(pinned.ToUpperInvariant()));
    }
}
