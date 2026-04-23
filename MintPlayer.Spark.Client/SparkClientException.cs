using System.Net;

namespace MintPlayer.Spark.Client;

/// <summary>
/// Thrown by <see cref="SparkClient"/> (and any extension on top of it) when the server
/// returns a non-success HTTP status code. Call sites can inspect <see cref="StatusCode"/>
/// (e.g. <c>HttpStatusCode.Conflict</c> for optimistic-concurrency rejection,
/// <c>HttpStatusCode.NotFound</c> for missing or row-level-denied entities) and
/// <see cref="ResponseBody"/> for the server's error payload.
/// </summary>
public sealed class SparkClientException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ResponseBody { get; }

    public SparkClientException(HttpStatusCode statusCode, string? responseBody, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <summary>
    /// Throws a populated <see cref="SparkClientException"/> if <paramref name="response"/>
    /// is not a 2xx. Shared between <see cref="SparkClient"/>'s built-in methods and any
    /// third-party extension methods (e.g. <c>MintPlayer.Spark.Client.Authorization</c>)
    /// so the exception shape and message format stay consistent.
    /// </summary>
    public static async Task ThrowIfNotSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new SparkClientException(
            response.StatusCode,
            body,
            $"Spark request failed with {(int)response.StatusCode} {response.StatusCode}: {Truncate(body, 500)}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
