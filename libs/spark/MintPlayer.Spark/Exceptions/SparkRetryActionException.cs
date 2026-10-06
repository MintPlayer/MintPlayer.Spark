using MintPlayer.Spark.Abstractions;
using System.Text.Json;

namespace MintPlayer.Spark.Exceptions;

internal sealed class SparkRetryActionException : Exception
{
    public int Step { get; }
    public string Title { get; }
    public string[] Options { get; }
    public string? DefaultOption { get; }
    public PersistentObject? PersistentObject { get; }
    public string? RetryMessage { get; }

    /// <summary>The client method of an <c>IRetryAccessor.Invoke</c> step; null for an ordinary prompt.</summary>
    public string? ClientMethod { get; }

    /// <summary>The client method's serialized argument; null when there is none.</summary>
    public JsonElement? Arguments { get; }

    public SparkRetryActionException(
        int step,
        string title,
        string[] options,
        string? defaultOption,
        PersistentObject? persistentObject,
        string? message,
        string? clientMethod = null,
        JsonElement? arguments = null)
        : base($"Retry action requested at step {step}: {title}")
    {
        Step = step;
        Title = title;
        Options = options;
        DefaultOption = defaultOption;
        PersistentObject = persistentObject;
        RetryMessage = message;
        ClientMethod = clientMethod;
        Arguments = arguments;
    }
}
