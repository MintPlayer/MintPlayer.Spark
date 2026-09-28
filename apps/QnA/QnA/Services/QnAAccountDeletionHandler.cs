using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.MailManager;

namespace QnA.Services;

/// <summary>
/// QnA's part of an account deletion (#460 D8): the posts stay — other people's answers and votes hang
/// on them, and <c>AuthorId</c> is an id that renders as a deleted user once the account is gone — and
/// the user gets a goodbye mail through MailManager (the app template <c>Templates/Mail/AccountDeleted</c>).
/// Moderation's own handler, registered before this one, removes the votes, flags and profile.
/// </summary>
/// <remarks>
/// ⚠️ Handlers run <b>before</b> the store deletes the account, so the mail is queued before the delete
/// is certain; a store failure after it would leave a sent goodbye on a live account. The store delete
/// is a single document delete, and the mail says so honestly ("was deleted at your request"), which is
/// the trade taken. Idempotent, as handlers must be: a retried deletion runs this again, and the
/// deduplication key keeps it to one mail.
/// </remarks>
public sealed partial class QnAAccountDeletionHandler : ISparkAccountDeletionHandler<SparkUser>
{
    [Inject] private readonly ISparkMailer mailer;

    public async Task OnDeletingAccountAsync(SparkUser user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(user.Email))
            return;

        await mailer.SendAsync(new SparkMailRequest
        {
            Template = "AccountDeleted",
            To = user.Email,
            ToName = user.UserName,
            // Template variables are the data's member names verbatim (the shipped templates use snake_case).
            Data = new { user_name = user.UserName ?? user.Email },
            DeduplicationKey = $"account-deleted:{user.Id}",
        }, cancellationToken);
    }
}
