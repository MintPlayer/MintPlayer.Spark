namespace MintPlayer.Spark.Configuration;

/// <summary>
/// The RavenDB connection, bound from the <c>Spark:RavenDb</c> configuration section by <c>spark.UseRavenDb()</c>
/// (#551: it moved out of <c>SparkOptions</c> with the provider, the section name is unchanged).
/// </summary>
public class RavenDbOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Spark:RavenDb";

    /// <summary>
    /// RavenDB cluster URLs. Empty means "use the default", <c>http://localhost:8080</c>. The default must stay
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
