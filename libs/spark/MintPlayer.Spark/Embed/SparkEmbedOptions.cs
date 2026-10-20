using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Embed;

/// <summary><c>Spark:Embed</c> (commerce PRD X23, GB8). <c>AddEmbed</c> switches it on; its code runs after the section.</summary>
public sealed class SparkEmbedOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Embed";

    /// <summary>Embed mode. Off (the default): no framing header is touched, the hand-over endpoints answer 404.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The organisers' sites allowed to frame the embed routes: <c>https://organiser.example</c>, or
    /// <c>https://*.organiser.example</c> for every subdomain. <c>http://</c> is accepted for <c>localhost</c> only. Checked
    /// at startup: a malformed entry refuses the app (it would end up in a <c>Content-Security-Policy</c> header).
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];

    /// <summary>The SPA routes that may be framed by <see cref="AllowedOrigins"/>.</summary>
    public List<string> PathPrefixes { get; set; } = ["/embed"];

    /// <summary>
    /// Every other HTML response gets <c>frame-ancestors 'self'</c> (clickjacking: only the embed routes are meant to be
    /// framed by other sites). Turn off only when the app sets its own framing policy.
    /// </summary>
    public bool DenyFramingElsewhere { get; set; } = true;

    /// <summary>How long a hand-over link stays valid.</summary>
    public TimeSpan HandoverLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The platform's public origin for hand-over links. Null means <c>Spark:Auth:PublicBaseUrl</c>, else (Development) the request's.</summary>
    public string? PublicOrigin { get; set; }
}
