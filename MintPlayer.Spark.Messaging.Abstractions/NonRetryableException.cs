namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Exception that signals the message bus should dead-letter the message immediately
/// rather than retrying. Used for non-retryable errors like 400 Bad Request or 404 Not Found.
/// </summary>
public class NonRetryableException : Exception
{
    public NonRetryableException(string message) : base(message) { }
    public NonRetryableException(string message, Exception innerException) : base(message, innerException) { }
}
