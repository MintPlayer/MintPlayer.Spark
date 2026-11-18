using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Webhooks;

/// <summary>
/// The wire JSON shared by producer and receiver: camelCase, string enums, and <b>strict</b> numbers (D19). The
/// web defaults read numbers from strings, which makes every number's schema "string or number".
/// </summary>
public static class WebhookJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    /// <summary>A shared read-only instance.</summary>
    public static JsonSerializerOptions Options { get; } = Freeze(CreateOptions());

    private static JsonSerializerOptions Freeze(JsonSerializerOptions options)
    {
        options.TypeInfoResolver ??= new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>The Standard Webhooks body: <c>{ "type", "timestamp", "data" }</c>.</summary>
public sealed class WebhookEnvelope
{
    /// <summary>Dot-delimited event type, e.g. <c>report.completed</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>When the event happened (not when this attempt was sent).</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }

    /// <summary>Parses the body; <see langword="null"/> when it is not a Standard Webhooks envelope.</summary>
    public static WebhookEnvelope? TryParse(byte[] body)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<WebhookEnvelope>(body, WebhookJson.Options);
            return envelope is null || string.IsNullOrEmpty(envelope.Type) ? null : envelope;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
