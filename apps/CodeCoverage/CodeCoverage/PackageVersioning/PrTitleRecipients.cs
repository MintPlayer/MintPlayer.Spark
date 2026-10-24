using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.PrTitles;
using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents.Session;

namespace CodeCoverage.PackageVersioning;

/// <summary>Correct one pull request's title (docs/coverage_package_versioning_PRD.md D14).</summary>
[MessageQueue(CoverageQueues.Versioning)]
public sealed record CorrectPrTitleMessage
{
    public required string RepositoryId { get; init; }
    public required int Number { get; init; }
}

/// <summary>
/// Forwards a described pull request to the versioning lane when title correction is on for its repository (D14). A filter
/// on the shared forge-event lane: point loads only, no writes.
/// </summary>
public partial class PrTitleEventsRecipient : IRecipient<ForgeWebhookMessage<PullRequestDescribed>>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;

    public async Task HandleAsync(ForgeWebhookMessage<PullRequestDescribed> message, CancellationToken cancellationToken = default)
    {
        var evt = message.Event;
        var repository = await session.LoadAsync<Repository>(evt.RepositoryId, cancellationToken);
        if (repository is null || repository.PrTitleCorrection == LookupReferences.EPrTitlePolicy.Disabled) return;
        var account = repository.Account is { Length: > 0 } accountId ? await session.LoadAsync<Account>(accountId, cancellationToken) : null;
        if (!Repository.ResolvePrTitleCorrection(repository, account).Enabled) return;

        await messageBus.BroadcastAsync(new CorrectPrTitleMessage { RepositoryId = evt.RepositoryId, Number = evt.Number }, cancellationToken);
    }
}

/// <summary>
/// Rewrites the title and reports the outcome as the "PR title" check on the head commit (D14). Never left in progress.
/// </summary>
public partial class PrTitleRecipient : IRecipient<CorrectPrTitleMessage>
{
    public const string CheckName = "PR title";

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly ILogger<PrTitleRecipient> logger;

    public async Task HandleAsync(CorrectPrTitleMessage message, CancellationToken cancellationToken = default)
    {
        var repository = await session.LoadAsync<Repository>(message.RepositoryId, cancellationToken);
        if (repository is null || repository.Connection == RepositoryConnection.Disconnected || repository.Archived) return;
        if (forges.FirstOrDefault(f => f.Provider == repository.Provider) is not { } forge) return;
        var account = repository.Account is { Length: > 0 } accountId ? await session.LoadAsync<Account>(accountId, cancellationToken) : null;
        if (!Repository.ResolvePrTitleCorrection(repository, account).Enabled) return;

        if (await forge.GetPullRequestTextAsync(repository, message.Number, cancellationToken) is not { IsOpen: true } pull) return;

        ForgeVersioningCheck? outcome = null;
        try
        {
            var result = await PrTitleBuilder.BuildAsync(pull.Title, pull.Body, pull.HeadRef, pull.AuthorIsBot, repository.FullName,
                async (number, ct) => (await forge.GetIssueAsync(repository, number, ct))?.Labels, cancellationToken);
            if (result is null)
            {
                outcome = Check("Title left alone", "The title is empty.");
                return;
            }

            var changed = !string.Equals(result.Title, pull.Title, StringComparison.Ordinal);
            if (changed) await forge.UpdatePullRequestTitleAsync(repository, message.Number, result.Title, cancellationToken);
            outcome = Check(changed ? $"Title set to \"{result.Title}\"" : "Title already correct", Explain(result));
        }
        catch (ForgeAccessDeniedException ex)
        {
            outcome = Check("Title not corrected", $"The app is missing a permission ({ex.RequiredPermission}). {ex.Message}");
        }
        catch (ForgeRequestException ex) when (ForgeRateLimitedException.Find(ex) is null)
        {
            outcome = Check("Title not corrected", ex.Message);
        }
        finally
        {
            await CompleteAsync(forge, repository, pull.HeadSha, outcome ?? Check("Title not corrected", "Interrupted; it is retried."));
        }
    }

    internal static string Explain(PrTitleResult result)
    {
        var why = result.TypeSource switch
        {
            EPrTitleTypeSource.Template => "the ticked box in the pull-request template",
            EPrTitleTypeSource.Title => "the type the title already had",
            EPrTitleTypeSource.LinkedIssue => "a linked issue labelled `bug`",
            EPrTitleTypeSource.BranchIssue => "the issue named in the branch, labelled `bug`",
            _ => "the default",
        };
        var issues = result.Issues.Count == 0 ? "No linked issues of this repository." : $"Issues: {string.Join(", ", result.Issues.Select(i => $"#{i}"))}.";
        return $"Type `{result.Type}` from {why}. {issues}";
    }

    private static ForgeVersioningCheck Check(string title, string summary) => new(CheckName, true, "neutral", title, summary, null);

    private async Task CompleteAsync(IForgeVersioning forge, Repository repository, string sha, ForgeVersioningCheck outcome)
    {
        try
        {
            await forge.PublishCheckAsync(repository, sha, outcome, null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is ForgeAccessDeniedException or ForgeRequestException)
        {
            logger.LogWarning(ex, "PR title: could not publish the check on {Sha} in {Repository}", sha, repository.FullName);
        }
    }
}
