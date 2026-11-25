using MintPlayer.Spark;
using System.ComponentModel;
using System.Security.Claims;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Apps;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Webhooks;

/// <summary>Line and branch totals.</summary>
public sealed record CoverageNumbers(int LinesCovered, int LinesCoverable, int BranchesCovered, int BranchesTotal, int Files);

/// <summary>Added-lines coverage against the diff base.</summary>
public sealed record PatchNumbers(int LinesCovered, int LinesCoverable, [property: Description("The base the diff was taken against.")] string? Base);

/// <summary>One report file of an upload, as it was read.</summary>
public sealed record CoverageReportFile(
    string File,
    bool Parsed,
    [property: Description("Cobertura, JaCoCo or LCOV; null when no parser recognised it.")] string? Format,
    int Files,
    [property: Description("Why it was rejected: empty, unrecognizedFormat, malformed, truncated, tooLarge, noFiles or missing; null when parsed.")] string? Rejection,
    string? Detail);

/// <summary>
/// #691 D41: one upload (a CI job's reports) was read. A build collects the uploads of one CI run; it completes later, as
/// <see cref="CoverageBuildCompleted"/>.
/// </summary>
[WebhookEvent("coverage.report.processed", Permission = "coverage:read", Description = "An uploaded coverage report was read: which files parsed, and why the others were rejected.")]
public sealed record CoverageReportProcessed(
    [property: Description("owner/name on the forge.")] string Repository,
    [property: Description("github, gitlab or bitbucket.")] string Forge,
    string Sha,
    string? Branch,
    int? PullRequest,
    [property: Description("The CI run, as runId.attempt.")] string Run,
    string? Workflow,
    [property: Description("The upload within the build.")] string Session,
    string? Job,
    IReadOnlyList<string> Flags,
    [property: Description("Parsed when at least one report was read (or none was sent), Failed when every one was rejected.")] string Status,
    string? Error,
    [property: Description("The source files the upload measured.")] int Files,
    IReadOnlyList<CoverageReportFile> Reports,
    DateTimeOffset UploadedAt);

/// <summary>
/// #691 M9 (<c>docs/apps_webhooks_PRD.md</c> D34, renamed in D41): a CI run's coverage build was closed. It is sent to the
/// webhooks of the repository's forge account, the people who manage that account on its forge.
/// </summary>
[WebhookEvent("coverage.build.completed", Permission = "coverage:read", Description = "A CI run's coverage build was closed and its totals computed.")]
public sealed record CoverageBuildCompleted(
    [property: Description("owner/name on the forge.")] string Repository,
    [property: Description("github, gitlab or bitbucket.")] string Forge,
    string Sha,
    string? Branch,
    int? PullRequest,
    [property: Description("The CI run, as runId.attempt.")] string Run,
    string? Workflow,
    [property: Description("Complete, or CompleteWithErrors when a report could not be read.")] string State,
    [property: Description("Explicit, Debounce or Timeout.")] string Reason,
    [property: Description("The run measured only part of the workspace.")] bool Partial,
    CoverageNumbers Coverage,
    PatchNumbers? Patch,
    DateTimeOffset CompletedAt);

/// <summary>
/// CodeCoverage's webhooks (D34): off unless <c>CodeCoverage:Webhooks:Enabled</c> is true. Owners are forge accounts
/// (kind <see cref="AccountKind"/>, key <c>provider:login</c>), managed by whoever the forge says manages the account.
/// </summary>
public static class CoverageWebhooks
{
    public const string EnabledSetting = "CodeCoverage:Webhooks:Enabled";
    public const string AccountKind = "account";

    public static WebhookOwner Account(string ownerKey) => new(AccountKind, ownerKey);

