namespace MintPlayer.Spark.Notifications.Models;

/// <summary>
/// A user's channels for one organization (<c>docs/seo_analyzer_PRD.md</c> X10, preferences per organization).
/// A notification about organization O reaches its user through the channels of their preference for O,
/// else of their preference without an organization (their default), else the configured defaults
/// (<c>Spark:Notifications:Defaults</c>, all on). Id <c>NotificationPreferences/{userId}/{organizationId or "default"}</c>,
/// so the lookup is two loads by id.
/// </summary>
/// <remarks>
/// The organization is an opaque id here: the package does not reference Organizations, so an application
/// without organizations keeps one default row per user. An application with organizations can turn the
/// attribute into a reference to <c>Organization</c> in its own model layer.
/// </remarks>
public class NotificationPreference
{
    public string? Id { get; set; }

    public string? UserId { get; set; }

    /// <summary>The organization this row is for; null for the user's default.</summary>
    public string? OrganizationId { get; set; }

    /// <summary>Keep an in-app notification (the bell and the list).</summary>
    public bool InApp { get; set; } = true;

    /// <summary>Mail it (needs MailManager).</summary>
    public bool Email { get; set; } = true;

    /// <summary>Push it to every subscribed device (needs VAPID keys).</summary>
    public bool Push { get; set; } = true;

    public const string DefaultScope = "default";

    public static string IdFor(string userId, string? organizationId)
        => "NotificationPreferences/" + userId + "/" + (string.IsNullOrEmpty(organizationId) ? DefaultScope : organizationId);
}
