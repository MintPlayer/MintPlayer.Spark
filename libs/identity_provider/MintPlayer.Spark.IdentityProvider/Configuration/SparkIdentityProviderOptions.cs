namespace MintPlayer.Spark.IdentityProvider.Configuration;

public class SparkIdentityProviderOptions
{
    /// <summary>
    /// The issuer identifier this provider stamps on every token it mints and requires on
    /// every token it validates — e.g. <c>https://id.example.com</c>.
    /// <para>
    /// Required outside Development. It was previously derived from the request's
    /// <c>Host</c> header on every issuance and validation path, which the client controls: a
    /// forged <c>Host</c> minted tokens claiming a different issuer, signed with the real key,
    /// and a relying party that trusts this key would accept them. In Development it still
    /// falls back to the request so a local run needs no configuration.
    /// </para>
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Path to signing key file. Default: App_Data/oidc-signing-key.json
    /// Auto-generated in Development; must be provided in Production.
    /// </summary>
    public string SigningKeyPath { get; set; } = "App_Data/oidc-signing-key.json";

    /// <summary>
    /// Whether to auto-approve consent for clients with ConsentType = "implicit".
    /// </summary>
    public bool AutoApproveImplicitConsent { get; set; } = true;

    /// <summary>
    /// Token cleanup interval. Default: 1 hour.
    /// </summary>
    public TimeSpan TokenCleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Opt in to cross-origin access on the OIDC protocol endpoints. <b>Off by default.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a <b>browser-based client on a different origin</b> needs this — a SPA doing
    /// authorization-code + PKCE against this provider, hosted somewhere else. An application's own
    /// Angular frontend does not: Spark serves it from the same host, so every call to
    /// <c>/connect/token</c> is same-origin and CORS never enters into it.
    /// </para>
    /// <para>
    /// ⚠️ <b>This said it "automatically allows origins registered in
    /// <c>OidcApplication.AllowedCorsOrigins</c>", defaulted to <see langword="true"/>, and did neither
    /// of those things.</b> The policy was <c>SetIsOriginAllowed(_ =&gt; true)</c> — any origin, never
    /// consulting the registered list, which was read by nothing in this repository. So every
    /// application that turned the identity provider on granted a cross-origin permission it did not
    /// ask for, to support a scenario whose access control was never built, described by a comment that
    /// said the opposite.
    /// </para>
    /// <para>
    /// It now does what it always claimed: the allowed set is the union of
    /// <c>AllowedCorsOrigins</c> across <b>enabled</b> applications. A union, because a preflight is an
    /// anonymous <c>OPTIONS</c> with no <c>client_id</c> — see <c>OidcCorsOrigins</c>.
    /// </para>
    /// <para>
    /// ⚠️ <b>Turning this on without registering an origin allows nothing.</b> The symptom is a
    /// missing header rather than an error, so the host logs a warning at startup when that is the
    /// case. See <c>docs/guide-cors.md</c>.
    /// </para>
    /// </remarks>
    public bool EnableDynamicCors { get; set; }
}