    /// <summary>
    /// Direct webhooks only: Apps and installations (D5) stay off here until the dogfood has run. Every feature's events are
    /// listed here: the application is one deployable (D41). Each record lives beside the feature that raises it.
    /// </summary>
    public static ISparkBuilder AddCoverageWebhooks(this ISparkBuilder spark, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>(EnabledSetting))
            return spark;
        return spark.AddApps(apps => apps
            .AddEvent<CoverageReportProcessed>()
            .AddEvent<CoverageBuildCompleted>()
            .AddEvent<MergeQueue.MergeQueueEntryQueued>()
            .AddEvent<MergeQueue.MergeQueueEntryMerged>()
            .AddEvent<MergeQueue.MergeQueueEntryRemoved>()
            .AddEvent<Dependencies.DependencyPullRequestOpened>()
            .AddEvent<Dependencies.DependencyRunCompleted>()
            .AddEvent<PackageVersioning.VersioningRunResult>()
            .AddEvent<PackageVersioning.BackportAttempted>()
            .AddEvent<PackageVersioning.BackportMerged>()
            .AddOwnerProvider<AccountWebhookOwnerProvider>()
            .ConfigureOptions(o => o.CatalogTitle = "CodeCoverage webhooks"));
    }

    internal static CoverageReportProcessed DescribeReport(Build build, BuildSession upload, Commit commit, Repository repository) => new(
        repository.FullName,
        repository.Provider.ToString().ToLowerInvariant(),
        commit.Sha,
        commit.Branch,
        commit.PullRequestNumber,
        build.Run,
        build.WorkflowName,
        upload.SessionId,
        upload.JobName,
        upload.Flags,
        upload.ParseStatus,
        upload.Error,
        upload.FilesCount,
        [.. upload.Reports.Select(r => new CoverageReportFile(r.FileName, r.Parsed, r.Format, r.FilesCount, r.Reason, r.Detail))],
        new DateTimeOffset(DateTime.SpecifyKind(upload.UploadedAtUtc, DateTimeKind.Utc)));

    /// <summary>Publishes <see cref="CoverageReportProcessed"/> in <paramref name="session"/>, keyed by the upload: a re-parse sends nothing twice.</summary>
    internal static async Task PublishReportAsync(CoverageWebhookPublisher webhooks, IAsyncDocumentSession session, Build build, BuildSession upload, CancellationToken cancellationToken)
    {
        if (!webhooks.Enabled) return;
        if (await session.LoadAsync<Commit>(build.Commit, cancellationToken) is not { } commit
            || await session.LoadAsync<Repository>(commit.Repository, cancellationToken) is not { } repository)
            return;
        await webhooks.PublishAsync(DescribeReport(build, upload, commit, repository), repository, $"{build.Id}/{upload.SessionId}", session, cancellationToken);
    }

    internal static CoverageBuildCompleted Describe(Build build, Commit commit, Repository repository)
    {
        var coverage = build.Coverage ?? new CoverageSummary();
        return new CoverageBuildCompleted(
            repository.FullName,
            repository.Provider.ToString().ToLowerInvariant(),
            commit.Sha,
            commit.Branch,
            commit.PullRequestNumber,
            build.Run,
            build.WorkflowName,
            Build.ClassifyState(build),
            build.FinalizeReason ?? "Explicit",
            build.Partial,
            new CoverageNumbers(coverage.LinesCovered, coverage.LinesCoverable, coverage.BranchesCovered, coverage.BranchesTotal, coverage.FilesCount),
            build.Patch is { } patch ? new PatchNumbers(patch.LinesCovered, patch.LinesCoverable, patch.DiffBaseRef) : null,
            new DateTimeOffset(DateTime.SpecifyKind(build.FinalizedAtUtc ?? DateTime.UtcNow, DateTimeKind.Utc)));
    }
}

/// <summary>
/// #691 D41: the features' way to raise a webhook event for a repository's forge account. It publishes in the caller's
/// session, before that session saves the state change the event reports, so the event goes out only if the change commits.
/// A no-op while webhooks are off. The key makes a re-run (an at-least-once message handler) send nothing twice: it names
/// the state change, e.g. a run and its outcome.
/// </summary>
/// <remarks>
/// Costs one registry load per publish, and nothing more when the account has no webhook. Not for a session that saves many
/// repositories' changes at once (the finalize cron): that queues a message instead (<see cref="CoverageWebhookQueue"/>).
/// </remarks>
[Register(ServiceLifetime.Scoped)]
public sealed partial class CoverageWebhookPublisher
{
    [Inject] private readonly IServiceProvider services;

    /// <summary>Whether webhooks are on: a caller that would load documents only to describe an event checks this first.</summary>
    public bool Enabled => services.GetService<IWebhookPublisher>() is not null;

