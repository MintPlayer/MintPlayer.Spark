using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Replication.Abstractions.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.ConnectionStrings;
using Raven.Client.Documents.Operations.ETL;
using Raven.Client.Documents.Operations.OngoingTasks;
using Raven.Client.Exceptions.Commercial;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>One owner-generated ETL transform.</summary>
/// <param name="Name">The transform name, unique within the task.</param>
/// <param name="SourceCollection">The owner's collection it reads.</param>
/// <param name="Script">The script <see cref="EtlTransformGenerator"/> generated.</param>
internal sealed record EtlTransformScript(string Name, string SourceCollection, string Script);

/// <summary>
/// What the owner deploys for one consumer. Every part of it is the owner's own: the destination
/// from the consumer's registry entry (F19), the transforms from the owner's contracts (F16).
/// </summary>
internal sealed record EtlDeployment(
    string RequestingModule,
    string TargetDatabase,
    string[] TargetUrls,
    IReadOnlyList<EtlTransformScript> Transforms);

internal interface IEtlTaskManager
{
    /// <summary>Creates or updates the consumer's ETL task. Idempotent.</summary>
    Task<EtlDeploymentResult> DeployAsync(EtlDeployment deployment, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates, updates, and removes RavenDB ETL tasks to replicate data to requesting modules.
/// </summary>
internal partial class EtlTaskManager : IEtlTaskManager
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ILogger<EtlTaskManager> logger;
    [Inject] private readonly IRavenEtlLicence licence;

    /// <summary>
    /// Deploys the transforms by creating or updating the consumer's RavenDB ETL task.
    /// This operation is idempotent.
    /// </summary>
    public async Task<EtlDeploymentResult> DeployAsync(EtlDeployment deployment, CancellationToken cancellationToken = default)
    {
        var result = new EtlDeploymentResult { Success = true };
        var connectionStringName = $"spark-etl-{deployment.RequestingModule}";
        var taskName = $"spark-etl-{deployment.RequestingModule}";

        try
        {
            // Refuse to create an ETL task whose source and target are the same database —
            // that would cause an infinite write loop (every applied transform re-triggers
            // the source). The target comes from the requesting module's registry entry, so the
            // two collapse only when that entry names this module's own database.
            if (string.Equals(deployment.TargetDatabase, documentStore.Database, StringComparison.OrdinalIgnoreCase))
            {
                // Known, expected failure — return it directly so callers can
                // distinguish self-loop config errors from generic ETL_DEPLOY_FAILED
                // failures that the R2-L6 catch returns. We keep the human-readable
                // string here because the caller is a trusted module (per R2-C1
                // mTLS gate); only fully-generic ex.Message round-trips were the leak.
                logger.LogWarning("Refusing self-loop ETL deployment for module '{Module}' (target db = local db '{Db}')",
                    deployment.RequestingModule, documentStore.Database);
                return new EtlDeploymentResult
                {
                    Success = false,
                    Error = $"self-loop refused: source and target are both '{documentStore.Database}'",
                };
            }

            // F18: a licence without ETL (Community) refuses here with a reason the consumer can act
            // on, instead of failing on AddEtlOperation with a generic error it would retry for ever.
            if (await licence.HasRavenEtlAsync(cancellationToken) == false)
                return NotLicensed(deployment.RequestingModule);

            // Step 1: Create/update connection string to the requesting module's database
            var connectionString = new RavenConnectionString
            {
                Name = connectionStringName,
                Database = deployment.TargetDatabase,
                TopologyDiscoveryUrls = deployment.TargetUrls,
            };

            documentStore.Maintenance.Send(new PutConnectionStringOperation<RavenConnectionString>(connectionString));
            logger.LogDebug("Connection string '{ConnectionStringName}' created/updated for database '{Database}'",
                connectionStringName, deployment.TargetDatabase);

            // Step 2: Build the ETL configuration from the owner-generated transforms
            var transforms = deployment.Transforms.Select(transform => new Transformation
            {
                Name = transform.Name,
                Collections = { transform.SourceCollection },
                Script = transform.Script,
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
                    taskName, transforms.Count, deployment.RequestingModule);
            }
            else
            {
                documentStore.Maintenance.Send(new AddEtlOperation<RavenConnectionString>(etlConfig));
                result.TasksCreated = 1;
                logger.LogInformation("Created ETL task '{TaskName}' with {ScriptCount} transformation(s) for module '{Module}'",
                    taskName, transforms.Count, deployment.RequestingModule);
            }
        }
        catch (LicenseLimitException ex)
        {
            // The licence status was unreadable, and RavenDB refused on send: the same answer.
            logger.LogError(ex, "ETL deployment for module '{Module}' refused by the RavenDB licence", deployment.RequestingModule);
            return NotLicensed(deployment.RequestingModule);
        }
        catch (Exception ex)
        {
            // R2-L6: log full exception server-side; surface a generic message
            // to the caller. RavenDB maintenance exceptions often contain
            // connection-string detail useful for recon.
            logger.LogError(ex, "Failed to deploy ETL scripts for module '{Module}'", deployment.RequestingModule);
            result.Success = false;
            result.Error = "ETL_DEPLOY_FAILED";
        }

        return result;
    }

    private EtlDeploymentResult NotLicensed(string module)
    {
        logger.LogError(
            "ETL deployment for module '{Module}' refused: this RavenDB licence has no ETL (Community). " +
            "Use a licence with RavenDB ETL, or the HTTP replication transport.", module);
        return new EtlDeploymentResult { Success = false, Error = EtlDeploymentResult.NotLicensedError };
    }

    /// <summary>
    /// Finds an existing RavenDB ETL task by name and returns its task ID, or <c>null</c> when
    /// there is none.
    /// <para>
    /// A missing task comes back as a <c>null</c> result, not an exception, so nothing here is
    /// caught. It used to swallow every exception as "task doesn't exist": a transient failure on
    /// this one request then sent an add for a task that already existed, and the deployment
    /// failed on the duplicate — logging the duplicate, not the fault that caused it. A failure
    /// now reaches the caller's catch, which reports it as what it is.
    /// </para>
    /// </summary>
    private long? FindExistingEtlTaskId(string taskName)
    {
        var result = documentStore.Maintenance.Send(
            new GetOngoingTaskInfoOperation(taskName, OngoingTaskType.RavenEtl));
        return result?.TaskId;
    }
}
