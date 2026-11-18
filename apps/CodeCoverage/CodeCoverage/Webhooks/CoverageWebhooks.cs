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

/// <summary>
/// #691 M9 (<c>docs/apps_webhooks_PRD.md</c> D34): a CI run's coverage build was closed. It is sent to the webhooks of the
/// repository's forge account, the people who manage that account on its forge.
/// </summary>
[WebhookEvent("coverage.build.finalized", Permission = "coverage:read", Description = "A CI run's coverage build was closed and its totals computed.")]
public sealed record CoverageBuildFinalized(
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
    DateTimeOffset FinalizedAt);

/// <summary>
/// CodeCoverage's webhooks (D34): off unless <c>CodeCoverage:Webhooks:Enabled</c> is true. Owners are forge accounts
/// (kind <see cref="AccountKind"/>, key <c>provider:login</c>), managed by whoever the forge says manages the account.
/// </summary>
public static class CoverageWebhooks
{
    public const string EnabledSetting = "CodeCoverage:Webhooks:Enabled";
    public const string AccountKind = "account";

    public static WebhookOwner Account(string ownerKey) => new(AccountKind, ownerKey);

    /// <summary>Direct webhooks only: Apps and installations (D5) stay off here until the dogfood has run.</summary>
    public static ISparkBuilder AddCoverageWebhooks(this ISparkBuilder spark, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>(EnabledSetting))
            return spark;
        return spark.AddApps(apps => apps
            .AddEvent<CoverageBuildFinalized>()
            .AddOwnerProvider<AccountWebhookOwnerProvider>()
            .ConfigureOptions(o => o.CatalogTitle = "CodeCoverage webhooks"));
    }

    internal static CoverageBuildFinalized Describe(Build build, Commit commit, Repository repository)
    {
        var coverage = build.Coverage ?? new CoverageSummary();
        return new CoverageBuildFinalized(
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

/// <summary>A build finalized while webhooks were on: <see cref="CoverageBuildFinalizedRecipient"/> publishes the event.</summary>
[MessageQueue(Feedback.CoverageQueues.Publishing)]
public record CoverageBuildFinalizedWebhookMessage
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
    public Task EnqueueFinalizedAsync(IAsyncDocumentSession session, Build build, CancellationToken cancellationToken)
        => build.Id is { } id && services.GetService<IWebhookPublisher>() is not null
            ? services.GetRequiredService<MintPlayer.Spark.Messaging.IMessageOutbox>().EnqueueAsync(session, new CoverageBuildFinalizedWebhookMessage { BuildId = id }, cancellationToken: cancellationToken)
            : Task.CompletedTask;
}

/// <summary>
/// Publishes <c>coverage.build.finalized</c> to the repository's account. The <c>webhook-id</c> derives from the build, so a
/// redelivered message sends nothing twice.
/// </summary>
public sealed partial class CoverageBuildFinalizedRecipient : IRecipient<CoverageBuildFinalizedWebhookMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IServiceProvider services;

    public async Task HandleAsync(CoverageBuildFinalizedWebhookMessage message, CancellationToken cancellationToken = default)
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

        await webhooks.PublishAsync(CoverageWebhooks.Describe(build, commit, repository), CoverageWebhooks.Account(repository.OwnerKey), build.Id!, session, cancellationToken);
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
