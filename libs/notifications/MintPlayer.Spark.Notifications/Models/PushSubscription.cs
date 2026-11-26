using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Notifications.Models;

/// <summary>
/// One device's Web Push subscription (<c>docs/seo_analyzer_PRD.md</c> D30): the browser's
/// <c>PushSubscription.toJSON()</c>, owned by one user. Its id is
/// <c>PushSubscriptions/{userId}/{hash of the endpoint}</c>, so a user's devices are read by id prefix
/// (no index), and subscribing the same browser twice updates one document.
/// </summary>
[IdKind(SparkIdKind.Natural)]
public partial class PushSubscription
{
    public Id<PushSubscription> Id { get; set; }

    public string? UserId { get; set; }

    /// <summary>The push service URL. A capability: never shown in the UI.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The browser's P-256 key (base64url).</summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>The browser's authentication secret (base64url).</summary>
    public string Auth { get; set; } = string.Empty;

    /// <summary>The push service's host, shown so a user can tell devices apart (<c>fcm.googleapis.com</c>).</summary>
    public string? Service { get; set; }

    /// <summary>The User-Agent of the browser that subscribed, cut to 200 characters.</summary>
    public string? Device { get; set; }

    public DateTime? CreatedAtUtc { get; set; }


    /// <summary>The prefix of every device of <paramref name="userId"/>. Devices stored before #536 keep their mixed casing; RavenDB ids ignore case.</summary>
    public static string PrefixFor(string userId) => "pushsubscriptions/" + userId + "/";

    public static Id<PushSubscription> IdFor(string userId, string endpoint)
        => Id<PushSubscription>.FromTrusted(PrefixFor(userId) + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)))[..32]);
}
