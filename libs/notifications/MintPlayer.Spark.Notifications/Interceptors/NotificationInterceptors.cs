using System.Security.Claims;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Notifications.Models;

namespace MintPlayer.Spark.Notifications.Interceptors;

/// <summary>
/// The generic UI edits only the caller's own preferences: a create gets the caller as owner and the fixed id
/// <c>NotificationPreferences/{userId}/{organization or default}</c> (one row per organization, so a second
/// create for the same organization is a 400, not a duplicate), and an edit keeps owner and organization.
/// </summary>
internal sealed class NotificationPreferenceInterceptors : IBeforeSave<NotificationPreference>
{
    public async ValueTask OnBeforeSaveAsync(NotificationPreference entity, SaveContext context)
    {
        if (context.Before is NotificationPreference stored)
        {
            entity.UserId = stored.UserId;
            entity.OrganizationId = stored.OrganizationId;
            return;
        }

        if (context.IsSystemContext && !string.IsNullOrEmpty(entity.UserId))
        {
            entity.Id = NotificationPreference.IdFor(entity.UserId, entity.OrganizationId);
            return;
        }

        if (context.User?.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } userId)
            throw new SparkRowLevelAccessDeniedException("New/NotificationPreference");

        entity.UserId = userId;
        entity.OrganizationId = string.IsNullOrWhiteSpace(entity.OrganizationId) ? null : entity.OrganizationId.Trim();
        entity.Id = NotificationPreference.IdFor(userId, entity.OrganizationId);
        if (await context.GetSession().Advanced.ExistsAsync(entity.Id))
            throw new SparkValidationException(entity.OrganizationId is null
                ? "You already have default notification preferences: edit them instead."
                : "You already have notification preferences for this organization: edit them instead.");
    }
}

/// <summary>A notification's content is the server's: a client edit can change nothing but nothing is granted to edit either.</summary>
internal sealed class NotificationInterceptors : IBeforeSave<Notification>
{
    public ValueTask OnBeforeSaveAsync(Notification entity, SaveContext context)
    {
        if (context.IsSystemContext)
            return ValueTask.CompletedTask;
        if (context.Before is Notification stored)
        {
            // Read state is the only thing a user changes, and through /spark/notifications/read; a direct
            // edit (an application that grants Edit) keeps everything else as stored.
            entity.UserId = stored.UserId;
            entity.OrganizationId = stored.OrganizationId;
            entity.Title = stored.Title;
            entity.Body = stored.Body;
            entity.Url = stored.Url;
            entity.Category = stored.Category;
            entity.CreatedAtUtc = stored.CreatedAtUtc;
            entity.ReadAtUtc = entity.IsRead ? stored.ReadAtUtc ?? DateTime.UtcNow : null;
            return ValueTask.CompletedTask;
        }
        throw new SparkRowLevelAccessDeniedException("New/Notification");
    }
}