    public async Task PublishAsync<TEvent>(TEvent data, Repository repository, string key, IAsyncDocumentSession session, CancellationToken cancellationToken)
        where TEvent : notnull
    {
        if (services.GetService<IWebhookPublisher>() is not { } webhooks || repository.OwnerLogin is not { Length: > 0 })
            return;
        await webhooks.PublishAsync(data, CoverageWebhooks.Account(repository.OwnerKey), key, session, cancellationToken);
    }
}

/// <summary>A build finalized while webhooks were on: <see cref="CoverageBuildCompletedRecipient"/> publishes the event.</summary>
[MessageQueue(Feedback.CoverageQueues.Publishing)]
public record CoverageBuildCompletedWebhookMessage
{
    public required string BuildId { get; init; }
}

/// <summary>
/// Queues the webhook of a finalize in the finalize's own session. Only a message is stored: the finalize cron closes up
/// to 128 builds in one session, which has no request budget left for each build's repository and owner registry.
/// </summary>
[Register(ServiceLifetime.Scoped)]
public sealed partial class CoverageWebhookQueue
{
    [Inject] private readonly IServiceProvider services;

    /// <summary>A no-op while webhooks are off. Stored in <paramref name="session"/>: it goes out only if the finalize commits.</summary>
    public Task EnqueueCompletedAsync(IAsyncDocumentSession session, Build build, CancellationToken cancellationToken)
        => build.Id is { } id && services.GetService<IWebhookPublisher>() is not null
            ? services.GetRequiredService<MintPlayer.Spark.Messaging.IMessageOutbox>().EnqueueAsync(session.AsSparkSession(), new CoverageBuildCompletedWebhookMessage { BuildId = id.Value }, cancellationToken: cancellationToken)
            : Task.CompletedTask;
}

/// <summary>
/// Publishes <c>coverage.build.completed</c> to the repository's account. The <c>webhook-id</c> derives from the build, so a
/// redelivered message sends nothing twice.
/// </summary>
public sealed partial class CoverageBuildCompletedRecipient : IRecipient<CoverageBuildCompletedWebhookMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IServiceProvider services;

    public async Task HandleAsync(CoverageBuildCompletedWebhookMessage message, CancellationToken cancellationToken = default)
    {
        // Webhooks were switched off after the finalize queued this: nothing to send.
        if (services.GetService<IWebhookPublisher>() is not { } webhooks)
            return;
        var build = await session.Include<Build>(b => b.Commit).LoadAsync<Build>(message.BuildId, cancellationToken);
        if (build?.Commit is null || build.Status != "Finalized")
            return;
        var commit = await session.Include<Commit>(c => c.Repository).LoadAsync<Commit>(build.Commit, cancellationToken);
        if (commit?.Repository is null || await session.LoadAsync<Repository>(commit.Repository, cancellationToken) is not { } repository)
            return;

        await webhooks.PublishAsync(CoverageWebhooks.Describe(build, commit, repository), CoverageWebhooks.Account(repository.OwnerKey), build.Id.Value, session, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// A forge account owns webhooks; its managers (<see cref="ISparkVisibility.CanManageOwnerAsync"/>, the forge's answer) manage
/// them. Visibility answers for the signed-in user of this request only, so a question about anyone else is refused.
/// </summary>
internal sealed partial class AccountWebhookOwnerProvider : IWebhookOwnerProvider
{
    [Inject] private readonly ISparkVisibility visibility;
    [Inject] private readonly IHttpContextAccessor http;

    public string Kind => CoverageWebhooks.AccountKind;

    public async Task<bool> CanManageAsync(string key, string userId, CancellationToken cancellationToken)
        => IsCurrentUser(userId) && ForgeOwner.TryParse(key, out _) && await visibility.CanManageOwnerAsync(key);

    public async Task<IReadOnlyList<WebhookOwnerDescription>> ListManageableAsync(string userId, CancellationToken cancellationToken)
    {
        if (!IsCurrentUser(userId))
            return [];
        return (await visibility.GetManagedOwnersAsync())
            .Select(key => ForgeOwner.TryParse(key, out var owner) && owner is { } o
                ? new WebhookOwnerDescription(Kind, key, $"{o.Login} ({o.Provider})")
                : null)
            .OfType<WebhookOwnerDescription>()
            .OrderBy(o => o.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private bool IsCurrentUser(string userId)
        => http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } current
           && string.Equals(current, userId, StringComparison.Ordinal);
}
