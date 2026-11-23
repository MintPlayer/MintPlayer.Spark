using CodeCoverage.Entities;
using CodeCoverage.Feedback;
using CodeCoverage.Forge;
using CodeCoverage.Versioning.Changelog;
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

    /// <summary>
    /// #693 D12: the identifier of the "PR title" check's button a person clicked (<see cref="PrTitleCheck.Buttons"/>);
    /// null for a correction an edit asked for.
    /// </summary>
    public string? Button { get; init; }
}

/// <summary>
/// Forwards a described pull request, or a click on the check's buttons, to the versioning lane when title correction is
/// on for its repository (D14, #693 D12). A filter on the shared forge-event lane: point loads only, no writes.
/// </summary>
public partial class PrTitleEventsRecipient : IRecipient<ForgeWebhookMessage<PullRequestDescribed>>, IRecipient<ForgeWebhookMessage<PullRequestTypeChosen>>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IMessageBus messageBus;

    public Task HandleAsync(ForgeWebhookMessage<PullRequestDescribed> message, CancellationToken cancellationToken = default)
        => ForwardAsync(message.Event.RepositoryId, message.Event.Number, null, cancellationToken);

    public Task HandleAsync(ForgeWebhookMessage<PullRequestTypeChosen> message, CancellationToken cancellationToken = default)
        => ForwardAsync(message.Event.RepositoryId, message.Event.Number, message.Event.Identifier, cancellationToken);

    private async Task ForwardAsync(string repositoryId, int number, string? button, CancellationToken cancellationToken)
    {
        var repository = await session.LoadAsync<Repository>(repositoryId, cancellationToken);
        if (repository is null || repository.PrTitleCorrection == LookupReferences.EPrTitlePolicy.Disabled) return;
        var account = repository.Account is { Length: > 0 } accountId ? await session.LoadAsync<Account>(accountId, cancellationToken) : null;
        if (!Repository.ResolvePrTitleCorrection(repository, account).Enabled) return;

        await messageBus.BroadcastAsync(new CorrectPrTitleMessage { RepositoryId = repositoryId, Number = number, Button = button }, cancellationToken);
    }
}

/// <summary>
/// Rewrites the title and reports the outcome as the "PR title" check on the head commit (D14). Never left in progress.
/// Without an explicit type the check asks with buttons (#693 D12); a click comes back here as <see cref="CorrectPrTitleMessage.Button"/>.
/// </summary>
public partial class PrTitleRecipient : IRecipient<CorrectPrTitleMessage>
{
    public const string CheckName = PrTitleCheck.Name;

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEnumerable<IForgeVersioning> forges;
    [Inject] private readonly IEnumerable<IForgeLabels> labellers;
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
            var skipped = HasSkipLabel(pull.Labels);
            if (message.Button == PrTitleCheck.NotUserFacing.Identifier && !skipped)
            {
                if (labellers.FirstOrDefault(l => l.Provider == repository.Provider) is not { } labeller)
                {
                    outcome = Check("Title not corrected", "This forge cannot label pull requests.");
                    return;
                }
                await labeller.AddLabelAsync(repository, message.Number, PrTitleCheck.SkipLabel, cancellationToken);
                skipped = true;
            }

            var result = await PrTitleBuilder.BuildAsync(pull.Title, pull.Body, pull.AuthorIsBot, repository.FullName,
                async (number, ct) => (await forge.GetIssueAsync(repository, number, ct))?.Labels,
                message.Button is { } button ? PrTitleCheck.TypeOf(button) : null, cancellationToken);
            if (result is null)
            {
                outcome = Check("Title left alone", "The title is empty.");
                return;
            }

            var changed = !string.Equals(result.Title, pull.Title, StringComparison.Ordinal);
            if (changed) await forge.UpdatePullRequestTitleAsync(repository, message.Number, result.Title, cancellationToken);
            outcome = Outcome(result, changed, skipped, message.Number);
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

    /// <summary>
    /// The check for a built title: with a type, what decided it and no buttons; without one, the buttons (#693 D12), unless
    /// the pull request is labelled <c>changelog: skip</c> already.
    /// </summary>
    internal static ForgeVersioningCheck Outcome(PrTitleResult result, bool changed, bool skipped, int number)
    {
        var external = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (result.Type is not null)
            return Check(changed ? $"Title set to \"{result.Title}\"" : "Title already correct", Explain(result)) with { Actions = [], ExternalId = external };
        if (skipped)
            return Check("Not user-facing", $"Labelled `{PrTitleCheck.SkipLabel}`: no changelog entry. {Issues(result)}") with { Actions = [], ExternalId = external };
        return Check("Type of change?",
            "Nothing says what kind of change this is: no ticked box in the template, no type in the title, no linked issue labelled `bug`. "
            + "Choose one with a button (Feature or Fix types the title; Not user-facing keeps it out of the changelog), tick a box, "
            + $"or start the title with a type such as `perf:`. {Issues(result)}") with { Actions = PrTitleCheck.Buttons, ExternalId = external };
    }

    internal static string Explain(PrTitleResult result)
    {
        var why = result.TypeSource switch
        {
            EPrTitleTypeSource.Button => "the button chosen on this check",
            EPrTitleTypeSource.Template => "the ticked box in the pull-request template",
            EPrTitleTypeSource.Title => "the type the title already had",
            EPrTitleTypeSource.LinkedIssue => "a linked issue labelled `bug`",
            _ => "nothing",
        };
        return $"Type `{result.Type}` from {why}. {Issues(result)}";
    }

    private static string Issues(PrTitleResult result)
        => result.Issues.Count == 0 ? "No linked issues of this repository." : $"Issues: {string.Join(", ", result.Issues.Select(i => $"#{i}"))}.";

    private static bool HasSkipLabel(IReadOnlyList<string> labels)
        => labels.Any(l => string.Equals(l, PrTitleCheck.SkipLabel, StringComparison.OrdinalIgnoreCase));

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
