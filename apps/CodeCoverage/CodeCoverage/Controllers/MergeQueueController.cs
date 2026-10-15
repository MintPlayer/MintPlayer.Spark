using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using MintPlayer.Spark.Endpoints;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Services;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents.Session;
using CoverageYml = CodeCoverage.Feedback.CoverageYml;
using MergeQueueYml = CodeCoverage.MergeQueue.MergeQueueYml;
using QueueDocument = CodeCoverage.Entities.MergeQueue;

namespace CodeCoverage.Controllers;

/// <summary>
/// The repository page's merge-queue panel (merge-queue PRD M6, O8, O15): the queue's state, read like every other browse
/// endpoint, and the enqueue/dequeue button.
/// </summary>
/// <remarks>
/// <para>
/// <b>The button is a third way to send a command, not a third way to authorise one.</b> It raises the same neutral
/// <see cref="MergeQueueCommandIssued"/> a label or a comment raises. The actor is the signed-in user's own forge login, as
/// the forge returned it for the user's own credential (<see cref="IForgeAccessService.GetViewerAccountAsync"/>, T1), and
/// never anything from the request. The engine then runs the same O9 role check it runs for every command. The right on
/// these actions only says who may press a button at all.
/// </para>
/// <para>
/// <b>Existence (#453).</b> Both halves resolve the repository through <see cref="IRepositoryScope"/> and refuse exactly as
/// the browse API does: 401 for an anonymous caller, 404 for a signed-in one, the same body whatever the reason.
/// </para>
/// </remarks>
[ApiController]
public partial class MergeQueueController : ControllerBase
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IRepositoryScope scope;
    [Inject] private readonly IForgeIntegrationResolver forges;
    [Inject] private readonly IEnumerable<IForgeAccessService> accounts;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly IMemoryCache cache;
    [Inject] private readonly ILogger<MergeQueueController> logger;

    /// <summary>How long the viewer's repository role is remembered for the panel's buttons.</summary>
    private static readonly TimeSpan RoleCacheDuration = TimeSpan.FromMinutes(1);

    public sealed record MergeQueueView(bool Supported, IReadOnlyList<LaneView> Lanes, ConfigView? Config, ViewerView Viewer);

    /// <summary>One base branch: the entry being worked on, the ones waiting in order, and how the latest runs ended.</summary>
    public sealed record LaneView(string BaseBranch, bool Paused, EntryView? Current, IReadOnlyList<EntryView> Waiting, IReadOnlyList<OutcomeView> Recent);

    /// <param name="Step">The last thing the queue did for this entry, as its check run says it.</param>
    /// <param name="SinceUtc">When the entry entered its current state.</param>
    /// <param name="RunUrl">The regenerate workflow run, while there is one.</param>
    public sealed record EntryView(
        int Number, string? Title, string? AuthorLogin, string State, string? Step, DateTime SinceUtc, DateTime EnqueuedAtUtc,
        string EnqueuedBy, string EnqueuedVia, int Run, int? Position, string? RunUrl);

    public sealed record OutcomeView(
        int Number, string? Title, int Run, string State, string? Reason, string? Message, string? EnqueuedBy, string? MergeCommitSha, DateTime FinishedAtUtc);

    /// <summary>The effective configuration (O15) as the run of #<paramref name="Number"/> read it, each value with its source.</summary>
    public sealed record ConfigView(int Number, int Run, string? ConfigRef, IReadOnlyList<ConfigValueView> Values);

    public sealed record ConfigValueView(string Key, string Value, string Source);

    /// <param name="Linked">The viewer's account has a sign-in with the repository's forge.</param>
    /// <param name="Login">The forge login the button would act as; null when there is none (yet).</param>
    /// <param name="Role">The viewer's role on the repository (<c>none</c> … <c>admin</c>), or null when it could not be read.
    /// Only decides which buttons the panel offers; the engine checks it again for every command (O9).</param>
    /// <param name="Credential">The viewer's forge credential: <c>Ok</c>, <c>ReauthRequired</c> or <c>Unavailable</c>.</param>
    public sealed record ViewerView(bool SignedIn, bool Linked, string? Login, string? Role, string? Credential);

    public sealed record CommandAccepted(int Number, string Command, string Actor);

    public sealed record CommandRefused(string Error);

    /// <summary>The pull requests the panel's dropdown offers to queue, newest first.</summary>
    /// <param name="Unavailable">Why the list is empty although it should not be (the queue is off, coverage.yml is broken,
    /// the forge could not be read); null when it is simply empty or not empty.</param>
    public sealed record QueueCandidates(IReadOnlyList<QueueCandidate> PullRequests, string? Unavailable);

    public sealed record QueueCandidate(int Number, string Title, string BaseBranch, string? AuthorLogin);

    /// <summary>How long the forge half of the candidates (coverage.yml and the open pull requests) is remembered.</summary>
    private static readonly TimeSpan CandidatesCacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>The forge half of <see cref="Candidates"/>, the part that is cached: open, non-draft pull requests on the configured branches.</summary>
    private sealed record OpenCandidates(IReadOnlyList<ForgeOpenPullRequest> PullRequests, string? Unavailable);

    /// <summary>The queue of one repository, for anyone who may browse the repository.</summary>
    [HttpGet("api/browse/repos/{provider}/{owner}/{name}/merge-queue")]
    [EnableRateLimiting("browse")]
    [SparkAuthorize("Browse", "Coverage")]
    public async Task<ActionResult<MergeQueueView>> Get(string provider, string owner, string name, CancellationToken cancellationToken)
    {
        var repository = await scope.ResolveAsync(provider, owner, name, cancellationToken);
        if (repository is null) return Refuse();

        var queue = queues.FirstOrDefault(q => q.Provider == repository.Provider);
        if (queue is null) return Ok(new MergeQueueView(false, [], null, await ViewerAsync(repository, null, cancellationToken)));

        var document = await session.LoadAsync<QueueDocument>(QueueDocument.DocumentId(repository.Id!), cancellationToken);
        var lanes = document?.Lanes ?? [];
        var recent = document?.Recent ?? [];

        var ids = lanes.SelectMany(l => l.Waiting.Prepend(l.Current)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var entries = ids.Count == 0
            ? new Dictionary<string, MergeQueueEntry>()
            : await session.LoadAsync<MergeQueueEntry>(ids, cancellationToken);
        // The engine saves a lane and its entries together, so an id whose entry is missing, or no longer in the state its
        // place implies, is a document someone removed or edited by hand: skipped rather than shown wrong.
        EntryView? View(MergeQueueLane lane, string? id, bool waiting)
            => id is not null && entries.GetValueOrDefault(id) is { } entry
               && (waiting ? entry.State == EMergeQueueEntryState.Queued : !entry.State.IsTerminal())
                ? ToView(entry, lane.PositionOf(id))
                : null;

        var views = lanes.Select(l => l.BaseBranch)
            .Concat(recent.Select(r => r.BaseBranch))
            .Distinct(StringComparer.Ordinal)
            .Select(branch =>
            {
                var lane = lanes.FirstOrDefault(l => string.Equals(l.BaseBranch, branch, StringComparison.Ordinal)) ?? new MergeQueueLane { BaseBranch = branch };
                return new LaneView(
                    branch,
                    lane.Paused,
                    View(lane, lane.Current, waiting: false),
                    [.. lane.Waiting.Select(id => View(lane, id, waiting: true)).OfType<EntryView>()],
                    [.. recent.Where(r => string.Equals(r.BaseBranch, branch, StringComparison.Ordinal)).Reverse().Take(10).Select(ToView)]);
            })
            .Where(l => l.Current is not null || l.Waiting.Count > 0 || l.Recent.Count > 0)
            .OrderBy(l => string.Equals(l.BaseBranch, repository.DefaultBranch, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(l => l.BaseBranch, StringComparer.Ordinal)
            .ToList();

        // O15: the configuration a run actually used, from the entry being worked on or else the latest one to end. Reading
        // coverage.yml afresh would cost a forge call on every view, anonymous ones included.
        var source = lanes.Select(l => l.Current).OfType<string>().Select(id => entries.GetValueOrDefault(id)).OfType<MergeQueueEntry>().FirstOrDefault()
            ?? (recent.Count > 0 ? await session.LoadAsync<MergeQueueEntry>(MergeQueueEntry.DocumentId(repository.Id!, recent[^1].Number), cancellationToken) : null);
        var config = source is { Config.Values.Count: > 0 }
            ? new ConfigView(source.Number, source.Run, source.Config.ConfigRef, [.. source.Config.Values.Select(v => new ConfigValueView(v.Key, v.Value, v.Source))])
            : null;

        return Ok(new MergeQueueView(true, views, config, await ViewerAsync(repository, queue, cancellationToken)));
    }

    /// <summary>The button's enqueue (O8): the same command a label or <c>/queue</c> raises, as the viewer's own login.</summary>
    // RequireAntiforgeryToken, not MVC's attribute (#300): see RepoSettingsController.RotateBadgeToken.
    [HttpPost("api/repos/{provider}/{owner}/{name}/merge-queue/{number:int}")]
    [SparkAuthorize("Manage", "RepoSettings")]
    [RequireAntiforgeryToken]
    public Task<ActionResult<CommandAccepted>> Enqueue(string provider, string owner, string name, int number, CancellationToken cancellationToken)
        => CommandAsync(provider, owner, name, number, EMergeQueueCommand.Enqueue, cancellationToken);

    /// <summary>The button's dequeue: the same command <c>/dequeue</c> raises (the author, or someone with write, D51 (3)).</summary>
    [HttpDelete("api/repos/{provider}/{owner}/{name}/merge-queue/{number:int}")]
    [SparkAuthorize("Manage", "RepoSettings")]
    [RequireAntiforgeryToken]
    public Task<ActionResult<CommandAccepted>> Dequeue(string provider, string owner, string name, int number, CancellationToken cancellationToken)
        => CommandAsync(provider, owner, name, number, EMergeQueueCommand.Dequeue, cancellationToken);

    /// <summary>
    /// The panel's queue dropdown: the open, non-draft pull requests on a branch the default branch's <c>coverage.yml</c>
    /// queues (O16), less those already in the queue. Same right as <see cref="Enqueue"/>, since only someone who may
    /// press the button needs the list.
    /// </summary>
    /// <param name="refresh">Skip the remembered forge answer (the panel's refresh button).</param>
    /// <remarks>
    /// Only the forge half is remembered, for <see cref="CandidatesCacheDuration"/>: a panel that reloads after every
    /// command would otherwise list the repository's pull requests each time. The queue's own state is read live, so a
    /// pull request leaves the list the moment it is queued. A forge failure answers an empty list with the reason, is
    /// not remembered, and is never a 500: the dropdown is a convenience, and the label and <c>/queue</c> still work.
    /// </remarks>
    [HttpGet("api/repos/{provider}/{owner}/{name}/merge-queue/candidates")]
    [EnableRateLimiting("browse")]
    [SparkAuthorize("Manage", "RepoSettings")]
    public async Task<ActionResult<QueueCandidates>> Candidates(
        string provider, string owner, string name, [FromQuery] bool refresh, CancellationToken cancellationToken)
    {
        var repository = await scope.ResolveAsync(provider, owner, name, cancellationToken);
        if (repository is null) return Refuse();

        var queue = queues.FirstOrDefault(q => q.Provider == repository.Provider);
        if (queue is null) return Ok(new QueueCandidates([], null));

        var key = $"merge-queue-candidates/{repository.Id}";
        if (refresh || !cache.TryGetValue(key, out OpenCandidates? open) || open is null)
        {
            (open, var remember) = await ReadOpenCandidatesAsync(repository, queue, cancellationToken);
            if (remember) cache.Set(key, open, CandidatesCacheDuration);
            else cache.Remove(key);
        }
        if (open.PullRequests.Count == 0) return Ok(new QueueCandidates([], open.Unavailable));

        // The same rule the enqueue applies: an entry that has not ended is in the queue; an evicted, dequeued or refused
        // one may be queued again.
        var entries = await session.LoadAsync<MergeQueueEntry>(
            open.PullRequests.Select(p => MergeQueueEntry.DocumentId(repository.Id!, p.Number)).Distinct(StringComparer.Ordinal), cancellationToken);
        var candidates = open.PullRequests
            .Where(p => entries.GetValueOrDefault(MergeQueueEntry.DocumentId(repository.Id!, p.Number)) is not { } entry || entry.State.IsTerminal())
            .OrderByDescending(p => p.Number)
            .Select(p => new QueueCandidate(p.Number, p.Title, p.BaseRef, p.AuthorLogin))
            .ToList();
        return Ok(new QueueCandidates(candidates, null));
    }

    /// <summary>
    /// The forge half of <see cref="Candidates"/>, and whether it may be remembered (a forge failure may not).
    /// </summary>
    private async Task<(OpenCandidates Open, bool Remember)> ReadOpenCandidatesAsync(
        Repository repository, IForgeMergeQueue queue, CancellationToken cancellationToken)
    {
        if (repository.DefaultBranch is not { } defaultBranch)
            return (new OpenCandidates([], "The repository's default branch is not known yet."), false);

        try
        {
            // O16: the queue's configuration comes from the DEFAULT branch, as the engine reads it.
            var yml = await forges.For(repository).GetFileContentAsync(repository, defaultBranch, CoverageYml.FileName, cancellationToken);
            var parsed = MergeQueueYml.Parse(yml, defaultBranch);
            if (!parsed.IsUsable || parsed.Section is not { } section)
                return (new OpenCandidates([], parsed.Errors.Count > 0
                    ? $"The mergeQueue section of {CoverageYml.FileName} on {defaultBranch} is not usable: {parsed.Errors[0]}"
                    : $"The merge queue is not turned on in {CoverageYml.FileName} on {defaultBranch}."), true);

            var open = new List<ForgeOpenPullRequest>();
            foreach (var branch in section.Branches)
                open.AddRange((await queue.ListOpenPullRequestsAsync(repository, branch, cancellationToken)).Where(p => !p.IsDraft));
            return (new OpenCandidates(open, null), true);
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
        {
            logger.LogWarning(ex, "Merge queue: could not list the queue candidates of {Repository}", repository.FullName);
            return (new OpenCandidates([], ex is ForgeAccessDeniedException
                ? "GitHub refused to list this repository's pull requests to the app. Queue one with the label or /queue instead."
                : "GitHub could not list this repository's open pull requests right now. Try again in a minute."), false);
        }
    }

    private async Task<ActionResult<CommandAccepted>> CommandAsync(
        string provider, string owner, string name, int number, EMergeQueueCommand command, CancellationToken cancellationToken)
    {
        var repository = await scope.ResolveAsync(provider, owner, name, cancellationToken);
        if (repository is null) return Refuse();
        if (number <= 0)
            return BadRequest(new CommandRefused("A pull request number is a positive whole number."));
        if (!queues.Any(q => q.Provider == repository.Provider))
            return Conflict(new CommandRefused("This repository's forge has no merge queue (GitHub only for now)."));

        // T1: the actor is who the forge says the signed-in user is, never a name from the request or the editable profile.
        var account = accounts.FirstOrDefault(a => a.Provider == repository.Provider) is { } access
            ? await access.GetViewerAccountAsync(cancellationToken)
            : null;
        if (account is not { Login: { } login })
            return account is { Linked: true }
                ? StatusCode(account.State == EForgeCredentialState.Unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status403Forbidden,
                    new CommandRefused(account.State == EForgeCredentialState.Unavailable
                        ? "GitHub could not confirm who you are right now. Try again in a minute."
                        : "Your GitHub sign-in has expired. Sign in with GitHub again, then queue it."))
                : StatusCode(StatusCodes.Status403Forbidden, new CommandRefused(
                    "Your account has no linked GitHub sign-in. The queue acts as your GitHub login, so sign in with GitHub to queue pull requests from here."));

        var entry = await session.LoadAsync<MergeQueueEntry>(MergeQueueEntry.DocumentId(repository.Id!, number), cancellationToken);
        var active = entry is not null && !entry.State.IsTerminal();
        if (command == EMergeQueueCommand.Dequeue && !active)
            return Conflict(new CommandRefused($"#{number} is not in the queue."));
        if (command == EMergeQueueCommand.Enqueue)
        {
            if (active)
                return Conflict(new CommandRefused($"#{number} is already in the queue."));
            // A number that names nothing would only bounce around the saga's retries; say so now.
            if (await forges.For(repository).GetPullRequestAsync(repository, number, cancellationToken) is null)
                return UnprocessableEntity(new CommandRefused($"{repository.FullName} has no pull request #{number}."));
        }

        await messageBus.BroadcastAsync(new ForgeWebhookMessage<MergeQueueCommandIssued>(repository.Provider,
            new MergeQueueCommandIssued(repository.Id!, number, command, login, EMergeQueueCommandSource.Button)), cancellationToken);
        logger.LogInformation("Merge queue: {Command} of #{Number} in {Repository} by @{Actor} (button)", command, number, repository.FullName, login);
        return Accepted(new CommandAccepted(number, command == EMergeQueueCommand.Enqueue ? "enqueue" : "dequeue", login));
    }

    private async Task<ViewerView> ViewerAsync(Repository repository, IForgeMergeQueue? queue, CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true)
            return new ViewerView(false, false, null, null, null);
        if (accounts.FirstOrDefault(a => a.Provider == repository.Provider) is not { } access)
            return new ViewerView(true, false, null, null, null);

        var account = await access.GetViewerAccountAsync(cancellationToken);
        string? role = null;
        if (queue is not null && account.Login is { } login)
        {
            var key = $"merge-queue-role/{repository.Id}/{login.ToLowerInvariant()}";
            if (!cache.TryGetValue(key, out role))
            {
                try
                {
                    role = (await queue.GetActorRoleAsync(repository, login, cancellationToken)).ToString().ToLowerInvariant();
                    cache.Set(key, role, RoleCacheDuration);
                }
                catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
                {
                    // Unknown is not "none": the panel offers the buttons and the engine decides.
                    logger.LogDebug(ex, "Merge queue: could not read @{Login}'s role on {Repository}", login, repository.FullName);
                    role = null;
                }
            }
        }
        return new ViewerView(true, account.Linked, account.Login, role, account.State.ToString());
    }

    private static EntryView ToView(MergeQueueEntry entry, int? position) => new(
        entry.Number, entry.Title, entry.AuthorLogin, entry.State.ToString(), entry.History.LastOrDefault()?.Text, entry.StateChangedAtUtc,
        entry.EnqueuedAtUtc, entry.EnqueuedBy, entry.EnqueuedVia.ToString(), entry.Run, position,
        entry.State == EMergeQueueEntryState.Regenerating ? entry.PendingDispatch()?.RunUrl ?? entry.DispatchRunUrl : null);

    private static OutcomeView ToView(MergeQueueOutcome outcome) => new(
        outcome.Number, outcome.Title, outcome.Run, outcome.State.ToString(), outcome.Reason, outcome.Message, outcome.EnqueuedBy,
        outcome.MergeCommitSha, outcome.FinishedAtUtc);

    /// <summary>The browse API's refusal (#453): anonymous 401, signed-in 404, one constant body.</summary>
    private JsonResult Refuse()
    {
        var (body, status) = SparkDenial.Refuse(HttpContext);
        return new JsonResult(body) { StatusCode = status };
    }
}
