namespace MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Configuration;

internal class SmeeOptions
{
    public string ChannelUrl { get; set; } = string.Empty;
    public string LocalWebhookPath { get; set; } = "/api/github/webhooks";
}
