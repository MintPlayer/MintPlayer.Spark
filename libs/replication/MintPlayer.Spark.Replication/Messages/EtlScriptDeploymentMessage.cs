using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Messages;

/// <summary>
/// Message sent via the durable message bus to deploy ETL scripts to a source module.
/// The message bus provides retry logic, persistence, and dead-letter queuing.
///
/// The recipient resolves the source module's URL from the shared <c>SparkModules</c>
/// database on each delivery — never carries the URL on the message itself, so retries
/// pick up the freshly-registered URL after a slow-starting source module comes online.
/// </summary>
[MessageQueue("spark-etl-deployment")]
public class EtlScriptDeploymentMessage
{
    /// <summary>The source module name that owns the data and should create the ETL task.</summary>
    public required string SourceModuleName { get; set; }

    /// <summary>The ETL script request payload to POST to the source module.</summary>
    public required EtlScriptRequest Request { get; set; }
}
