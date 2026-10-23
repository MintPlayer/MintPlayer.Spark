using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PrSize;

/// <summary>
/// Forwards a pull request's opened or pushed event to the labeller's lane when the repository has the feature on
/// (docs/coverage_pr_size_labels_PRD.md D1, D9).
/// </summary>
/// <remarks>
/// A filter, then a forward, like the merge queue's: this runs on the shared forge-event lane, so it only point-loads the
/// repository and its account. It never creates an account, and every write happens on
/// <see cref="Feedback.CoverageQueues.PrSizeLabels"/>.
/// </remarks>
public partial class PrSizeLabelEventsRecipient : IRecipient<ForgeWebhookMessage<PullRequestUpdated>>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;

    public async Task HandleAsync(ForgeWebhookMessage<PullRequestUpdated> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        var repository = await session.LoadAsync<Repository>(evt.RepositoryId, cancellationToken);
        if (repository is null || repository.PrSizeLabels == LookupReferences.EPrSizeLabelPolicy.Disabled) return;
        var account = repository.Account is { Length: > 0 } accountId ? await session.LoadAsync<Account>(accountId, cancellationToken) : null;
        if (!Repository.ResolvePrSizeLabels(repository, account).Enabled) return;

        await messageBus.BroadcastAsync(new ApplyPrSizeLabelMessage
        {
            RepositoryId = evt.RepositoryId,
            Number = evt.Number,
            HeadSha = evt.HeadSha,
            Additions = evt.Additions,
            Deletions = evt.Deletions,
            ChangedFiles = evt.ChangedFiles,
        }, cancellationToken);
    }
}

