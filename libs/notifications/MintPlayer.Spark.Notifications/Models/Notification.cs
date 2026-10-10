using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Notifications.Models;

/// <summary>
/// One in-app notification for one user (<c>docs/seo_analyzer_PRD.md</c> X10). Written by
/// <see cref="ISparkNotifier"/>, read only by its user (the package's row rule), shown live through the
/// <c>"live": true</c> type and queries (X4): there is no second channel.
/// </summary>
public class Notification : IExpiring
{
    public string? Id { get; set; }

    /// <summary>The user it is for (<c>SparkUsers/…</c>).</summary>
    public string? UserId { get; set; }

    /// <summary>The organization it is about, which also picks the user's preferences; null for none.</summary>
    public string? OrganizationId { get; set; }

    /// <summary>A short headline (the push title and mail subject).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The text.</summary>
    public string? Body { get; set; }

    /// <summary>Where a click goes: an app-relative path (<c>/runs/42</c>) or an absolute URL of the app.</summary>
    public string? Url { get; set; }

    /// <summary>An application-defined kind (<c>score-drop</c>), for grouping and the push <c>Topic</c>.</summary>
    public string? Category { get; set; }

    public DateTime? CreatedAtUtc { get; set; }

    public bool IsRead { get; set; }

    public DateTime? ReadAtUtc { get; set; }

    /// <summary>X1: <c>"expiresAfter"</c> in the library's model (90 days); change it in your own model layer.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    internal static string NewId() => "Notifications/" + Guid.NewGuid().ToString("N");
}
