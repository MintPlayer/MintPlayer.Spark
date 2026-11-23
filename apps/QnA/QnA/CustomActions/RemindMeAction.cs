using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Actions;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Notifications;

namespace QnA.CustomActions;

/// <summary>
/// "Remind me": a stored notification about this question, a few seconds later, in the bell of whoever asked: the
/// signed-in user, or an anonymous visitor, who gets a visitor id for it (<c>docs/notification_bell_PRD.md</c> G2). It is
/// the demo of notifications for visitors: the notification arrives over <c>/spark/live</c> without a reload, is read
/// only by this browser, and moves to the account when the visitor signs in. The delay is a delayed message, so the
/// request ends at once.
/// </summary>
public partial class RemindMeAction : SparkCustomAction
{
    /// <summary>How long after the click the reminder arrives.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);

    [Inject] private readonly ISparkCurrentPrincipal principal;
    [Inject] private readonly IMessageBus messageBus;
    [Inject] private readonly IClientAccessor client;

    public override async Task ExecuteAsync(CustomActionArgs args, CancellationToken cancellationToken)
    {
        var question = args.Parent ?? throw new SparkValidationException("Open the question to be reminded of it.");
        var userId = principal.IsAuthenticated ? principal.Id : null;
        var visitorId = userId is null ? principal.EnsureVisitorId() : null;
        if (userId is null && visitorId is null)
            throw new SparkValidationException("A reminder needs a browser that keeps cookies.");

        // The question's title (PersistentObject.Name is the type's name, "Question").
        var title = question.Breadcrumb
            ?? (question.TryGetAttribute("Title", out var attribute) ? attribute.Value?.ToString() : null);
        await messageBus.DelayBroadcastAsync(
            new QuestionReminderMessage(question.Id!, title, userId, visitorId), Delay, cancellationToken);
        // "Shortly", not "in 5 seconds": a delayed message is woken by Messaging's sweeper (FallbackPollInterval, 30 s),
        // so it arrives between Delay and Delay + 30 s (15.8 s measured, PRD S1b).
        client.Notify("We will remind you of this question shortly.", NotificationKind.Info);
    }
}

/// <summary>A reminder of a question for one user or one visitor.</summary>
[MessageQueue("qna-reminders")]
public sealed record QuestionReminderMessage(string QuestionId, string? QuestionTitle, string? UserId, string? VisitorId);

/// <summary>Sends <see cref="QuestionReminderMessage"/> as a stored notification, in-app only for a visitor.</summary>
public partial class QuestionReminderRecipient : IRecipient<QuestionReminderMessage>
{
    [Inject] private readonly ISparkNotifier notifier;

    public Task HandleAsync(QuestionReminderMessage message, CancellationToken cancellationToken = default)
        => notifier.NotifyAsync(new SparkNotification
        {
            UserIds = message.UserId is { Length: > 0 } user ? [user] : [],
            VisitorIds = message.VisitorId is { Length: > 0 } visitor ? [visitor] : [],
            Title = "Reminder",
            Body = string.IsNullOrWhiteSpace(message.QuestionTitle) ? "The question you asked to be reminded of." : message.QuestionTitle,
            Url = $"/po/question/{Uri.EscapeDataString(message.QuestionId)}",
            Category = "reminders",
        }, cancellationToken);
}
