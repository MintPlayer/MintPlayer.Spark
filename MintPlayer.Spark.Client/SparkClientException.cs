using System.Net;

namespace MintPlayer.Spark.Client;

/// <summary>
/// Thrown by <see cref="SparkClient"/> when the server returns a non-success HTTP status code.
/// Tests and call sites can inspect <see cref="StatusCode"/> (e.g. <c>HttpStatusCode.Conflict</c>
/// for optimistic-concurrency rejection, <c>HttpStatusCode.NotFound</c> for missing or
/// row-level-denied entities) and <see cref="ResponseBody"/> for the server's error payload.
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
}
