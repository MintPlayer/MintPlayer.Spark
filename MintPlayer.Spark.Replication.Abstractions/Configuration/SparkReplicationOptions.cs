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
}
