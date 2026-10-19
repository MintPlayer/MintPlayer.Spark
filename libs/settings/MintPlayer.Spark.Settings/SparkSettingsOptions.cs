using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Settings;

/// <summary>Options of the settings package, bound from <c>Spark:Settings</c>, then code.</summary>
public sealed class SparkSettingsOptions : ISparkConfigurationSection
{
    /// <summary>The configuration section the options are bound from.</summary>
    public static string SectionName => "Spark:Settings";

    /// <summary>
    /// How long a snapshot of the stored values is used before it is loaded again. Default 30 s: the
    /// longest another instance shows an old value. The saving instance drops its snapshot at once.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);
}
