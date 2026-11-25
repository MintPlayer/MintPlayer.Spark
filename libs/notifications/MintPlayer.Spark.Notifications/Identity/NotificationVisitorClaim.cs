using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Notifications.Models;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Notifications.Identity;

/// <summary>
/// A visitor who signs in keeps what was addressed to them (<c>docs/notification_bell_PRD.md</c> R7): every
/// notification of the browser's visitor id moves to the account, and the visitor cookies are cleared.
/// </summary>
/// <remarks>
/// <para>
/// Runs on every fresh sign-in (password, second factor, recovery code, external login, passkey; PRD Pass 3), in the
/// sign-in request, which still carries the visitor cookie. Without a visitor it does nothing.
/// </para>
/// <para>
/// The moved documents get the model's <c>expiresAfter</c> from now (90 days by default) instead of the visitor's
/// short lifetime, as if they had been addressed to the user. Idempotent: a retry finds no document left with the
/// visitor id.
/// </para>
/// <para>
/// Known limit: a job that was started as the visitor and finishes after the claim still addresses the visitor id,
/// whose cookie is gone; that notification is never shown and expires with the visitor's lifetime.
/// </para>
/// </remarks>
internal sealed partial class NotificationVisitorClaim : ISparkSignedInHandler
{
    internal const int PageSize = 512;

    [Inject] private readonly ISparkStore store;
    [Inject] private readonly ISparkCurrentPrincipal principal;
    [Inject] private readonly TimeProvider timeProvider;
    [Inject] private readonly IModelLoader? modelLoader;

    public async Task OnSignedInAsync(ISparkAccount user, CancellationToken cancellationToken)
    {
        if (principal.VisitorId is not { Length: > 0 } visitorId || user.Id is not { Length: > 0 } userId)
            return;

        var expiresAfter = modelLoader?.GetEntityTypeByClrType(typeof(Notification).FullName!)?.ExpiresAfter;
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Pages, waiting for the index so none is missed. Each page's documents leave the query once saved.
        for (var page = 0; page < 100; page++)
        {
            using var session = store.OpenSession();
            var batch = await session.ToListAsync(session.Query<Notification>(TimeSpan.FromSeconds(30)).Where(n => n.VisitorId == visitorId).Take(PageSize), cancellationToken);
            if (batch.Count == 0)
                break;
            foreach (var notification in batch)
            {
                notification.UserId = userId;
                notification.VisitorId = null;
                notification.ExpiresAtUtc = expiresAfter is { } after ? now + after : null;
            }
            await session.SaveChangesAsync(cancellationToken);
        }

        principal.ForgetVisitor();
    }
}
