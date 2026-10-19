using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Licensing;
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
/// What the owner deploys for one consumer. Every part of it is the owner's own: the destination from the
/// consumer's registry entry (F19), the transforms from the owner's contracts (F16).
/// </summary>
internal sealed record EtlDeployment(
    string RequestingModule,
    string TargetDatabase,
    string[] TargetUrls,
    IReadOnlyList<EtlTransformScript> Transforms);

/// <summary>How an ETL task change ended.</summary>
internal sealed record EtlTaskResult(bool Success, string? Error = null, int Created = 0, int Updated = 0, int Removed = 0)
{
    /// <summary><see cref="Error"/> when the licence has no ETL (Community, F18).</summary>
    public const string NotLicensedError = "ETL_NOT_LICENSED";
}

internal interface IEtlTaskManager
{
    /// <summary>Creates or updates the consumer's ETL task: one per consumer per owner. Idempotent.</summary>
    Task<EtlTaskResult> DeployAsync(EtlDeployment deployment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the consumer's ETL task and its connection string, when there is one (F18: a withdrawn subscription or a
    /// revoked grant must stop the data flow). Idempotent; a licence without ETL has nothing to remove.
    /// </summary>
    Task<EtlTaskResult> RemoveAsync(string consumerModule, CancellationToken cancellationToken = default);
}

/// <summary>Creates, updates and removes the RavenDB ETL tasks that deliver contracts to consumers.</summary>
internal partial class EtlTaskManager : IEtlTaskManager
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ILogger<EtlTaskManager> logger;
    [Inject] private readonly ISparkLicenseCapabilities licence;

    internal static string TaskName(string module) => $"spark-etl-{module}";

    public async Task<EtlTaskResult> DeployAsync(EtlDeployment deployment, CancellationToken cancellationToken = default)
    {
        var name = TaskName(deployment.RequestingModule);

        try
        {
            // Source and target the same database would loop for ever (each load re-triggers the source). The target is
            // the consumer's registry entry, so the two meet only when that entry names this module's own database.
            if (string.Equals(deployment.TargetDatabase, documentStore.Database, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Refusing self-loop ETL deployment for module '{Module}' (target db = local db '{Db}')",
                    deployment.RequestingModule, documentStore.Database);
                return new EtlTaskResult(false, $"self-loop refused: source and target are both '{documentStore.Database}'");
            }

            // F18: no ETL in the licence is said as such, not as a generic failure retried for ever.
            if ((await licence.GetAsync(cancellationToken))?.HasRavenEtl == false)
                return NotLicensed(deployment.RequestingModule);

            documentStore.Maintenance.Send(new PutConnectionStringOperation<RavenConnectionString>(new RavenConnectionString
            {
                Name = name,
                Database = deployment.TargetDatabase,
                TopologyDiscoveryUrls = deployment.TargetUrls,
            }));

            var configuration = new RavenEtlConfiguration
            {
                ConnectionStringName = name,
                Name = name,
                Transforms = deployment.Transforms.Select(transform => new Transformation
                {
                    Name = transform.Name,
                    Collections = { transform.SourceCollection },
                    Script = transform.Script,
                }).ToList(),
            };

            if (FindExistingEtlTaskId(name) is { } existing)
            {
                documentStore.Maintenance.Send(new UpdateEtlOperation<RavenConnectionString>(existing, configuration));
                logger.LogInformation("Updated ETL task '{TaskName}' with {Count} transformation(s) for module '{Module}'",
                    name, configuration.Transforms.Count, deployment.RequestingModule);
                return new EtlTaskResult(true, Updated: 1);
            }

            documentStore.Maintenance.Send(new AddEtlOperation<RavenConnectionString>(configuration));
            logger.LogInformation("Created ETL task '{TaskName}' with {Count} transformation(s) for module '{Module}'",
                name, configuration.Transforms.Count, deployment.RequestingModule);
            return new EtlTaskResult(true, Created: 1);
        }
        catch (LicenseLimitException ex)
        {
            logger.LogError(ex, "ETL deployment for module '{Module}' refused by the RavenDB licence", deployment.RequestingModule);
            return NotLicensed(deployment.RequestingModule);
        }
        catch (Exception ex)
        {
            // R2-L6: the full exception stays in the log; maintenance exceptions carry connection-string detail.
            logger.LogError(ex, "Failed to deploy ETL transforms for module '{Module}'", deployment.RequestingModule);
            return new EtlTaskResult(false, "ETL_DEPLOY_FAILED");
        }
    }

    public async Task<EtlTaskResult> RemoveAsync(string consumerModule, CancellationToken cancellationToken = default)
    {
        var name = TaskName(consumerModule);
        try
        {
            if ((await licence.GetAsync(cancellationToken))?.HasRavenEtl == false)
                return new EtlTaskResult(true);

            var removed = 0;
            if (FindExistingEtlTaskId(name) is { } existing)
            {
                documentStore.Maintenance.Send(new DeleteOngoingTaskOperation(existing, OngoingTaskType.RavenEtl));
                removed = 1;
                logger.LogInformation("Removed ETL task '{TaskName}' (module '{Module}' no longer subscribed)", name, consumerModule);
            }

            var strings = documentStore.Maintenance.Send(new GetConnectionStringsOperation(name, ConnectionStringType.Raven));
            if (strings?.RavenConnectionStrings?.ContainsKey(name) == true)
                documentStore.Maintenance.Send(new RemoveConnectionStringOperation<RavenConnectionString>(strings.RavenConnectionStrings[name]));

            return new EtlTaskResult(true, Removed: removed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove the ETL task for module '{Module}'", consumerModule);
            return new EtlTaskResult(false, "ETL_REMOVE_FAILED");
        }
    }

    private EtlTaskResult NotLicensed(string module)
    {
        logger.LogError(
            "ETL deployment for module '{Module}' refused: this RavenDB licence has no ETL (Community). " +
            "Set Spark:Replication:Transport to 'Http' or 'Auto', or use a licence with RavenDB ETL.", module);
        return new EtlTaskResult(false, EtlTaskResult.NotLicensedError);
    }

    /// <summary>
    /// The task id of the ETL task named <paramref name="taskName"/>, or <c>null</c>. A missing task is a <c>null</c>
    /// result, not an exception, so nothing is caught here: a transient failure must not read as "no task".
    /// </summary>
    private long? FindExistingEtlTaskId(string taskName)
        => documentStore.Maintenance.Send(new GetOngoingTaskInfoOperation(taskName, OngoingTaskType.RavenEtl))?.TaskId;
}
