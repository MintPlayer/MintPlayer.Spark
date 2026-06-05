using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.ConnectionStrings;
using Raven.Client.Documents.Operations.ETL;
using Raven.Client.Documents.Operations.OngoingTasks;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Creates, updates, and removes RavenDB ETL tasks to replicate data to requesting modules.
/// </summary>
internal partial class EtlTaskManager
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ILogger<EtlTaskManager> logger;

    /// <summary>
    /// Deploys ETL scripts by creating/updating RavenDB ETL tasks.
    /// This operation is idempotent.
    /// </summary>
    public Task<EtlDeploymentResult> DeployAsync(EtlScriptRequest request)
    {
        var result = new EtlDeploymentResult { Success = true };
        var connectionStringName = $"spark-etl-{request.RequestingModule}";
        var taskName = $"spark-etl-{request.RequestingModule}";

        try
        {
            // Refuse to create an ETL task whose source and target are the same database —
            // that would cause an infinite write loop (every applied transform re-triggers
            // the source). Only the requesting module is supposed to invoke this, on the
            // source module's store; if the two collapse to the same DB, the message bus
            // routed the deployment to the wrong recipient (e.g. stale moduleInformations
            // URL pointing back at the requester).
            if (string.Equals(request.TargetDatabase, documentStore.Database, StringComparison.OrdinalIgnoreCase))
            {
                // Known, expected failure — return it directly so callers can
                // distinguish self-loop config errors from generic ETL_DEPLOY_FAILED
                // failures that the R2-L6 catch returns. We keep the human-readable
                // string here because the caller is a trusted module (per R2-C1
                // mTLS gate); only fully-generic ex.Message round-trips were the leak.
                logger.LogWarning("Refusing self-loop ETL deployment for module '{Module}' (target db = local db '{Db}')",
                    request.RequestingModule, documentStore.Database);
                return Task.FromResult(new EtlDeploymentResult
                {
                    Success = false,
                    Error = $"self-loop refused: source and target are both '{documentStore.Database}'",
                });
            }

            // Step 1: Create/update connection string to the requesting module's database
            var connectionString = new RavenConnectionString
            {
                Name = connectionStringName,
                Database = request.TargetDatabase,
                TopologyDiscoveryUrls = request.TargetUrls,
            };

            documentStore.Maintenance.Send(new PutConnectionStringOperation<RavenConnectionString>(connectionString));
            logger.LogDebug("Connection string '{ConnectionStringName}' created/updated for database '{Database}'",
                connectionStringName, request.TargetDatabase);

            // Step 2: Build the ETL configuration with transformations
            var transforms = request.Scripts.Select(script => new Transformation
            {
                Name = script.SourceCollection,
                Collections = { script.SourceCollection },
                Script = script.Script,
            }).ToList();

            var etlConfig = new RavenEtlConfiguration
            {
                ConnectionStringName = connectionStringName,
                Name = taskName,
                Transforms = transforms,
            };

            // Step 3: Try to find an existing ETL task by name and update it, or create new
            var existingTaskId = FindExistingEtlTaskId(taskName);

            if (existingTaskId.HasValue)
            {
                documentStore.Maintenance.Send(
                    new UpdateEtlOperation<RavenConnectionString>(existingTaskId.Value, etlConfig));
                result.TasksUpdated = 1;
                logger.LogInformation("Updated ETL task '{TaskName}' with {ScriptCount} transformation(s) for module '{Module}'",
                    taskName, transforms.Count, request.RequestingModule);
            }
            else
            {
                documentStore.Maintenance.Send(new AddEtlOperation<RavenConnectionString>(etlConfig));
                result.TasksCreated = 1;
                logger.LogInformation("Created ETL task '{TaskName}' with {ScriptCount} transformation(s) for module '{Module}'",
                    taskName, transforms.Count, request.RequestingModule);
            }
        }
        catch (Exception ex)
        {
            // R2-L6: log full exception server-side; surface a generic message
            // to the caller. RavenDB maintenance exceptions often contain
            // connection-string detail useful for recon.
            logger.LogError(ex, "Failed to deploy ETL scripts for module '{Module}'", request.RequestingModule);
            result.Success = false;
            result.Error = "ETL_DEPLOY_FAILED";
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Tries to find an existing RavenDB ETL task by name and returns its task ID.
    /// </summary>
    private long? FindExistingEtlTaskId(string taskName)
    {
        try
        {
            var result = documentStore.Maintenance.Send(
                new GetOngoingTaskInfoOperation(taskName, OngoingTaskType.RavenEtl));
            return result?.TaskId;
        }
        catch
        {
            // Task doesn't exist
            return null;
        }
    }
}
