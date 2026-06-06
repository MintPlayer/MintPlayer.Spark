using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Exceptions;

internal sealed class SparkRetryActionException : Exception
{
    public int Step { get; }
    public string Title { get; }
    public string[] Options { get; }
    public string? DefaultOption { get; }
    public PersistentObject? PersistentObject { get; }
    public string? RetryMessage { get; }

    public SparkRetryActionException(
        int step,
        string title,
        string[] options,
        string? defaultOption,
        PersistentObject? persistentObject,
        string? message)
        : base($"Retry action requested at step {step}: {title}")
    {
        Step = step;
        Title = title;
        Options = options;
        DefaultOption = defaultOption;
        PersistentObject = persistentObject;
        RetryMessage = message;
    }
}
