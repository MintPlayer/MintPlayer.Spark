using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Messages;

/// <summary>
/// Message sent via the durable message bus to deploy ETL scripts to a source module.
/// The message bus provides retry logic, persistence, and dead-letter queuing.
/// </summary>
[MessageQueue("spark-etl-deployment")]
public class EtlScriptDeploymentMessage
{
    /// <summary>The source module name that owns the data and should create the ETL task.</summary>
    public required string SourceModuleName { get; set; }

    /// <summary>The URL of the source module (looked up from SparkModules DB).</summary>
    public required string SourceModuleUrl { get; set; }

    /// <summary>The ETL script request payload to POST to the source module.</summary>
    public required EtlScriptRequest Request { get; set; }
}