/// <summary>
/// The labeller's lane (PRD D3, D6, D7, D9): labels a pull request, reconciles a repository's label definitions, and
/// removes them. The one writer of <see cref="Repository.PrSizeLabelState"/>.
/// </summary>
public partial class PrSizeLabelRecipient :
    IRecipient<ApplyPrSizeLabelMessage>,
    IRecipient<ReconcilePrSizeLabelsMessage>,
    IRecipient<RemovePrSizeLabelsMessage>
{
    /// <summary>The breakdown's row cap: GitHub limits a check run's text to 65 535 characters (D7).</summary>
    internal const int BreakdownRows = 50;

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEnumerable<IForgeLabels> labels;
    [Inject] private readonly PrSizeLabelPacing pacing;
    [Inject] private readonly ILogger<PrSizeLabelRecipient> logger;

    public async Task HandleAsync(ApplyPrSizeLabelMessage message, CancellationToken cancellationToken = default)
    {
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        if (await LoadAsync(message.RepositoryId, cancellationToken) is not { } context) return;
        var (repository, account, forge) = context;
        if (!Repository.ResolvePrSizeLabels(repository, account).Enabled) return;

        long? checkId = null;
        ForgeSizeCheck? outcome = null;
        try
        {
            checkId = await forge.PublishSizeCheckAsync(repository, message.HeadSha,
                new ForgeSizeCheck(false, "Calculating size…", "Counting the changed lines.", null), null, cancellationToken);

            var scale = ScaleOf(account);
            var reconciler = new PrSizeLabelReconciler(forge, pacing);
            var archived = PrSizeLabelReconciler.IsCurrent(repository, scale)
                ? await ArchivedAsync(forge, repository, scale, cancellationToken)
                : await ReconcileAndSaveAsync(reconciler, repository, scale, cancellationToken);

            var result = await SizeAsync(forge, repository, account, scale, message, cancellationToken);
            var row = scale.LabelFor(result.Size)!;
            var target = scale.Desired(row).Name;

            if (archived.Contains(target))
            {
                outcome = new ForgeSizeCheck(true, "Size label not applied",
                    $"This pull request changes {Lines(result.Size)}, which is `{target}`, but that label is archived in this repository. Unarchive it to have it applied.",
                    Breakdown(result));
                return;
            }

            await ApplyExclusivelyAsync(forge, repository, message.Number, target, cancellationToken);
            outcome = new ForgeSizeCheck(true, $"{target} · {Lines(result.Size)} changed", Summary(result), Breakdown(result));
        }
        catch (ForgeAccessDeniedException ex)
        {
            outcome = Failed($"The app is missing a permission ({ex.RequiredPermission}). {ex.Message}");
        }
        catch (ForgeRequestException ex) when (ForgeRateLimitedException.Find(ex) is null)
        {
            outcome = Failed(ex.Message);
        }
        finally
        {
            // Never left in progress (D7): the merge queue waits for every check run.
            await CompleteCheckAsync(forge, repository, message.HeadSha, checkId, outcome ?? Failed("Interrupted; it is retried."));
        }
    }

    public async Task HandleAsync(ReconcilePrSizeLabelsMessage message, CancellationToken cancellationToken = default)
    {
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        if (await LoadAsync(message.RepositoryId, cancellationToken) is not { } context) return;
        var (repository, account, forge) = context;
        // Only a repository that has labels, or should have them, is worth the writes.
        if (repository.PrSizeLabelState.Count == 0 && !Repository.ResolvePrSizeLabels(repository, account).Enabled) return;
        var scale = ScaleOf(account);
        if (PrSizeLabelReconciler.IsCurrent(repository, scale)) return;
        await ReconcileAndSaveAsync(new PrSizeLabelReconciler(forge, pacing), repository, scale, cancellationToken);
    }

    public async Task HandleAsync(RemovePrSizeLabelsMessage message, CancellationToken cancellationToken = default)
    {
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        if (await LoadAsync(message.RepositoryId, cancellationToken) is not { } context) return;
        var (repository, _, forge) = context;
        if (repository.PrSizeLabelState.Count == 0) return;
        try
        {
            await new PrSizeLabelReconciler(forge, pacing).RemoveAsync(repository, cancellationToken);
        }
        finally
        {
            // What was deleted stays forgotten even when a later delete failed.
            await session.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task<(Repository Repository, Account? Account, IForgeLabels Forge)?> LoadAsync(string repositoryId, CancellationToken cancellationToken)
    {
        var repository = await session.LoadAsync<Repository>(repositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return null;
        // A forge without pull-request labels (Bitbucket Cloud) has no implementation: nothing to do (D8).
        if (labels.FirstOrDefault(l => l.Provider == repository.Provider) is not { } forge) return null;
        var account = repository.Account is { Length: > 0 } accountId ? await session.LoadAsync<Account>(accountId, cancellationToken) : null;
        return (repository, account, forge);
    }

    private async Task<IReadOnlySet<string>> ReconcileAndSaveAsync(PrSizeLabelReconciler reconciler, Repository repository, PrSizeScale scale, CancellationToken cancellationToken)
    {
        try
        {
            return await reconciler.ReconcileAsync(repository, scale, cancellationToken);
        }
        finally
        {
            // The state records only confirmed writes, so it is saved even when a later write failed.
            await session.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static async Task<IReadOnlySet<string>> ArchivedAsync(IForgeLabels forge, Repository repository, PrSizeScale scale, CancellationToken cancellationToken)
    {
        var names = scale.Rows.Select(r => scale.Desired(r).Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (await forge.ListRepositoryLabelsAsync(repository, cancellationToken))
            .Where(l => l.Archived && names.Contains(l.Name))
            .Select(l => l.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The size (D5, D6): from the totals when they settle it or exceed the forge's cap, else from the files.</summary>
    private static async Task<PrSizeResult> SizeAsync(IForgeLabels forge, Repository repository, Account? account, PrSizeScale scale, ApplyPrSizeLabelMessage message, CancellationToken cancellationToken)
    {
        if (message.Additions is { } additions && message.Deletions is { } deletions)
        {
            // Ignoring only lowers the size, so a raw total inside the smallest row is that row whatever the rules say (S2).
            if (scale.LowestRowCeiling is { } ceiling && additions + deletions < ceiling)
                return PrSizeCalculator.FromTotals(additions, deletions);
            if (message.ChangedFiles > PrSizeCalculator.ForgeFileCap)
                return PrSizeCalculator.FromTotals(additions, deletions);
        }

        var files = await forge.ListPullRequestFilesAsync(repository, message.Number, cancellationToken);
        var calculator = new PrSizeCalculator((account?.PrSizeIgnoreRules ?? PrSizeDefaults.IgnoreRules()).Concat(repository.PrSizeIgnoreRules));
        return calculator.Compute(files,
            message.Additions ?? files.Sum(f => f.Additions),
            message.Deletions ?? files.Sum(f => f.Deletions));
    }

    /// <summary>Adds the label and takes the scale's other labels off; no call when the pull request is already right.</summary>
    private static async Task ApplyExclusivelyAsync(IForgeLabels forge, Repository repository, int number, string target, CancellationToken cancellationToken)
    {
        var managed = repository.PrSizeLabelState.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var carried = await forge.ListPullRequestLabelsAsync(repository, number, cancellationToken);
        if (!carried.Contains(target, StringComparer.OrdinalIgnoreCase))
            await forge.AddLabelAsync(repository, number, target, cancellationToken);
        foreach (var other in carried.Where(l => managed.Contains(l) && !string.Equals(l, target, StringComparison.OrdinalIgnoreCase)))
            await forge.RemoveLabelAsync(repository, number, other, cancellationToken);
    }

    private async Task CompleteCheckAsync(IForgeLabels forge, Repository repository, string sha, long? checkId, ForgeSizeCheck outcome)
    {
        try
        {
            await forge.PublishSizeCheckAsync(repository, sha, outcome, checkId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException)
        {
            logger.LogWarning(ex, "PR size: could not complete the check on {Sha} in {Repository}", sha, repository.FullName);
        }
    }

    /// <summary>
    /// The account's scale. A repository switched on by itself may sit under an account that never had one, or none at all:
    /// it gets the defaults (D2) rather than nothing.
    /// </summary>
    private static PrSizeScale ScaleOf(Account? account)
        => new(account is { PrSizeLabelScale.Count: > 0 } ? account.PrSizeLabelScale : PrSizeDefaults.Scale());

    private static ForgeSizeCheck Failed(string reason) => new(true, "Size label not applied", reason, null);

    private static string Lines(int size) => size == 1 ? "1 line" : $"{size} lines";

    internal static string Summary(PrSizeResult result)
    {
        if (result.Files.Count == 0)
            return $"Counted from the pull request's totals: {Lines(result.Size)}, no file rules applied.";
        var counted = result.Files.Count(f => f.Outcome == PrSizeFileOutcome.Counted);
        var ignored = result.Files.Count(f => f.Outcome == PrSizeFileOutcome.Ignored);
        var deletionsOnly = result.Files.Count(f => f.Outcome == PrSizeFileOutcome.DeletionsOnly);
        var summary = $"{counted} file(s) counted, {ignored} ignored, {deletionsOnly} counted for deletions only.";
        return result.UnlistedLines > 0
            ? $"{summary} {Lines(result.UnlistedLines)} in files the forge did not list count fully."
            : summary;
    }

    /// <summary>The per-file table, capped at <see cref="BreakdownRows"/> rows.</summary>
    internal static string? Breakdown(PrSizeResult result)
    {
        if (result.Files.Count == 0) return null;
        var text = new StringBuilder();
        text.AppendLine("| File | + | − | Counted | How | Rule |");
        text.AppendLine("|---|---:|---:|---:|---|---|");
        foreach (var file in result.Files.Take(BreakdownRows))
        {
            var how = file.Outcome switch
            {
                PrSizeFileOutcome.Ignored => "ignored",
                PrSizeFileOutcome.DeletionsOnly => "deletions only",
                _ => "counted",
            };
            text.AppendLine($"| `{Cell(file.Path)}` | {file.Additions} | {file.Deletions} | {file.Counted} | {how} | {(file.Rule is null ? "" : $"`{Cell(file.Rule)}`")} |");
        }
        if (result.Files.Count > BreakdownRows)
            text.AppendLine().AppendLine($"…and {result.Files.Count - BreakdownRows} more.");
        return text.ToString();
    }

    private static string Cell(string value) => value.Replace("|", "\\|").Replace("`", "'");
}
