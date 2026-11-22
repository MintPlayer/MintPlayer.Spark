namespace MintPlayer.Spark.Files.Services;

public enum FilesOutcome
{
    Ok,
    Invalid,
    Unauthorized,
    Forbidden,
    NotFound,
    TooLarge,
    TooManyRequests,
}

/// <summary>A service answer the endpoints turn into HTTP, without the service knowing about HTTP.</summary>
public sealed record FilesResult<T>(FilesOutcome Outcome, T? Value, string? Error)
{
    public static FilesResult<T> Ok(T value) => new(FilesOutcome.Ok, value, null);

    public static FilesResult<T> Fail(FilesOutcome outcome, string? error = null) => new(outcome, default, error);
}
