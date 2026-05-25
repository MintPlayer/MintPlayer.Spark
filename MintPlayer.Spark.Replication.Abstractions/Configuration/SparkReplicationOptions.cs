namespace MintPlayer.Spark.Replication.Abstractions.Configuration;

/// <summary>
/// Configuration options for Spark module replication.
/// </summary>
public class SparkReplicationOptions
{
    /// <summary>Name of this module (e.g. "Fleet", "HR").</summary>
    public required string ModuleName { get; set; }

    /// <summary>The publicly reachable URL of this module (e.g. "https://localhost:5001").</summary>
    public required string ModuleUrl { get; set; }

    /// <summary>RavenDB URLs for the shared SparkModules database.</summary>
    public string[] SparkModulesUrls { get; set; } = ["http://localhost:8080"];

    /// <summary>Name of the shared SparkModules database where all modules register.</summary>
    public string SparkModulesDatabase { get; set; } = "SparkModules";

    /// <summary>Assemblies to scan for [Replicated] attributes. If empty, scans the entry assembly.</summary>
    public System.Reflection.Assembly[] AssembliesToScan { get; set; } = [];

    /// <summary>
    /// Cross-module mTLS configuration (R2-C1 / R2-C2 / R2-H7). Each module deploys
    /// with its own X.509 client certificate; cross-module requests carry the cert and
    /// inbound endpoints verify the cert thumbprint matches the one pinned for the
    /// requesting module in SparkModules. On first registration the module pins its
    /// own thumbprint; subsequent re-registrations from a different cert thumbprint
    /// are refused, closing the trust-on-first-claim hole.
    /// </summary>
    public SparkReplicationCertificateOptions ClientCertificate { get; set; } = new();
}

/// <summary>
/// mTLS settings for cross-module replication endpoints.
/// </summary>
public class SparkReplicationCertificateOptions
{
    /// <summary>
    /// When <c>true</c> (default), <c>/spark/etl/deploy</c> and <c>/spark/sync/apply</c>
    /// require a valid client certificate whose SHA-256 thumbprint matches the
    /// requesting module's pinned thumbprint. When <c>false</c>, the endpoints accept
    /// unauthenticated cross-module traffic — only set this in trusted-network
    /// dev/test environments and only with a clear warning in logs.
    /// </summary>
    public bool RequireClientCertificate { get; set; } = true;

    /// <summary>
    /// SHA-256 thumbprint of THIS module's client certificate (uppercase hex). Used
    /// during registration to pin the thumbprint in SparkModules so other modules can
    /// validate inbound calls from this one. Sourced from operator config (appsettings,
    /// env, key vault).
    /// </summary>
    public string? Thumbprint { get; set; }

    /// <summary>
    /// Optional path to the PFX/PEM file this module presents on outbound replication
    /// calls. The Replication package's spark-etl / spark-sync named HttpClients
    /// attach this cert. When <c>null</c>, outbound mTLS auth is not configured —
    /// callers running in trusted-network dev mode can leave this unset.
    /// </summary>
    public string? CertificateFile { get; set; }

    /// <summary>
    /// Optional password for the PFX file at <see cref="CertificateFile"/>.
    /// </summary>
    public string? CertificatePassword { get; set; }
}
