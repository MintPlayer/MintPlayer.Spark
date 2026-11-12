using System.Globalization;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PackageVersioning;

/// <summary>
/// Segment F: the people a held backport waits for, and what tells them. The merge queue holds the App's backport on a
/// release line instead of evicting it (segment C); this says who is to fix it, and shows everyone else why the line stands.
/// </summary>
public interface IBackportAttention
{
    /// <summary>
    /// The backport <paramref name="held"/> was just held. Labels and assigns it, records it for the app's held list, and
    /// gives each pull request in <paramref name="waiting"/> a "Line blocked by #N" check. Returns what the hold comment adds:
    /// whom it asks, and the link to the held list. Never throws for the forge: what it cannot do, it leaves out.
    /// </summary>
    Task<string> HeldAsync(Repository repository, MergeQueueEntry held, IReadOnlyList<MergeQueueEntry> waiting, CancellationToken cancellationToken = default);

    /// <summary>A pull request joined the line behind <paramref name="held"/>: it gets the check too.</summary>
    Task BlockedAsync(Repository repository, MergeQueueEntry held, MergeQueueEntry waiting, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reminder <paramref name="reminder"/> (1: ask again; 2: the code owners) for a backport that is still held. Returns the
    /// comment to post, or null when there is nobody to remind.
    /// </summary>
    Task<string?> ReminderAsync(Repository repository, MergeQueueEntry held, int reminder, CancellationToken cancellationToken = default);

    /// <summary>The hold ended (it goes on, merged, or left the queue): the label goes, the blocked checks pass.</summary>
    Task ReleasedAsync(Repository repository, MergeQueueEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// Segment F (resilience PRD Q8 and Q12). Who is asked: the backported pull request's author; a fork's or a bot's (the
/// dependency updater's) pull request has no author who can push the fix, so the code owners of the changed files are
/// asked instead. A reminder after 2 working days asks again, and the next one asks the code owners.
/// </summary>
/// <remarks>
/// What they see (Q12): a comment linking to the app's held list (the database is the source of truth), the
/// <see cref="Label"/> label on the held pull request, and an informational, never required, "Line blocked by #N" check on
/// each pull request waiting behind it. The title is not changed, so the queue has no marker to strip.
/// </remarks>
[Register(typeof(IBackportAttention), ServiceLifetime.Scoped)]
public partial class BackportAttention : IBackportAttention
{
    [Inject] private readonly IForgeIntegrationResolver integrations;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeMergeQueue> queues;
    [Inject] private readonly IEnumerable<IForgeLabels> labels;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<BackportAttention> logger;

    /// <summary>The label on a held backport.</summary>
    public const string Label = "backport: needs attention";

    /// <summary>The held list's query alias: <c>/query/held-backports</c>.</summary>
    public const string HeldListAlias = "held-backports";

    /// <summary>The check on a pull request waiting behind held backport <paramref name="number"/>.</summary>
    public static string BlockedCheckName(int number) => $"Line blocked by #{number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Changed files read for CODEOWNERS; a backport touching more is asked about by its first ones.</summary>
    private const int MaxPaths = 300;

    /// <summary>The people asked: the author, else the code owners.</summary>
    private sealed record Asked(int? Source, string? Author, IReadOnlyList<string> Owners);

    public async Task<string> HeldAsync(Repository repository, MergeQueueEntry held, IReadOnlyList<MergeQueueEntry> waiting, CancellationToken cancellationToken = default)
    {
        var record = await session.LoadAsync<BackportPullRequest>(BackportPullRequest.DocumentId(repository.Id!, held.Number), cancellationToken);
        var asked = await WhoAsync(repository, held, record, owners: false, cancellationToken);

        if (Queue(repository) is { } queue)
        {
            await TryAsync(() => queue.AddLabelAsync(repository, held.Number, Label, cancellationToken), "label", held, repository);
            await AssignAsync(queue, repository, held, asked.Author is { } author ? [author] : asked.Owners, cancellationToken);
        }
        if (record is not null)
            await RecordAsync(repository, record, held, asked.Author, cancellationToken);
        foreach (var behind in waiting)
            await BlockedAsync(repository, held, behind, cancellationToken);

        var text = asked switch
        {
            { Author: { } author } => $"@{author}, this backports your #{Number(asked.Source)}, so it is yours to fix.",
            { Owners.Count: > 0 } => $"{string.Join(", ", asked.Owners)}: #{Number(asked.Source)} came from a fork or a bot, so the code owners of its files are asked to fix it.",
            _ => "Nobody is named to fix it: the backported pull request has no author to ask, and no CODEOWNERS entry covers its files.",
        };
        return HeldListUrl() is { } url ? $"{text}\n\n[Held backports]({url}) lists every backport that waits for you." : text;
    }

    public async Task BlockedAsync(Repository repository, MergeQueueEntry held, MergeQueueEntry waiting, CancellationToken cancellationToken = default)
    {
        var sha = string.IsNullOrEmpty(waiting.ExpectedHeadSha) ? waiting.PinnedHeadSha : waiting.ExpectedHeadSha;
        if (string.IsNullOrEmpty(sha) || Forge(repository) is not { } forge) return;
        var check = new ForgeVersioningCheck(BlockedCheckName(held.Number), Completed: true, Conclusion: "neutral",
            Title: $"`{held.BaseBranch}` waits for #{Number(held.Number)}",
            Summary: $"The merge queue holds #{Number(held.Number)}, and `{held.BaseBranch}` takes its backports in the order they merged, "
                + "so this pull request waits behind it. This check is informational: it never blocks a merge.",
            Text: held.EvictionMessage);
        if (await TryAsync(() => forge.PublishCheckAsync(repository, sha, check, null, cancellationToken), "publish the blocked check", held, repository)
            && !held.BlockedHeads.Contains(sha, StringComparer.OrdinalIgnoreCase))
            held.BlockedHeads.Add(sha);
    }

    public async Task<string?> ReminderAsync(Repository repository, MergeQueueEntry held, int reminder, CancellationToken cancellationToken = default)
    {
        var record = await session.LoadAsync<BackportPullRequest>(BackportPullRequest.DocumentId(repository.Id!, held.Number), cancellationToken);
        var asked = await WhoAsync(repository, held, record, owners: reminder > 1, cancellationToken);
        var since = held.HeldSinceUtc is { } at ? $" since {at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : string.Empty;
        var stands = $"This backport has held `{held.BaseBranch}`{since}, and the backports behind it still wait.";

        if (reminder > 1)
        {
            // The escalation (Q8): the code owners, also when the author was asked first.
            var owners = asked.Owners.Where(o => asked.Author is null || !string.Equals(o, $"@{asked.Author}", StringComparison.OrdinalIgnoreCase)).ToList();
            if (owners.Count == 0) return null;
            if (Queue(repository) is { } queue) await AssignAsync(queue, repository, held, owners, cancellationToken);
            return $"{string.Join(", ", owners)}: {stands} You own its files; push a fix to `{held.HeadBranch}`, or `/dequeue` it to let the line go on.";
        }
        return asked switch
        {
            { Author: { } author } => $"@{author}, a reminder: {stands}",
            { Owners.Count: > 0 } => $"{string.Join(", ", asked.Owners)}, a reminder: {stands}",
            _ => null,
        };
    }

    public async Task ReleasedAsync(Repository repository, MergeQueueEntry entry, CancellationToken cancellationToken = default)
    {
        if (Queue(repository) is { } queue)
            await TryAsync(() => queue.RemoveLabelAsync(repository, entry.Number, Label, cancellationToken), "remove the label", entry, repository);

        if (await session.LoadAsync<BackportPullRequest>(BackportPullRequest.DocumentId(repository.Id!, entry.Number), cancellationToken) is { } record
            && await session.LoadAsync<BackportAttempt>(BackportAttempt.DocumentId(repository.Provider, repository.GitHubId, record.Source, entry.BaseBranch), cancellationToken)
                is { Outcome: BackportAttempt.Held } attempt)
        {
            attempt.Outcome = BackportAttempt.Opened;
            attempt.Detail = null;
        }

        if (Forge(repository) is { } forge)
        {
            var check = new ForgeVersioningCheck(BlockedCheckName(entry.Number), Completed: true, Conclusion: "success",
                Title: $"#{Number(entry.Number)} no longer holds `{entry.BaseBranch}`",
                Summary: "The backport this pull request waited behind is no longer held.", Text: null);
            foreach (var sha in entry.BlockedHeads)
                await TryAsync(() => forge.PublishCheckAsync(repository, sha, check, null, cancellationToken), "clear the blocked check", entry, repository);
        }
        entry.BlockedHeads.Clear();
    }

    /// <summary>The author, unless the source is a fork's or a bot's; the code owners when there is no author, or when asked.</summary>
    private async Task<Asked> WhoAsync(Repository repository, MergeQueueEntry held, BackportPullRequest? record, bool owners, CancellationToken ct)
    {
        string? author = null;
        if (record is not null && Forge(repository) is { } forge)
        {
            try
            {
                if (await forge.GetPullRequestTextAsync(repository, record.Source, ct) is { AuthorIsBot: false, HeadIsFork: false, AuthorLogin: { Length: > 0 } login })
                    author = login;
            }
            catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
            {
                logger.LogWarning(ex, "Backport attention: could not read #{Source} in {Repository}", record.Source, repository.FullName);
            }
        }
        var codeOwners = author is null || owners ? await CodeOwnersAsync(repository, held, ct) : [];
        return new(record?.Source, author, codeOwners);
    }

    /// <summary>The code owners of the held backport's files, by the line's own CODEOWNERS.</summary>
    private async Task<IReadOnlyList<string>> CodeOwnersAsync(Repository repository, MergeQueueEntry held, CancellationToken ct)
    {
        try
        {
            var integration = integrations.For(repository);
            string? text = null;
            foreach (var location in CodeOwners.Locations)
                if ((text = await integration.GetFileContentAsync(repository, held.BaseBranch, location, ct)) is not null) break;
            if (text is null) return [];

            IReadOnlyList<string> paths = held.EvictionPaths;
            if (paths.Count == 0 && labels.FirstOrDefault(l => l.Provider == repository.Provider) is { } files)
                paths = [.. (await files.ListPullRequestFilesAsync(repository, held.Number, ct)).Take(MaxPaths).Select(f => f.Path)];
            return CodeOwners.Parse(text).OwnersOf(paths);
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException)
        {
            logger.LogWarning(ex, "Backport attention: could not read the code owners of #{Number} in {Repository}", held.Number, repository.FullName);
            return [];
        }
    }

    /// <summary>Assigns people, never teams (a team is mentioned, not assigned).</summary>
    private async Task AssignAsync(IForgeMergeQueue queue, Repository repository, MergeQueueEntry held, IReadOnlyList<string> people, CancellationToken ct)
    {
        var logins = people.Select(p => p.TrimStart('@')).Where(p => p.Length > 0 && !p.Contains('/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (logins.Count > 0)
            await TryAsync(() => queue.AddAssigneesAsync(repository, held.Number, logins, ct), "assign", held, repository);
    }

    /// <summary>The held list's row: the backport attempt of this source and line, held now.</summary>
    private async Task RecordAsync(Repository repository, BackportPullRequest record, MergeQueueEntry held, string? author, CancellationToken ct)
    {
        var id = BackportAttempt.DocumentId(repository.Provider, repository.GitHubId, record.Source, held.BaseBranch);
        var attempt = await session.LoadAsync<BackportAttempt>(id, ct);
        if (attempt is null)
        {
            attempt = new BackportAttempt
            {
                Id = id, Repository = repository.Id!, Source = record.Source, SourceSha = record.SourceSha, Branch = held.BaseBranch,
                PullRequest = held.Number, AttemptedAtUtc = time.GetUtcNow().UtcDateTime,
            };
            await session.StoreAsync(attempt, id, ct);
        }
        attempt.Outcome = BackportAttempt.Held;
        attempt.PullRequest = held.Number;
        attempt.SourceAuthor = author ?? attempt.SourceAuthor;
        var detail = held.EvictionMessage;
        attempt.Detail = detail is { Length: > BackportAttempt.MaxDetail } ? detail[..BackportAttempt.MaxDetail] : detail;
    }

    private string? HeldListUrl()
        => configuration["Coverage:BaseUrl"] is { Length: > 0 } baseUrl ? $"{baseUrl.TrimEnd('/')}/query/{HeldListAlias}" : null;

    private IForgeVersioning? Forge(Repository repository) => forges.FirstOrDefault(f => f.Provider == repository.Provider);

    private IForgeMergeQueue? Queue(Repository repository) => queues.FirstOrDefault(q => q.Provider == repository.Provider);

    private static string Number(int? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "?";

    private async Task<bool> TryAsync(Func<Task> action, string what, MergeQueueEntry entry, Repository repository)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException or HttpRequestException or NotSupportedException)
        {
            logger.LogWarning(ex, "Backport attention: could not {What} for #{Number} in {Repository}", what, entry.Number, repository.FullName);
            return false;
        }
    }
}
