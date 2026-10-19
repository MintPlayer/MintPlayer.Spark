using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Commerce.Ticketing.Events;

/// <summary>
/// <c>Spark:Commerce:Ticketing:Events</c> (PRD §4.11). Code given to <c>AddTicketingEvents(o => …)</c> runs after the
/// section. Per-sale values (hold, per-buyer limit, refund policy, transfer, resale, queue mode) are chosen per event
/// within the channel's bounds (<c>Spark:Commerce:Channels:{name}</c>, D2), not here.
/// </summary>
public sealed class SparkTicketingEventsOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Commerce:Ticketing:Events";

    /// <summary>The channel a new event sells in when it names none. Default <c>tickets</c>; must be configured in <c>Spark:Commerce:Channels</c>.</summary>
    public string DefaultChannel { get; set; } = "tickets";

    /// <summary>The currency of a quick-created event's prices. Null takes <c>Spark:Commerce:DefaultCurrency</c>.</summary>
    public string? DefaultCurrency { get; set; }

    /// <summary>The SPA route of a public event page, with <c>{slug}</c>. Used by the sitemap, the meta tags, the <c>.ics</c> link and the JSON-LD offers. Default <c>/events/{slug}</c>.</summary>
    public string EventPagePath { get; set; } = "/events/{slug}";

    /// <summary>Whether published events are listed in <c>/sitemap.xml</c> (X22) and get server-rendered meta tags (X6). Default true.</summary>
    public bool PublicPages { get; set; } = true;

    /// <summary>Shards of a date's capacity quota at most (one per 250 places, at least one). Default 16.</summary>
    public int ShardsPerQuota { get; set; } = 16;

    /// <summary>The attendee questions' rules (GB3).</summary>
    public AttendeeAnswerOptions Answers { get; set; } = new();

    /// <summary>The PreSelectOrder rules (D25).</summary>
    public PreSelectOptions PreSelect { get; set; } = new();
}

/// <summary>Attendee answers (GB3): deadline, retention, exports.</summary>
public sealed class AttendeeAnswerOptions
{
    /// <summary>
    /// How long after an event's last date its answers are kept when the event sets no retention of its own. Default 30
    /// days (PRD §4.11: "erased after a seller-set retention (default: 30 days after the last date)").
    /// </summary>
    public TimeSpan DefaultRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>The longest retention a seller may set. Default 365 days (GDPR Art. 5(1)(e): no longer than necessary).</summary>
    public TimeSpan MaxRetention { get; set; } = TimeSpan.FromDays(365);

    /// <summary>How long before a date's start answers may still be given or changed when the event sets no deadline. Default 2 hours.</summary>
    public TimeSpan DefaultDeadlineBeforeStart { get; set; } = TimeSpan.FromHours(2);

    /// <summary>The retention job's schedule: six fields = seconds (NCrontab, UTC). Default hourly. Read at registration.</summary>
    public string RetentionSchedule { get; set; } = "0 0 * * * *";
}

/// <summary>PreSelectOrder (D25).</summary>
public sealed class PreSelectOptions
{
    /// <summary>Most lines one pre-selection may hold. Default 6.</summary>
    public int MaxLines { get; set; } = 6;

    /// <summary>Most ranked alternatives per line (the first choice included). Default 4.</summary>
    public int MaxAlternatives { get; set; } = 4;
}
