using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Notifications;

/// <summary><c>Spark:Notifications</c> (<c>docs/seo_analyzer_PRD.md</c> X10). Code given to <c>AddNotifications(o => …)</c> runs after the section.</summary>
public sealed class SparkNotificationsOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Notifications";

    /// <summary>Web Push. Off until all three VAPID values are set.</summary>
    public SparkWebPushOptions WebPush { get; set; } = new();

    /// <summary>The channels of a user who saved no preference.</summary>
    public SparkNotificationChannels Defaults { get; set; } = new();

    /// <summary>The MailManager template of the notification mail; an app's own file of the same name wins.</summary>
    public string MailTemplate { get; set; } = SparkNotificationMailTemplates.Notification;
}

/// <summary><c>Spark:Notifications:WebPush</c>. The keys come from <c>VapidDetails.Generate</c>; the private key is a secret (user secrets, environment, a vault).</summary>
public sealed class SparkWebPushOptions
{
    /// <summary>The VAPID public key (base64url, 65 bytes): what the browser subscribes with.</summary>
    public string? PublicKey { get; set; }

    /// <summary>The VAPID private key (base64url, 32 bytes). Never in appsettings.json.</summary>
    public string? PrivateKey { get; set; }

    /// <summary><c>mailto:</c> or <c>https:</c> contact for the push services.</summary>
    public string? Subject { get; set; }

    /// <summary>How long a push service keeps an undelivered message. Default 1 day; at most 28 days.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// The push services a subscription may point at, as host names; a name also admits its subdomains. Null
    /// (the default) means <see cref="KnownPushServices"/>; a configured list replaces it. The endpoint comes from
    /// the browser, so without this list a signed-in user could make the server POST to any public URL (the
    /// SSRF-safe client already refuses private addresses).
    /// </summary>
    public List<string>? AllowedPushServices { get; set; }

    /// <summary>Devices kept per user; subscribing one more drops the oldest. Default 20.</summary>
    public int MaxDevicesPerUser { get; set; } = 20;

    /// <summary>Google (Chrome, Edge on Android), Mozilla (Firefox), Apple (Safari), Microsoft (Edge on Windows).</summary>
    public static readonly IReadOnlyList<string> KnownPushServices =
    [
        "fcm.googleapis.com",
        "push.services.mozilla.com",
        "push.apple.com",
        "notify.windows.com",
    ];

    /// <summary>Whether <paramref name="endpoint"/>'s host is an allowed push service.</summary>
    public bool IsAllowedService(Uri endpoint)
    {
        var host = endpoint.IdnHost.TrimEnd('.');
        return (AllowedPushServices ?? (IEnumerable<string>)KnownPushServices)
            .Select(s => s.Trim().TrimEnd('.'))
            .Any(s => s.Length > 0 && (host.Equals(s, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Whether all three VAPID values are present.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey) && !string.IsNullOrWhiteSpace(Subject);
}

/// <summary>Which channels a notification takes.</summary>
public sealed class SparkNotificationChannels
{
    public bool InApp { get; set; } = true;

    public bool Email { get; set; } = true;

    public bool Push { get; set; } = true;
}

/// <summary>The package's mail template names.</summary>
public static class SparkNotificationMailTemplates
{
    public const string Notification = "SparkNotifications/Notification";
}
