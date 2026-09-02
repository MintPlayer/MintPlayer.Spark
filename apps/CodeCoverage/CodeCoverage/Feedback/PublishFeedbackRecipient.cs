using CodeCoverage.Entities;
using CodeCoverage.Ingestion;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Feedback;

/// <summary>
/// Posts the two published check-runs — <c>coverage/project</c> and
/// <c>coverage/patch</c> (names are a compatibility promise in
/// docs/upload-api.md) — for a finalized build. The outbox on the Build makes
/// this idempotent: stored check-run ids turn a re-finalize into an update,
/// and failures schedule bounded retries via <see cref="PublishFeedbackCronJob"/>.
/// A repo without an App installation is recorded as Unavailable, quietly —
/// OIDC-only repos are a supported population, not an error.
/// </summary>
public partial class PublishFeedbackRecipient : IRecipient<PublishFeedbackMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IBaseResolver baseResolver;
    [Inject] private readonly IGitHubInstallationService installationService;
    [Inject] private readonly IGitHubContentService contentService;
    [Inject] private readonly ILogger<PublishFeedbackRecipient> logger;

    private const int MaxAttempts = 5;

    public async Task HandleAsync(PublishFeedbackMessage message, CancellationToken cancellationToken = default)
    {
        var build = await session.LoadAsync<Build>(message.BuildId, cancellationToken);
        if (build is null || build.Status != "Finalized" || build.Commit is null)
            return;

        var commit = await session.LoadAsync<Entities.Commit>(build.Commit, cancellationToken);
        var repository = commit?.Repository is null ? null : await session.LoadAsync<Entities.Repository>(commit.Repository, cancellationToken);
        if (commit is null || repository is null)
            return;

        var feedback = build.Feedback ??= new BuildFeedback();

        long? installationId = null;
        if (repository.Account is not null)
            installationId = (await session.LoadAsync<Entities.Account>(repository.Account, cancellationToken))?.InstallationId;
        if (installationId is null)
        {
            feedback.State = "Unavailable";
            feedback.Error = "No GitHub App installation for this repository.";
            feedback.NextAttemptAtUtc = null;
            await SyncAndSave(build, feedback, cancellationToken);
            return;
        }

        var comparison = await BuildComparer.CompareAsync(session, baseResolver, repository, build, commit, cancellationToken);

        // Policy from the base ref, so a PR can't rewrite the gate judging it.
        var ymlRef = comparison.Base.ResolvedSha ?? repository.DefaultBranch;
        var yml = ymlRef is null ? null
            : await contentService.GetFileContentAsync(repository, installationId, ymlRef, CoverageYml.FileName, cancellationToken);
        var gate = CoverageYml.Merge(repository.Gate ?? new GateSettings(), yml, out var ymlError);
        build.GateSnapshot = gate;

        var assembly = build.Commit is null ? null : await session.LoadAsync<CommitAssembly>(CommitAssembly.DocumentId(build.Commit), cancellationToken);
        var project = GateEvaluator.Project(gate, build, comparison, assembly);
        var patch = GateEvaluator.Patch(gate, build);

        try
        {
            var client = await installationService.CreateInstallationClientAsync(installationId.Value);
            feedback.ProjectCheckRunId = await PostAsync(client, repository, commit.Sha, "coverage/project", project, feedback.ProjectCheckRunId);
            feedback.PatchCheckRunId = await PostAsync(client, repository, commit.Sha, "coverage/patch", patch, feedback.PatchCheckRunId);

            feedback.State = "Posted";
            feedback.Error = ymlError;
            feedback.NextAttemptAtUtc = null;
            logger.LogInformation("Posted check-runs for {BuildId}: project={Project}, patch={Patch}", build.Id, project.Conclusion, patch.Conclusion);
        }
        catch (Exception ex)
        {
            feedback.Attempts++;
            feedback.Error = ex.Message;
            if (feedback.Attempts >= MaxAttempts)
            {
                feedback.State = "Failed";
                feedback.NextAttemptAtUtc = null;
                logger.LogWarning(ex, "Giving up on check-runs for {BuildId} after {Attempts} attempts", build.Id, feedback.Attempts);
            }
            else
            {
                feedback.State = "Retry";
                feedback.NextAttemptAtUtc = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Pow(2, feedback.Attempts));
                logger.LogWarning(ex, "Check-run post failed for {BuildId}; retry {Attempts}/{Max} at {Next}", build.Id, feedback.Attempts, MaxAttempts, feedback.NextAttemptAtUtc);
            }
        }

        await SyncAndSave(build, feedback, cancellationToken);
    }

    private async Task SyncAndSave(Build build, BuildFeedback feedback, CancellationToken cancellationToken)
    {
        build.FeedbackState = feedback.State;
        build.FeedbackNextAttemptAtUtc = feedback.NextAttemptAtUtc;
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task<long> PostAsync(IGitHubClient client, Entities.Repository repository, string sha, string name, CheckVerdict verdict, long? existingId)
    {
        var conclusion = verdict.Conclusion switch
        {
            "success" => CheckConclusion.Success,
            "failure" => CheckConclusion.Failure,
            _ => CheckConclusion.Neutral,
        };
        var output = new NewCheckRunOutput(verdict.Title, verdict.Summary);

        if (existingId is { } id)
        {
            await client.Check.Run.Update(repository.OwnerLogin, repository.Name, id, new CheckRunUpdate
            {
                Status = CheckStatus.Completed,
                Conclusion = conclusion,
                Output = output,
            });
            return id;
        }

        var created = await client.Check.Run.Create(repository.OwnerLogin, repository.Name, new NewCheckRun(name, sha)
        {
            Status = CheckStatus.Completed,
            Conclusion = conclusion,
            Output = output,
        });
        return created.Id;
    }
}
