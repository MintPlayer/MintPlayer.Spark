namespace MintPlayer.Spark.Abstractions.ClientInstructions;

/// <summary>
/// Blocking instruction — the action cannot proceed without user input. The frontend
/// opens a retry modal; user selects an option; original request is resubmitted with
/// the accumulated <c>retryResults[]</c>. Pushed by <c>IRetryAccessor.Action(...)</c>
/// before it throws.
/// </summary>
public sealed class RetryInstruction : ClientInstruction
{
    public required int Step { get; init; }
    public required string Title { get; init; }
    public required string[] Options { get; init; }
    public string? DefaultOption { get; init; }
    public PersistentObject? PersistentObject { get; init; }
    public string? Message { get; init; }
}
