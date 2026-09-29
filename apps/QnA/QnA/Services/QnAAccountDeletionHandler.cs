using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.MailManager;

namespace QnA.Services;

/// <summary>
/// QnA's part of an account deletion (#460 D8): the posts stay — other people's answers and votes hang
/// on them, and <c>AuthorId</c> is an id that renders as a deleted user once the account is gone — and
/// the user gets a goodbye mail through MailManager (the app template <c>Templates/Mail/AccountDeleted</c>).
/// Moderation's own <see cref="ISparkAccountDeletionHandler{TUser}"/> removes the votes, flags and
/// profile before the delete.
/// </summary>
/// <remarks>
/// An <see cref="ISparkAccountDeletedHandler{TUser}"/>: it runs only after the store committed the
/// delete, so a deletion stopped by a handler or refused by the store never mails a goodbye to a live
/// account. The deduplication key keeps it to one mail.
/// </remarks>
public sealed partial class QnAAccountDeletionHandler : ISparkAccountDeletedHandler<SparkUser>
{
    [Inject] private readonly ISparkMailer mailer;

    public async Task OnAccountDeletedAsync(SparkUser user, CancellationToken cancellationToken)
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
