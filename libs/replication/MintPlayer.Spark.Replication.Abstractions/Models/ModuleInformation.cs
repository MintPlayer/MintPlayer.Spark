namespace MintPlayer.Spark.Replication.Abstractions.Models;

/// <summary>
/// Stored in the shared SparkModules database so that modules can discover each other.
/// Document ID: "moduleInformations/{AppName}"
/// </summary>
public class ModuleInformation
{
    /// <summary>
    /// The document id a module registers under. Derived rather than queried, so every
    /// lookup is a point-load — which matters for the ones that gate authentication:
    /// an index would answer from a possibly-stale view.
    /// </summary>
    public static string DocumentId(string moduleName) => $"moduleInformations/{moduleName}";

    public string? Id { get; set; }
    public required string AppName { get; set; }
    public required string AppUrl { get; set; }
    public required string DatabaseName { get; set; }
    public string[] DatabaseUrls { get; set; } = [];
    public DateTime RegisteredAtUtc { get; set; }

    /// <summary>
    /// SHA-256 thumbprint (uppercase hex) of the module's client certificate, pinned
    /// on first registration. Inbound replication endpoints validate the presenting
    /// caller's client cert thumbprint against this value to enforce cross-module
    /// mTLS auth (R2-C1 / R2-C2 / R2-H7). Null when the module didn't supply a
    /// thumbprint at registration — legacy entries created before the mTLS roll-out.
    /// </summary>
    public string? ClientCertificateThumbprint { get; set; }
}
