using CodeCoverage.Dependencies.Onboarding;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Notifications;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;

namespace CodeCoverage.MergeQueue;

/// <summary>
/// Delivers the open <see cref="RulesetNotice"/>s to the signed-in viewer (#714 Dec5, Dec11): one bell notification per
/// notice of a repository the viewer manages, once per notice <see cref="RulesetNotice.Key"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Called when the app is opened by someone signed in, and right after a sign-in</b> (<c>POST api/me/ruleset-notices</c>,
/// which the shell posts whenever it becomes signed in). Not an <c>ISparkSignedInHandler</c>: that runs before the sign-in
/// ticket is issued, while every "manages" check (<see cref="IDependencyOnboardingService.CheckAsync"/>) still sees the
/// anonymous caller, and it misses everyone who stays signed in across visits.
/// </para>
/// <para>
/// <b>Cheap when there is nothing to say.</b> One prefix load of the notices, no index; the forge is asked only when an open
/// notice waits for this viewer. A notice of a repository the viewer does not manage stays waiting for its managers.
/// </para>
/// <para>
/// <b>Who was told is a patch, not a store.</b> The queue may raise a new drift on the same document between this load and
/// this save. The patch adds the viewer only while the key is still the one they were told about, so the new drift reaches
/// them too.
/// </para>
/// </remarks>
public interface IRulesetNoticeBell
{
    /// <summary>Notifies the signed-in viewer of every open notice they manage and were not told about; returns how many.</summary>
    Task<int> DeliverAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRulesetNoticeBell"/>
[Register(typeof(IRulesetNoticeBell), ServiceLifetime.Scoped)]
public partial class RulesetNoticeBell : IRulesetNoticeBell
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly ISparkCurrentPrincipal currentUser;
    [Inject] private readonly IDependencyOnboardingService permissions;
    [Inject] private readonly ISparkNotifier notifier;
    [Inject] private readonly IManager manager;

    /// <summary>The notifications' category, also the push topic.</summary>
    public const string Category = "merge-queue-rulesets";

    /// <summary>A bound on the one load: three notices per repository at most.</summary>
    internal const int MaxNotices = 1024;

    public async Task<int> DeliverAsync(CancellationToken cancellationToken = default)
    {
        if (currentUser.Id is not { Length: > 0 } userId)
            return 0;

        var notices = await session.Advanced.LoadStartingWithAsync<RulesetNotice>(RulesetNotice.Prefix, start: 0, pageSize: MaxNotices,
            token: cancellationToken);
        var waiting = notices.Where(notice => notice.IsOpen() && !notice.NotifiedUsers.Contains(userId, StringComparer.Ordinal)).ToList();
        if (waiting.Count == 0)
            return 0;

        var repositories = await session.LoadAsync<Repository>(waiting.Select(notice => notice.Repository).Distinct(StringComparer.Ordinal),
            cancellationToken);
        var managed = await permissions.CheckAsync([.. repositories.Values.OfType<Repository>()], cancellationToken);

        var delivered = 0;
        foreach (var notice in waiting)
        {
            if (repositories.GetValueOrDefault(notice.Repository) is not { } repository
                || managed.GetValueOrDefault(repository.Id.Value) is null)
            {
                continue;
            }

            var (title, body) = Text(notice, repository);
            await notifier.NotifyAsync(new SparkNotification
            {
                UserIds = [userId],
                Title = Cut(title, 200),
                Body = Cut(body, 2000),
                Url = AccountUrl(repository),
                Category = Category,
            }, cancellationToken);
            session.Advanced.Defer(new PatchCommandData(session.Advanced.GetDocumentId(notice), null, new PatchRequest
            {
                Script = "if (this.Key === args.key && this.NotifiedUsers.indexOf(args.user) < 0) { this.NotifiedUsers.push(args.user); }",
                Values =
                {
                    ["key"] = notice.Key,
                    ["user"] = userId,
                },
            }));
            delivered++;
        }

        if (delivered > 0)
            await session.SaveChangesAsync(cancellationToken);
        return delivered;
    }

    /// <summary>
    /// The account page, whose repository list carries "Set up rulesets": Spark has no link that opens a query action on a
    /// selected row, so the notification says which repository to select.
    /// </summary>
    internal static string AccountUrl(Repository repository)
        => $"/{repository.Provider.ToCanonicalString()}/a/{Uri.EscapeDataString(repository.OwnerLogin)}";

    private (string Title, string Body) Text(RulesetNotice notice, Repository repository)
    {
        var name = repository.FullName;
        var checks = string.Join(", ", notice.Checks);
        var (title, body) = notice.Kind switch
        {
            ERulesetNoticeKind.MissingCheck => (
                manager.GetTranslatedMessage("rulesets.bell.missingCheck.title", name),
                manager.GetTranslatedMessage("rulesets.bell.missingCheck.body", checks, notice.PullRequest ?? 0)),
            ERulesetNoticeKind.PinMismatch => (
                manager.GetTranslatedMessage("rulesets.bell.pinMismatch.title", name),
                manager.GetTranslatedMessage("rulesets.bell.pinMismatch.body", checks, notice.PullRequest ?? 0)),
            _ => (
                manager.GetTranslatedMessage("rulesets.bell.summaryJob.title", name),
                manager.GetTranslatedMessage("rulesets.bell.summaryJob.body", checks)),
        };
        return (title, body + " " + manager.GetTranslatedMessage("rulesets.bell.howTo", name));
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
