using System.Reflection;

namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// Deploys the application's index definitions to the store at startup (#551 D20), the
/// <see cref="SparkStoreCapabilities.Indexes"/> capability. Core populates and freezes the index catalog first, from
/// every assembly and regardless of the deployment filter, so the model hash and query resolution mean the
/// same thing with or without a filter; this only writes to the database.
/// </summary>
public interface ISparkIndexDeployment
{
    /// <summary>
    /// Creates or updates the indexes declared in <paramref name="assemblies"/>. Best-effort per assembly: one broken
    /// or unreachable module must not cost the others their indexes. <paramref name="deploymentFilter"/> (test hosts)
    /// narrows which index types are deployed.
    /// </summary>
    void Deploy(IReadOnlyList<Assembly> assemblies, Func<Type, bool>? deploymentFilter);
}
