using System.Text.Json;
using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

internal sealed class PersistentObjectRequest
{
    public Abstractions.PersistentObject? PersistentObject { get; set; }
    public RetryResult? RetryResult { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Reads the request body, supporting both:
    /// - Wrapper format: { "persistentObject": {...}, "retryResult": {...} }
    /// - Flat format: a direct PersistentObject (backwards compatible)
    /// </summary>
    public static async Task<(Abstractions.PersistentObject PersistentObject, RetryResult? RetryResult)> ReadAsync(HttpRequest request)
    {
        var json = await request.ReadFromJsonAsync<JsonElement>();

        if (json.TryGetProperty("persistentObject", out _))
        {
            var wrapper = json.Deserialize<PersistentObjectRequest>(JsonOptions)!;
            return (
                wrapper.PersistentObject ?? throw new InvalidOperationException("PersistentObject is required."),
                wrapper.RetryResult
            );
        }

        var obj = json.Deserialize<Abstractions.PersistentObject>(JsonOptions)
            ?? throw new InvalidOperationException("PersistentObject could not be deserialized.");
        return (obj, null);
    }

    /// <summary>
    /// Reads just the RetryResult from the request body (for DELETE endpoints).
    /// Returns null if the body is empty or contains no retryResult.
    /// </summary>
    public static async Task<RetryResult?> ReadRetryResultAsync(HttpRequest request)
    {
        if (request.ContentLength is null or 0) return null;

        var json = await request.ReadFromJsonAsync<JsonElement>();

        if (json.TryGetProperty("retryResult", out var retryElement) && retryElement.ValueKind != JsonValueKind.Null)
        {
            return retryElement.Deserialize<RetryResult>(JsonOptions);
        }

        return null;
    }
}
