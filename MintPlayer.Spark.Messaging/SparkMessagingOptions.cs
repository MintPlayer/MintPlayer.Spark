namespace MintPlayer.Spark.Messaging;

public class SparkMessagingOptions
{
    public int MaxAttempts { get; set; } = 5;
    public TimeSpan FallbackPollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan[] BackoffDelays { get; set; } =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];
}
