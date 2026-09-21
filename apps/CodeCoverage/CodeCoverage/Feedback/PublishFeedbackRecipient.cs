using CodeCoverage.Badges;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Ingestion;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Services;
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
    [Inject] private readonly IForgeIntegrationResolver forges;

    [Inject] private readonly IConfiguration configuration;
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

        // Selected from the repository, not chosen here: this method publishes to whichever
        // forge owns the repository and never learns which one that is.
        var forge = forges.For(repository);

        // The forge supplies the reason, so this stays true whichever provider the repository is on
        // — the message still names the App for a GitHub repository, without this method knowing
        // that GitHub is what it is talking to.
        var access = await forge.CheckAccessAsync(repository, cancellationToken);
        if (!access.Available)
        {
            feedback.State = "Unavailable";
            feedback.Error = access.UnavailableReason;
            feedback.NextAttemptAtUtc = null;
            await SyncAndSave(build, feedback, cancellationToken);
            return;
        }

        var comparison = await BuildComparer.CompareAsync(session, baseResolver, repository, build, commit, cancellationToken);

        // Policy from the base ref, so a PR can't rewrite the gate judging it.
        var ymlRef = comparison.Base.ResolvedSha ?? repository.DefaultBranch;
        var yml = ymlRef is null ? null
            : await forge.GetFileContentAsync(repository, ymlRef, CoverageYml.FileName, cancellationToken);
        var gate = CoverageYml.Merge(repository.Gate ?? new GateSettings(), yml, out var ymlError);
        build.GateSnapshot = gate;

        var assembly = build.Commit is null ? null : await session.LoadAsync<CommitAssembly>(CommitAssembly.DocumentId(build.Commit), cancellationToken);
        var project = GateEvaluator.Project(gate, build, comparison, assembly);
        var patch = GateEvaluator.Patch(gate, build);

        try
        {
            feedback.ProjectCheckRunId = await forge.PublishStatusAsync(
                repository, commit.Sha, "coverage/project",
                ToVerdict(project, commit.ContributedFromFork), feedback.ProjectCheckRunId, cancellationToken);
            feedback.PatchCheckRunId = await forge.PublishStatusAsync(
                repository, commit.Sha, "coverage/patch",
                ToVerdict(patch, commit.ContributedFromFork), feedback.PatchCheckRunId, cancellationToken);

            feedback.State = "Posted";
            feedback.Error = ymlError;
            feedback.NextAttemptAtUtc = null;
            logger.LogInformation("Posted check-runs for {BuildId}: project={Project}, patch={Patch}", build.Id, project.Conclusion, patch.Conclusion);
        }
        catch (ForgeAccessDeniedException ex)
        {
            // A permission the forge will not grant: an installation that has not accepted the
            // raised permissions cannot be helped by retrying.
            feedback.State = "Unavailable";
            feedback.Error = ex.Message;
            feedback.NextAttemptAtUtc = null;
            logger.LogInformation("Check-runs unavailable for {BuildId}: {Message}", build.Id, ex.Message);
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

        // After the check-runs and their save, so a comment failure can never
        // lose the record of a successful check-run publish. The publisher does
        // not throw; it records its own outbox state.
        if (commit.PullRequestNumber is { } pullRequestNumber)
        {
            // ⚠️ The signature is for a PRIVATE repository's badge, and a fork upload can only ever
            // target a public one — so this stays keyed on IsPrivate and needs no fork arm. If fork
            // uploads ever reach private repositories, this is a place that must be revisited:
            // minting a badge signature on an anonymous contributor's behalf would publish a
            // capability for a repository they cannot otherwise see.
            var body = PullRequestCommentRenderer.Render(
                repository, commit, project, patch, assembly,
                configuration["Coverage:BaseUrl"],
                repository.IsPrivate
                    ? BadgePrSignature.Compute(configuration, repository.GitHubId, pullRequestNumber)
                    : null);

            await forge.PublishCommentAsync(repository, pullRequestNumber, commit.Sha, body, cancellationToken);
        }
    }

    private async Task SyncAndSave(Build build, BuildFeedback feedback, CancellationToken cancellationToken)
    {
        build.FeedbackState = feedback.State;
        build.FeedbackNextAttemptAtUtc = feedback.NextAttemptAtUtc;
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Maps the gate's own verdict onto the provider-neutral one.
    /// </summary>
    /// <remarks>
    /// The gate speaks in strings because that is what it stores; anything that is neither
    /// "success" nor "failure" is a genuine absence of judgement and becomes
    /// <see cref="EForgeOutcome.Neutral"/>. That default is deliberate and must not become
    /// <see cref="EForgeOutcome.Success"/>: a gate that could not evaluate has not passed.
    /// </remarks>
    /// <summary>
    /// Maps a gate conclusion onto the forge's vocabulary.
    /// </summary>
    /// <param name="contributedFromFork">
    /// When true the outcome is forced to <see cref="EForgeOutcome.Neutral"/> (D20).
    /// </param>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>Fork coverage never fails a check.</b> The number was produced by a workflow the base
    /// repository's maintainers do not control, on a runner they do not own, from code under
    /// review. Letting it turn a check red would let any contributor mark a pull request as failing
    /// — and letting it turn green would be a claim we cannot stand behind either. Neutral says what
    /// is actually true: here is a measurement, it is not a judgement.
    /// </para>
    /// <para>
    /// The title carries the provenance too, because a check-run list shows titles and not bodies,
    /// and a reviewer skimming it should not have to know that neutral means "from a fork".
    /// </para>
    /// </remarks>
    internal static ForgeVerdict ToVerdict(CheckVerdict verdict, bool contributedFromFork) => new(
        contributedFromFork
            ? EForgeOutcome.Neutral
            : verdict.Conclusion switch
            {
                "success" => EForgeOutcome.Success,
                "failure" => EForgeOutcome.Failure,
                _ => EForgeOutcome.Neutral,
            },
        contributedFromFork ? $"{verdict.Title} (from a fork)" : verdict.Title,
        contributedFromFork
            ? verdict.Summary + ForkSummarySuffix
            : verdict.Summary);

    /// <summary>
    /// Appended to a fork build's check-run summary, so the neutral conclusion is explained where
    /// it is read rather than only in documentation.
    /// </summary>
    private const string ForkSummarySuffix =
        "\n\n---\n⚠️ This coverage was contributed from a fork, by a workflow this repository does "
        + "not control. It is reported for information and never gates the pull request, and it does "
        + "not affect the repository's own coverage or its badge.";
}
