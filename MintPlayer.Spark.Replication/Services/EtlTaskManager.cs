using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Replication.Abstractions.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.ConnectionStrings;
using Raven.Client.Documents.Operations.ETL;
using Raven.Client.Documents.Operations.OngoingTasks;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Creates, updates, and removes RavenDB ETL tasks to replicate data to requesting modules.
/// </summary>
internal class EtlTaskManager
{
    private readonly IDocumentStore _documentStore;
    private readonly ILogger<EtlTaskManager> _logger;

    public EtlTaskManager(IDocumentStore documentStore, ILogger<EtlTaskManager> logger)
    {
        _documentStore = documentStore;
        _logger = logger;
    }

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
            // Step 1: Create/update connection string to the requesting module's database
            var connectionString = new RavenConnectionString
            {
                Name = connectionStringName,
                Database = request.TargetDatabase,
                TopologyDiscoveryUrls = request.TargetUrls,
            };

            _documentStore.Maintenance.Send(new PutConnectionStringOperation<RavenConnectionString>(connectionString));
            _logger.LogDebug("Connection string '{ConnectionStringName}' created/updated for database '{Database}'",
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
                _documentStore.Maintenance.Send(
                    new UpdateEtlOperation<RavenConnectionString>(existingTaskId.Value, etlConfig));
                result.TasksUpdated = 1;
                _logger.LogInformation("Updated ETL task '{TaskName}' with {ScriptCount} transformation(s) for module '{Module}'",
                    taskName, transforms.Count, request.RequestingModule);
            }
            else
            {
                _documentStore.Maintenance.Send(new AddEtlOperation<RavenConnectionString>(etlConfig));
                result.TasksCreated = 1;
                _logger.LogInformation("Created ETL task '{TaskName}' with {ScriptCount} transformation(s) for module '{Module}'",
                    taskName, transforms.Count, request.RequestingModule);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy ETL scripts for module '{Module}'", request.RequestingModule);
            result.Success = false;
            result.Error = ex.Message;
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
            var result = _documentStore.Maintenance.Send(
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
