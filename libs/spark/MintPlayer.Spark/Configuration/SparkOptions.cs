using System.Reflection;

namespace MintPlayer.Spark.Configuration;

public class SparkOptions
{
    public RavenDbOptions RavenDb { get; set; } = new();
    public BreadcrumbOptions Breadcrumb { get; set; } = new();

    /// <summary>
    /// Decides what each property of a context-root type becomes when the synchronizer first creates its
    /// model attribute (<c>docs/default_index_PRD.md</c>, D9): <see cref="ModelAttributeSeed.Default"/>,
    /// <see cref="ModelAttributeSeed.Hidden"/> or <see cref="ModelAttributeSeed.Exclude"/>. It matters most
    /// for library types (an <c>Organizations</c> root over <c>Organization</c>), whose properties the app
    /// cannot annotate.
    /// <para>
    /// Defaults to <see cref="ModelAttributeConventions.AuditMembersHidden"/>. Replace or wrap it with
    /// <c>spark.ModelAttributes(...)</c>. <c>[IgnoreProperty]</c> always excludes, whatever this returns.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Runs only during synchronize, and only when an attribute is first created: it never changes or
    /// deletes an existing attribute (#253). It must be pure — the same answer for the same property on
    /// every run — or the model file churns.
    /// </remarks>
    public Func<Type, PropertyInfo, ModelAttributeSeed> ModelAttribute { get; set; } = ModelAttributeConventions.AuditMembersHidden;
}

public class BreadcrumbOptions
{
    /// <summary>
    /// Maximum reference depth the breadcrumb resolver follows (roots are depth 0). Bounds the
    /// number of batched load round-trips and breaks reference cycles. Default: 5.
    /// </summary>
    public int MaxDepth { get; set; } = 5;

    /// <summary>Separator joining the breadcrumbs of a reference-array placeholder. Default: ", ".</summary>
    public string ReferenceSeparator { get; set; } = ", ";

    /// <summary>
    /// Rendered in place of a referenced entity the current user is not allowed to read
    /// (row-level Read denied). Default: "—".
    /// </summary>
    public string RedactedPlaceholder { get; set; } = "—";
}

public class RavenDbOptions
{
    /// <summary>
    /// RavenDB cluster URLs. Empty means "use the default" — see <see cref="SparkExtensions"/>
    /// where an unset value falls back to <c>http://localhost:8080</c>. The default must stay
    /// empty so <see cref="Microsoft.Extensions.Configuration.ConfigurationBinder"/> can replace
    /// it: array binding APPENDS config values to an existing array rather than overwriting,
    /// so a non-empty default would prevent <c>appsettings.{env}.json</c> or env-var overrides
    /// from taking effect for the first URL.
    /// </summary>
    public string[] Urls { get; set; } = [];
    public string Database { get; set; } = "Spark";

    /// <summary>
    /// Maximum number of connection attempts when waiting for RavenDB to become available.
    /// Set to 0 to disable retry logic. Default: 30.
    /// </summary>
    public int MaxConnectionRetries { get; set; } = 30;

    /// <summary>
    /// Delay in seconds between connection retry attempts. Default: 2.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 2;

    /// <summary>
    /// Automatically create the database if it does not exist.
    /// Always enabled in Development mode. Set to true for container deployments
    /// where the database may not exist yet. Default: false.
    /// </summary>
    public bool EnsureDatabaseCreated { get; set; } = false;

    /// <summary>
    /// The client certificate this app presents to a secured RavenDB server (HTTPS with client
    /// certificates). Either a PKCS#12 file (<c>.pfx</c>/<c>.p12</c>, with
    /// <see cref="CertificatePassword"/> when it has one) or a PEM certificate, whose private key is in
    /// <see cref="CertificateKeyFile"/> or in the same file. Empty (the default) = no certificate: the
    /// unsecured <c>http://</c> server of local development and the tests. A certificate requires every
    /// URL in <see cref="Urls"/> to be <c>https://</c>; startup refuses otherwise. Applied by
    /// <see cref="SparkRavenDbSecurity.Apply"/>.
    /// </summary>
    public string? CertificateFile { get; set; }

    /// <summary>The PEM private key of <see cref="CertificateFile"/>, when it is a separate file.</summary>
    public string? CertificateKeyFile { get; set; }

    /// <summary>The password of a PKCS#12 <see cref="CertificateFile"/> or of an encrypted PEM key.</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>
    /// A PEM or DER file holding the certificate authority that issued the RavenDB <em>server's</em>
    /// certificate, for a server whose certificate is not publicly trusted (a private CA on an internal
    /// host name). The server's certificate is then accepted when it chains to this authority and its
    /// name matches the URL; nothing else is relaxed. Empty = the operating system's trust store only.
    /// </summary>
    public string? ServerCertificateAuthorityFile { get; set; }
}
